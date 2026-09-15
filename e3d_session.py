#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 项目实时连接数与在线协同会话追踪模块
========================================
1. 局域网/远程共享库在线心跳（.sep_sessions/）：跨设备实时感知谁在连接哪个项目；
2. 本机 E3D 运行进程与会话状态联动；
3. DABACON 数据库文件锁（*.lok, *.lck）与近期事务写入状态检测。

性能约束：
- 所有磁盘探测都带超时（离线 UNC 路径绝不能拖住 HTTP 请求）；
- 批量探测并行执行，并维护一份快照缓存，接口立即返回上一次结果、后台刷新；
- 本机进程检测走 Toolhelp32 快照（ctypes），不再每 20 秒拉起 tasklist 子进程
  （打包为无控制台程序后，子进程会周期性闪出黑窗）。
"""

import ctypes
import json
import os
import subprocess
import sys
import threading
import time
from concurrent.futures import wait as futures_wait
from datetime import datetime

import e3d_util as util

# 本机会话注册表
_ACTIVE_LOCAL_SESSIONS = {}
_HEARTBEAT_THREAD = None
_STOP_HEARTBEAT = threading.Event()
_LOCK = threading.Lock()

E3D_PROCESS_NAMES = {'mon.exe', 'design.exe', 'draw.exe', 'isodraft.exe', 'e3ddes.exe', 'e3d.exe'}

# 单个项目探测的时间预算（秒）与批量探测的总预算
PROBE_TIMEOUT = 2.5
BATCH_BUDGET = 6.0
SNAPSHOT_TTL = 20.0
STALE_SECONDS = 180

_PROC_CACHE = util.TTLCache(ttl=5.0)
_SNAPSHOT = {'ts': 0.0, 'data': {}, 'refreshing': False}
_SNAPSHOT_LOCK = threading.Lock()
CREATE_NO_WINDOW = 0x08000000 if sys.platform == 'win32' else 0


def get_current_computer_info():
    """获取当前计算机名称与当前登录用户名。"""
    import socket
    comp = os.environ.get('COMPUTERNAME') or socket.gethostname() or 'Local-PC'
    user = os.environ.get('USERNAME') or os.environ.get('USER') or 'Engineer'
    return comp, user


def _running_process_names_win():
    """通过 Toolhelp32 快照枚举进程名（无子进程、无窗口、毫秒级）。"""
    TH32CS_SNAPPROCESS = 0x00000002

    class PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [
            ('dwSize', ctypes.c_ulong),
            ('cntUsage', ctypes.c_ulong),
            ('th32ProcessID', ctypes.c_ulong),
            ('th32DefaultHeapID', ctypes.POINTER(ctypes.c_ulong)),
            ('th32ModuleID', ctypes.c_ulong),
            ('cntThreads', ctypes.c_ulong),
            ('th32ParentProcessID', ctypes.c_ulong),
            ('pcPriClassBase', ctypes.c_long),
            ('dwFlags', ctypes.c_ulong),
            ('szExeFile', ctypes.c_wchar * 260),
        ]

    k32 = ctypes.windll.kernel32
    k32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
    k32.CreateToolhelp32Snapshot.argtypes = [ctypes.c_ulong, ctypes.c_ulong]
    k32.Process32FirstW.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    k32.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    k32.CloseHandle.argtypes = [ctypes.c_void_p]
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == ctypes.c_void_p(-1).value:
        raise OSError('CreateToolhelp32Snapshot failed')
    names = set()
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        if k32.Process32FirstW(snap, ctypes.byref(entry)):
            while True:
                names.add(entry.szExeFile.lower())
                if not k32.Process32NextW(snap, ctypes.byref(entry)):
                    break
    finally:
        k32.CloseHandle(snap)
    return names


def get_running_process_names():
    """获取本机正在运行的进程名集合（小写）。"""
    if sys.platform == 'win32':
        try:
            return _running_process_names_win()
        except Exception:
            pass
    return set()


def resolve_project_code_and_db_dir(bat_path):
    """
    根据 bat 脚本路径推导项目代号及 000 数据库目录。
    返回 (code, db_dir, root_dir)
    """
    bat_path = util.normalize_path(bat_path)
    root_dir = os.path.dirname(bat_path) if os.path.isfile(bat_path) else bat_path
    fname = os.path.basename(bat_path)

    code = ''
    import re
    m = re.match(r'^evars([A-Za-z0-9_-]{2,8})\.bat$', fname, re.I)
    if m:
        code = m.group(1).upper()
    else:
        # 从文件夹名猜测
        code = os.path.basename(root_dir).upper()

    db_dir = None
    if os.path.isfile(bat_path):
        try:
            with open(bat_path, 'r', encoding='utf-8', errors='ignore') as f:
                for line in f:
                    line = line.strip()
                    m_db = re.match(r'^set\s+([A-Za-z0-9_-]+000)=(.+)$', line, re.I)
                    if m_db:
                        target = m_db.group(2).strip().strip('"')
                        if target:
                            db_dir = util.normalize_path(target)
                            break
        except Exception:
            pass

    if not db_dir:
        candidates = [
            os.path.join(root_dir, f"{code}000"),
            os.path.join(root_dir, f"{code.upper()}000"),
            os.path.join(root_dir, "000"),
        ]
        for c in candidates:
            if os.path.isdir(c):
                db_dir = c
                break

    return code, db_dir, root_dir


def is_local_e3d_running():
    """检查本机当前是否正在运行 AVEVA E3D 主程序进程（结果缓存 5 秒）。"""
    if sys.platform != 'win32':
        return False
    cached = _PROC_CACHE.get('e3d')
    if cached is not None:
        return cached
    running = False
    try:
        running = bool(_running_process_names_win() & E3D_PROCESS_NAMES)
    except Exception:
        try:
            output = subprocess.check_output(
                ['tasklist', '/FO', 'CSV', '/NH'], text=True, errors='ignore',
                timeout=3, creationflags=CREATE_NO_WINDOW,
            )
            for line in output.splitlines():
                proc = line.split(',')[0].strip(' "\'').lower()
                if proc in E3D_PROCESS_NAMES:
                    running = True
                    break
        except Exception:
            running = False
    return _PROC_CACHE.set('e3d', running)


def get_project_session_dir(project_path):
    """获取项目对应的会话心跳存放目录（位于项目文件夹下的 .sep_sessions 目录）。"""
    if not project_path:
        return None
    p = util.normalize_path(project_path)
    p_dir = os.path.dirname(p) if p.lower().endswith('.bat') else p
    return os.path.join(p_dir, '.sep_sessions')


def _write_session_file(ses_dir, session_info):
    os.makedirs(ses_dir, exist_ok=True)
    ses_file = os.path.join(ses_dir, f"{session_info['session_id']}.json")
    tmp = ses_file + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(session_info, f, ensure_ascii=False)
    os.replace(tmp, ses_file)


def register_project_session(project_id, project_name, bat_path):
    """当启动某项目时，在本机及共享项目目录下注册一条活跃在线会话记录。"""
    comp, user = get_current_computer_info()
    now_iso = util.now_iso()
    session_id = util.gen_id('ses', f"{comp}_{user}_{project_id}")

    session_info = {
        'session_id': session_id,
        'project_id': project_id,
        'project_name': project_name,
        'bat_path': util.normalize_path(bat_path),
        'computer_name': comp,
        'user_name': user,
        'started_at': now_iso,
        'last_heartbeat': now_iso,
        'pid': os.getpid(),
    }

    with _LOCK:
        _ACTIVE_LOCAL_SESSIONS[project_id] = session_info

    ses_dir = get_project_session_dir(bat_path)
    if ses_dir:
        util.run_with_timeout(lambda: _write_session_file(ses_dir, session_info), PROBE_TIMEOUT * 2, None)

    _ensure_heartbeat_worker()
    invalidate_snapshot()
    return session_info


def unregister_project_session(project_id=None):
    """注销指定项目或本机的全部在线会话。"""
    with _LOCK:
        if project_id:
            targets = [(_ACTIVE_LOCAL_SESSIONS.pop(project_id, None))]
        else:
            targets = list(_ACTIVE_LOCAL_SESSIONS.values())
            _ACTIVE_LOCAL_SESSIONS.clear()

    for s in targets:
        if not s:
            continue
        ses_dir = get_project_session_dir(s.get('bat_path'))
        if ses_dir:
            ses_file = os.path.join(ses_dir, f"{s['session_id']}.json")
            util.run_with_timeout(lambda f=ses_file: os.path.isfile(f) and os.remove(f), PROBE_TIMEOUT, None)
    invalidate_snapshot()


def _heartbeat_loop():
    """后台保活线程：每 20 秒刷新活跃会话心跳，若 E3D 已退出则自动清理。"""
    while not _STOP_HEARTBEAT.wait(20):
        with _LOCK:
            active_items = list(_ACTIVE_LOCAL_SESSIONS.values())
        if not active_items:
            continue

        e3d_alive = is_local_e3d_running()
        now_iso = util.now_iso()
        for s in active_items:
            s['last_heartbeat'] = now_iso
            ses_dir = get_project_session_dir(s.get('bat_path'))
            if not ses_dir:
                continue
            if e3d_alive:
                util.run_with_timeout(lambda d=ses_dir, si=s: _write_session_file(d, si), PROBE_TIMEOUT * 2, None)
            else:
                ses_file = os.path.join(ses_dir, f"{s['session_id']}.json")
                util.run_with_timeout(lambda f=ses_file: os.path.isfile(f) and os.remove(f), PROBE_TIMEOUT, None)
        if not e3d_alive:
            # E3D 已关闭：本机会话全部失效
            with _LOCK:
                _ACTIVE_LOCAL_SESSIONS.clear()
            invalidate_snapshot()


def _ensure_heartbeat_worker():
    global _HEARTBEAT_THREAD
    if _HEARTBEAT_THREAD is None or not _HEARTBEAT_THREAD.is_alive():
        _STOP_HEARTBEAT.clear()
        _HEARTBEAT_THREAD = threading.Thread(target=_heartbeat_loop, daemon=True, name='sep-heartbeat')
        _HEARTBEAT_THREAD.start()


def _empty_result():
    return {
        'online_count': 0, 'sessions': [], 'has_locks': False, 'lock_files': [],
        'is_active_recently': False, 'last_db_write_time': None, 'is_local_running': False,
    }


def _inspect_impl(norm_path):
    p_dir = os.path.dirname(norm_path) if norm_path.lower().endswith('.bat') else norm_path
    current_comp, _ = get_current_computer_info()
    sessions = []
    lock_files = []
    last_db_write_time = None
    is_active_recently = False
    now_ts = time.time()

    # 1. 扫描 .sep_sessions
    ses_dir = os.path.join(p_dir, '.sep_sessions')
    try:
        entries = os.listdir(ses_dir)
    except OSError:
        entries = []
    for f in entries:
        if not f.endswith('.json'):
            continue
        fp = os.path.join(ses_dir, f)
        try:
            with open(fp, 'r', encoding='utf-8') as sf:
                data = json.load(sf)
        except Exception:
            continue
        hb_str = data.get('last_heartbeat') or data.get('started_at') or ''
        is_stale = True
        if hb_str:
            try:
                dt = datetime.fromisoformat(hb_str.replace('Z', '+00:00'))
                is_stale = (now_ts - dt.timestamp()) >= STALE_SECONDS
            except Exception:
                is_stale = False
        if is_stale:
            try:
                os.remove(fp)
            except Exception:
                pass
            continue
        data['is_current_device'] = (str(data.get('computer_name', '')).lower() == current_comp.lower())
        sessions.append(data)

    detection_sources = set()
    if sessions:
        detection_sources.add('sep_heartbeat')
    for s in sessions:
        s.setdefault('source', 'sep')

    # 2. 检查 000 数据库目录中的锁文件与修改时间
    proj_code, auto_db_dir, _ = resolve_project_code_and_db_dir(norm_path)
    if not proj_code:
        proj_code = os.path.basename(p_dir)
    candidate_000 = []
    if auto_db_dir and os.path.isdir(auto_db_dir):
        candidate_000.append(auto_db_dir)
    candidate_000.extend([
        os.path.join(p_dir, f"{proj_code}000"),
        os.path.join(p_dir, f"{proj_code.upper()}000"),
        os.path.join(p_dir, "000"),
        p_dir,
    ])
    for c in candidate_000:
        try:
            with os.scandir(c) as it:
                for entry in it:
                    try:
                        if entry.is_dir(follow_symlinks=False):
                            continue
                        ext = os.path.splitext(entry.name)[1].lower()
                        if ext in ('.lok', '.lck', '.tmp', '.lock'):
                            lock_files.append(entry.name)
                        mtime = entry.stat(follow_symlinks=False).st_mtime
                        if (now_ts - mtime) < 600:
                            is_active_recently = True
                        if last_db_write_time is None or mtime > last_db_write_time:
                            last_db_write_time = mtime
                    except OSError:
                        continue
            if lock_files or is_active_recently:
                break
        except OSError:
            continue

    if lock_files:
        detection_sources.add('dabacon_lock')
        import re
        for c in candidate_000:
            for lf in lock_files:
                lfp = os.path.join(c, lf)
                if os.path.isfile(lfp):
                    try:
                        with open(lfp, 'rb') as lff:
                            content = lff.read(512).decode('latin-1', errors='ignore')
                        m_u = re.search(r'([A-Za-z0-9_-]+)@([A-Za-z0-9_-]+)', content)
                        if m_u:
                            u_name, c_name = m_u.group(1), m_u.group(2)
                            sessions.append({
                                'session_id': f'lock_{lf}',
                                'user_name': u_name,
                                'computer_name': c_name,
                                'source': 'lock',
                                'last_heartbeat': util.now_iso(),
                                'is_current_device': (c_name.lower() == current_comp.lower()),
                            })
                    except Exception:
                        pass

    if is_active_recently:
        detection_sources.add('db_write')

    # 3. 本机活跃会话内存表
    with _LOCK:
        local_items = list(_ACTIVE_LOCAL_SESSIONS.values())
    for sinfo in local_items:
        if util.normalize_path(sinfo.get('bat_path', '')).lower() == norm_path.lower():
            if not any(s.get('session_id') == sinfo.get('session_id') for s in sessions):
                copy_ = dict(sinfo)
                copy_['is_current_device'] = True
                copy_.setdefault('source', 'local')
                sessions.append(copy_)

    # 对 sessions 按 (computer_name, user_name) 去重，sep 优先于 lock
    SOURCE_PRIORITY = {'sep': 10, 'local': 5, 'lock': 1}
    unique = {}
    for s in sessions:
        key = (str(s.get('computer_name', '')).lower(), str(s.get('user_name', '')).lower())
        if key not in unique or SOURCE_PRIORITY.get(s.get('source'), 0) > SOURCE_PRIORITY.get(unique[key].get('source'), 0):
            unique[key] = s
    final_sessions = list(unique.values())

    last_write_str = None
    if last_db_write_time:
        try:
            last_write_str = datetime.fromtimestamp(last_db_write_time).strftime('%Y-%m-%d %H:%M')
        except Exception:
            pass

    return {
        'online_count': len(final_sessions),
        'sessions': final_sessions,
        'has_locks': bool(lock_files),
        'lock_files': lock_files[:20],
        'is_active_recently': is_active_recently,
        'last_db_write_time': last_write_str,
        'is_local_running': any(s.get('is_current_device') for s in final_sessions),
        'detection_sources': list(detection_sources),
    }


def inspect_project_connections(project_path, timeout=PROBE_TIMEOUT):
    """
    深度探测单个项目的实时连接情况（带超时）：
    1. 在线会话数与用户列表（来自 .sep_sessions/）
    2. 数据库文件锁与近期写入活跃度（来自 000 数据库目录）
    """
    if not project_path:
        res = _empty_result()
        res['detection_sources'] = []
        return res
    norm_path = util.normalize_path(project_path)
    res = util.run_with_timeout(lambda: _inspect_impl(norm_path), timeout, None)
    if res is None:
        res = _empty_result()
        res['timed_out'] = True
        res['detection_sources'] = []
    return res


def batch_inspect_sessions(projects_list, budget=BATCH_BUDGET, use_cache=True):
    """并行探测一组项目的在线会话与连接数；支持缓存与预算控制。"""
    if use_cache:
        data, _ = get_sessions_snapshot(projects_list, max_age=SNAPSHOT_TTL, wait_first=True)
        return data

    pool = util._io_pool()
    futures = {}
    seen = set()
    for p in projects_list:
        pid = p.get('id')
        bat = p.get('bat_path')
        if not pid or not bat or pid in seen:
            continue
        seen.add(pid)
        norm = util.normalize_path(bat)
        futures[pool.submit(_inspect_impl, norm)] = pid

    res = {}
    if futures:
        done, _pending = futures_wait(list(futures.keys()), timeout=budget)
        previous = _SNAPSHOT.get('data') or {}
        for fut, pid in futures.items():
            if fut in done:
                try:
                    res[pid] = fut.result()
                except Exception:
                    res[pid] = _empty_result()
            else:
                fut.cancel()
                stale = dict(previous.get(pid) or _empty_result())
                stale['timed_out'] = True
                res[pid] = stale
    return res


def invalidate_snapshot():
    with _SNAPSHOT_LOCK:
        _SNAPSHOT['ts'] = 0.0


def get_sessions_snapshot(projects_list, max_age=SNAPSHOT_TTL, wait_first=True):
    """
    返回会话快照 {project_id: result}。
    - 快照新鲜：直接返回；
    - 快照过期：后台线程刷新，本次返回旧快照（首次且 wait_first=True 时同步等待一次）。
    """
    now = time.monotonic()
    with _SNAPSHOT_LOCK:
        fresh = (now - _SNAPSHOT['ts']) < max_age
        has_data = _SNAPSHOT['ts'] > 0
        refreshing = _SNAPSHOT['refreshing']
        if fresh or refreshing:
            return dict(_SNAPSHOT['data']), _SNAPSHOT['ts']
        _SNAPSHOT['refreshing'] = True

    def _refresh():
        try:
            data = batch_inspect_sessions(projects_list, use_cache=False)
            with _SNAPSHOT_LOCK:
                _SNAPSHOT['data'] = data
                _SNAPSHOT['ts'] = time.monotonic()
        finally:
            with _SNAPSHOT_LOCK:
                _SNAPSHOT['refreshing'] = False

    if wait_first and not has_data:
        _refresh()
    else:
        threading.Thread(target=_refresh, daemon=True, name='sep-sessions').start()
    with _SNAPSHOT_LOCK:
        return dict(_SNAPSHOT['data']), _SNAPSHOT['ts']
