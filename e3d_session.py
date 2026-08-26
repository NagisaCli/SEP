#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 项目实时连接数与在线协同会话追踪模块
========================================
1. 局域网/远程共享库在线心跳（.sep_sessions/）：跨设备实时感知谁在连接哪个项目；
2. 本机 E3D 运行进程与会话状态联动；
3. DABACON 数据库文件锁（*.lok, *.lck）与近期事务写入状态检测。
"""

import json
import os
import re
import socket
import sys
import threading
import time
from datetime import datetime, timezone

import e3d_util as util
import e3d_store as store

# 本机会话注册表
_ACTIVE_LOCAL_SESSIONS = {}
_HEARTBEAT_THREAD = None
_STOP_HEARTBEAT = threading.Event()
_LOCK = threading.Lock()

E3D_PROCESS_NAMES = {'mon.exe', 'design.exe', 'draw.exe', 'isodraft.exe', 'e3ddes.exe', 'e3d.exe'}


def get_current_computer_info():
    """获取当前计算机名称与当前登录用户名。"""
    comp = os.environ.get('COMPUTERNAME') or socket.gethostname() or 'Local-PC'
    user = os.environ.get('USERNAME') or os.environ.get('USER') or 'Engineer'
    return comp, user


def is_local_e3d_running():
    """检查本机当前是否正在运行 AVEVA E3D 主程序进程。"""
    if sys.platform != 'win32':
        return False
    try:
        import subprocess
        output = subprocess.check_output(['tasklist', '/FO', 'CSV', '/NH'], text=True, errors='ignore', timeout=1.5)
        for line in output.splitlines():
            row = line.split(',')
            if row:
                proc = row[0].strip(' "\'').lower()
                if proc in E3D_PROCESS_NAMES:
                    return True
    except Exception:
        pass
    return False


def get_project_session_dir(project_path):
    """获取项目对应的会话心跳存放目录（位于项目文件夹下的 .sep_sessions 目录）。"""
    if not project_path:
        return None
    p = util.normalize_path(project_path)
    if os.path.isfile(p):
        p_dir = os.path.dirname(p)
    else:
        p_dir = p
    return os.path.join(p_dir, '.sep_sessions')


def register_project_session(project_id, project_name, bat_path):
    """
    当启动某项目时，在本机及共享项目目录下注册一条活跃在线会话记录。
    """
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

    # 尝试写入共享项目目录下的 .sep_sessions/<session_id>.json
    ses_dir = get_project_session_dir(bat_path)
    if ses_dir:
        try:
            os.makedirs(ses_dir, exist_ok=True)
            ses_file = os.path.join(ses_dir, f"{session_id}.json")
            with open(ses_file, 'w', encoding='utf-8') as f:
                json.dump(session_info, f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    _ensure_heartbeat_worker()
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
        if ses_dir and os.path.isdir(ses_dir):
            ses_file = os.path.join(ses_dir, f"{s['session_id']}.json")
            if os.path.isfile(ses_file):
                try:
                    os.remove(ses_file)
                except Exception:
                    pass


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
            if ses_dir:
                ses_file = os.path.join(ses_dir, f"{s['session_id']}.json")
                if e3d_alive:
                    try:
                        os.makedirs(ses_dir, exist_ok=True)
                        with open(ses_file, 'w', encoding='utf-8') as f:
                            json.dump(s, f, ensure_ascii=False, indent=2)
                    except Exception:
                        pass
                else:
                    # E3D 已关闭，自动清理该会话文件
                    if os.path.isfile(ses_file):
                        try:
                            os.remove(ses_file)
                        except Exception:
                            pass


def _ensure_heartbeat_worker():
    global _HEARTBEAT_THREAD
    if _HEARTBEAT_THREAD is None or not _HEARTBEAT_THREAD.is_alive():
        _STOP_HEARTBEAT.clear()
        _HEARTBEAT_THREAD = threading.Thread(target=_heartbeat_loop, daemon=True)
        _HEARTBEAT_THREAD.start()


def inspect_project_connections(project_path, timeout=1.0):
    """
    深度探测单个项目的实时连接情况：
    1. 在线会话数与用户列表（来自 .sep_sessions/）
    2. 数据库文件锁与近期写入活跃度（来自 000 数据库目录）
    """
    if not project_path:
        return {'online_count': 0, 'sessions': [], 'has_locks': False, 'is_active': False, 'is_local_running': False}

    norm_path = util.normalize_path(project_path)
    if os.path.isfile(norm_path):
        p_dir = os.path.dirname(norm_path)
    else:
        p_dir = norm_path

    current_comp, current_user = get_current_computer_info()
    sessions = []
    has_locks = False
    lock_files = []
    last_db_write_time = None
    is_active_recently = False

    # 1. 扫描 .sep_sessions
    ses_dir = os.path.join(p_dir, '.sep_sessions')
    if os.path.isdir(ses_dir):
        now_ts = time.time()
        try:
            for f in os.listdir(ses_dir):
                if f.endswith('.json'):
                    fp = os.path.join(ses_dir, f)
                    try:
                        with open(fp, 'r', encoding='utf-8') as sf:
                            data = json.load(sf)
                        # 解析心跳时间戳（超过 180 秒无心跳视为离线并尝试清理）
                        hb_str = data.get('last_heartbeat') or data.get('started_at') or ''
                        is_stale = True
                        if hb_str:
                            try:
                                dt = datetime.fromisoformat(hb_str.replace('Z', '+00:00'))
                                if (now_ts - dt.timestamp()) < 180:
                                    is_stale = False
                            except Exception:
                                is_stale = False

                        if not is_stale:
                            data['is_current_device'] = (data.get('computer_name', '').lower() == current_comp.lower())
                            sessions.append(data)
                        else:
                            # 尝试清理过期心跳
                            try:
                                os.remove(fp)
                            except Exception:
                                pass
                    except Exception:
                        pass
        except OSError:
            pass

    # 2. 检查 000 数据库目录中的锁文件与修改时间
    proj_code = os.path.basename(p_dir)
    # 常见 000 目录如 FOX000, XXX000, OWL000, 000
    candidate_000 = [
        os.path.join(p_dir, f"{proj_code}000"),
        os.path.join(p_dir, f"{proj_code.upper()}000"),
        os.path.join(p_dir, "000"),
        p_dir,
    ]
    db_000_dir = None
    for c in candidate_000:
        if os.path.isdir(c):
            db_000_dir = c
            break

    if db_000_dir:
        try:
            entries = os.listdir(db_000_dir)
            now_ts = time.time()
            for entry in entries:
                fp = os.path.join(db_000_dir, entry)
                ext = os.path.splitext(entry)[1].lower()
                if ext in ('.lok', '.lck', '.tmp', '.lock'):
                    has_locks = True
                    lock_files.append(entry)

                # 检查数据库文件（如 sys, com, *0001, *.db*）最近活跃写入
                if not os.path.isdir(fp):
                    try:
                        mtime = os.path.getmtime(fp)
                        if (now_ts - mtime) < 600:  # 10 分钟内有写操作
                            is_active_recently = True
                        if last_db_write_time is None or mtime > last_db_write_time:
                            last_db_write_time = mtime
                    except OSError:
                        pass
        except OSError:
            pass

    # 3. 检查本机活跃会话内存表
    with _LOCK:
        for sid, sinfo in _ACTIVE_LOCAL_SESSIONS.items():
            if util.normalize_path(sinfo.get('bat_path', '')).lower() == norm_path.lower():
                if not any(s.get('session_id') == sinfo.get('session_id') for s in sessions):
                    sinfo_copy = dict(sinfo)
                    sinfo_copy['is_current_device'] = True
                    sessions.append(sinfo_copy)

    is_local = any(s.get('is_current_device') for s in sessions)

    last_write_str = None
    if last_db_write_time:
        try:
            last_write_str = datetime.fromtimestamp(last_db_write_time).strftime('%Y-%m-%d %H:%M')
        except Exception:
            pass

    return {
        'online_count': len(sessions),
        'sessions': sessions,
        'has_locks': has_locks,
        'lock_files': lock_files,
        'is_active_recently': is_active_recently,
        'last_db_write_time': last_write_str,
        'is_local_running': is_local,
    }


def batch_inspect_sessions(projects_list):
    """批量探测一组项目的在线会话与连接数。"""
    res = {}
    for p in projects_list:
        pid = p.get('id')
        bat = p.get('bat_path')
        if pid and bat:
            res[pid] = inspect_project_connections(bat)
    return res
