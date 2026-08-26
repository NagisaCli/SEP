#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 启动层
==========
两种项目配置机制：

方式 A（整库载入）：改写 evars.bat / evars.init 的 projects_dir= 指向项目库，
                   E3D 直接读取该库的 custom_evars.bat。
方式 B（项目追加）：把 call "路径\\evarsXXX.bat" 写入本地 custom_evars.bat 的
                   托管区，单项目启动或载入我的全部项目。

每次写入前备份，写入后回读验证，失败自动还原；启动通过 .lnk（os.startfile），
不弹出命令行窗口。
"""

import os
import re
import shutil
import subprocess
import sys

import e3d_store as store
import e3d_util as util


MANAGED_START = ':: >>> SEP MANAGED PROJECTS (do not edit) >>>'
MANAGED_END = ':: <<< SEP MANAGED PROJECTS <<<'
MANAGED_BLOCK_RE = re.compile(
    r'(?ms)^[ \t]*' + re.escape(MANAGED_START) + r'.*?' + re.escape(MANAGED_END) + r'[ \t]*\r?\n?'
)
SET_PROJECTS_RE = re.compile(rb'(?m)^(\s*(?:set\s+)?projects_dir=)[^\r\n]*', re.IGNORECASE)

EVARS_BAT = None
EVARS_INIT = None


class LauncherError(Exception):
    pass


# ============================================================
# E3D 路径解析
# ============================================================

def resolve_e3d(force=False, verbose=True):
    """检测/读取 E3D 安装路径，设置 EVARS_BAT / EVARS_INIT。"""
    global EVARS_BAT, EVARS_INIT
    util.add_script_path()
    try:
        from e3d_config import get_e3d_paths
    except Exception as e:
        if verbose:
            print(f'  [e3d_config] 导入失败: {e}')
        return False
    bat, init = get_e3d_paths(force=force, verbose=verbose)
    if bat and init:
        EVARS_BAT = bat
        EVARS_INIT = init
        return True
    return False


def get_evars_files():
    return EVARS_BAT, EVARS_INIT


def read_projects_dir(filepath):
    """读取 evars.bat / evars.init 中的 projects_dir 值（兼容 set 与非 set 写法）。"""
    if not filepath or not os.path.exists(filepath):
        return None
    enc = util.detect_encoding(filepath)
    try:
        with open(filepath, 'r', encoding=enc, errors='replace') as f:
            for line in f:
                m = re.match(r'^(?:set\s+)?projects_dir=(.*)$', line.strip(), re.IGNORECASE)
                if m:
                    val = m.group(1).strip()
                    return val.strip('"').strip("'")
    except OSError:
        return None
    return None


def get_local_projects_dir(data=None):
    """本地项目库路径：优先已配置且存在的路径，其次动态探测或默认路径。"""
    data = data or store.load_data()
    d = (data.get('settings') or {}).get('local_projects_dir') or ''
    if d and os.path.isdir(d):
        return util.normalize_path(d)

    # 尝试检测缓存
    cache = store.read_paths_cache()
    pd = cache.get('projects_dir')
    if pd and os.path.isdir(pd):
        return util.normalize_path(pd)

    # 尝试重新检测 E3D
    if resolve_e3d(force=False, verbose=False):
        cache = store.read_paths_cache()
        pd = cache.get('projects_dir')
        if pd and os.path.isdir(pd):
            return util.normalize_path(pd)

    d = util.normalize_path(d or r'D:\AVEVA\Projects\E3D3.1')
    if d and not os.path.isdir(d):
        try:
            os.makedirs(d, exist_ok=True)
        except OSError:
            pass
    return d


def custom_file_path(local_dir):
    """本地 custom_evars.bat（兼容 custom_evar.bat 拼写）。"""
    for name in ('custom_evars.bat', 'custom_evar.bat'):
        p = os.path.join(local_dir, name)
        if os.path.isfile(p):
            return p
    return os.path.join(local_dir, 'custom_evars.bat')


# ============================================================
# 方式 B：custom_evars.bat 托管区
# ============================================================

def managed_block(paths):
    lines = [MANAGED_START]
    for p in paths:
        safe_p = util.validate_safe_bat_path(p)
        lines.append(f'if exist "{safe_p}" call "{safe_p}"')
    lines.append(MANAGED_END)
    return '\r\n'.join(lines)


def read_managed(local_dir):
    """读取本地 custom_evars.bat 托管区中的项目路径列表。"""
    path = custom_file_path(local_dir)
    if not os.path.exists(path):
        return []
    try:
        text, _enc = util.read_text_smart(path)
    except OSError:
        return []
    m = MANAGED_BLOCK_RE.search(text)
    if not m:
        return []
    out = []
    for line in m.group(0).splitlines():
        mm = re.search(r'(?:if\s+exist\s+[^\r\n]+?\s+)?call\s+(?:"([^"]+)"|(\S+))', line, re.IGNORECASE)
        if mm:
            out.append(util.normalize_path(mm.group(1) or mm.group(2)))
    return out


def sanitize_evars_bat(bat_path):
    """自动修复目标 evars*.bat 中的失效主机名与硬编码网络路径。"""
    bat_path = util.normalize_path(bat_path)
    if not bat_path or not os.path.isfile(bat_path):
        return
    try:
        text, enc = util.read_text_smart(bat_path)
        new_text = util.sanitize_unc_paths(text, os.path.dirname(bat_path))
        if new_text != text:
            util.write_text_preserve(bat_path, new_text, enc)
    except Exception:
        pass


def ensure_library_custom_evars(library_dir):
    """
    整库载入前确保目标项目库具备自适应且无错误的主引导文件（custom_evars.bat）：
    1. 若已存在 custom_evars.bat：自动修复其中无法解析的电脑名/旧主机名；
    2. 若不存在 custom_evars.bat：自动根据子项目生成基于 %~dp0 的安全引导入口。
    """
    library_dir = util.normalize_path(library_dir)
    if not library_dir or not util.path_exists(library_dir, timeout=6):
        return

    custom_bat = os.path.join(library_dir, 'custom_evars.bat')
    if os.path.exists(custom_bat):
        try:
            text, enc = util.read_text_smart(custom_bat)
            new_text = util.sanitize_unc_paths(text, library_dir)
            if new_text != text:
                util.write_text_preserve(custom_bat, new_text, enc)
        except Exception:
            pass
        return

    # 若不存在，寻找下一层子项目自动生成基于 %~dp0 的安全调用入口
    try:
        subfolders = []
        for name in os.listdir(library_dir):
            sub = os.path.join(library_dir, name)
            if os.path.isdir(sub):
                for f in os.listdir(sub):
                    if f.lower().startswith('evars') and f.lower().endswith('.bat') and f.lower() != 'evars.bat':
                        subfolders.append((name, f))
                        break
        if subfolders:
            lines = [
                'rem --------------------------------------------------',
                'rem Auto-generated by SEP for E3D library mode',
                'rem --------------------------------------------------',
            ]
            for folder, bat in subfolders:
                lines.append(f'if exist "%~dp0{folder}\\{bat}" call "%~dp0{folder}\\{bat}"')
            lines.append('')
            util.write_text_preserve(custom_bat, '\r\n'.join(lines), util.default_bat_encoding())
    except Exception:
        pass



def write_managed(local_dir, paths):
    """
    把托管区写入本地 custom_evars.bat 末尾。
    幂等：先移除旧托管区，再追加新块，不触碰其他内容。
    返回写入的文件路径。
    """
    local_dir = util.normalize_path(local_dir)
    if not local_dir:
        raise LauncherError('本地项目库路径为空')
    try:
        os.makedirs(local_dir, exist_ok=True)
    except PermissionError:
        raise LauncherError(f'无法创建或访问本地项目库目录 {local_dir}：权限不足。')
    except OSError as e:
        raise LauncherError(f'无法创建本地项目库目录 {local_dir}: {e}')

    path = custom_file_path(local_dir)
    existed = os.path.exists(path)
    try:
        if existed:
            text, enc = util.read_text_smart(path)
        else:
            text, enc = '', util.default_bat_encoding()
    except PermissionError:
        raise LauncherError(f'无法读取 {path}：权限不足。')

    text = MANAGED_BLOCK_RE.sub('', text)
    try:
        block = managed_block(paths) + '\r\n'
    except ValueError as e:
        raise LauncherError(str(e))

    if text.strip():
        text = text.rstrip('\r\n') + '\r\n\r\n' + block
    else:
        text = '@echo off\r\n\r\n' + block

    try:
        util.write_text_preserve(path, text, enc)
    except PermissionError:
        raise LauncherError(f'无法写入 {path}：权限不足，请以管理员身份运行。')
    return path


# ============================================================
# 方式 A：projects_dir 改写
# ============================================================

def ensure_trailing_slash(p):
    p = util.normalize_path(p)
    if p and not p.endswith('\\'):
        p += '\\'
    return p


def set_projects_dir(filepath, new_path):
    """
    二进制安全替换 projects_dir= 后的路径。
    编码按原文件检测（UTF-8 / GBK），中文路径不乱码。
    返回备份路径（不存在则 None）。
    """
    if not os.path.exists(filepath):
        raise LauncherError(f'文件不存在: {filepath}')
    try:
        new_path_safe = util.validate_safe_bat_path(new_path)
    except ValueError as e:
        raise LauncherError(str(e))
    try:
        with open(filepath, 'rb') as f:
            data = f.read()
    except PermissionError:
        raise LauncherError(f'无法读取 {filepath}：权限不足，请尝试以管理员身份运行 SEP。')
    if not SET_PROJECTS_RE.search(data):
        raise LauncherError(f'{filepath} 中未找到 projects_dir= 行')

    enc = _bytes_encoding(data)
    if enc == 'utf-8-sig':
        enc = 'utf-8'  # BOM 保留在文件头，替换段不能再带 BOM
    new_value = ensure_trailing_slash(new_path_safe).encode(enc)
    new_data = SET_PROJECTS_RE.sub(lambda m: m.group(1) + new_value, data)

    backup = filepath + '.sep.bak'
    try:
        # 已有备份说明外层事务（write_mode）已经保存了初始内容，
        # 不能再覆盖：否则重试写入会把「改过一次」的内容当成原始内容，
        # 回滚时便再也回不到用户真正的初始配置。
        if not os.path.exists(backup):
            shutil.copy2(filepath, backup)
        with open(filepath, 'wb') as f:
            f.write(new_data)
    except PermissionError:
        raise LauncherError(f'无法写入 {filepath}：权限不足，请尝试以管理员身份运行 SEP。')
    return backup


def _bytes_encoding(data):
    if data.startswith(b'\xef\xbb\xbf'):
        return 'utf-8-sig'
    for enc in ('utf-8', 'gbk', 'latin-1'):
        try:
            data.decode(enc)
            return enc
        except (UnicodeDecodeError, UnicodeError):
            continue
    return 'utf-8'


# ============================================================
# 写入与验证
# ============================================================

def _backup(path):
    """
    备份文件。返回 True 表示原文件存在（回滚时应还原内容），
    False 表示原文件不存在（回滚时应删除写入过程中新建的文件）。
    残留的旧 .sep.bak（上次崩溃留下）会被清理，避免用陈旧内容覆盖。
    """
    bak = path + '.sep.bak'
    if os.path.exists(path):
        shutil.copy2(path, bak)
        return True
    if os.path.exists(bak):
        try:
            os.remove(bak)
        except OSError:
            pass
    return False


def _restore(path, existed=True):
    """
    回滚：原文件存在则还原备份；原文件本不存在则删除本次新建的文件，
    不把中途写了一半的内容留在磁盘上。
    """
    bak = path + '.sep.bak'
    if os.path.exists(bak):
        shutil.copy2(bak, path)
        try:
            os.remove(bak)
        except OSError:
            pass
        return
    if not existed and os.path.exists(path):
        try:
            os.remove(path)
        except OSError:
            pass


def _cleanup_backup(path):
    bak = path + '.sep.bak'
    if os.path.exists(bak):
        try:
            os.remove(bak)
        except OSError:
            pass


def _verify(expected_dir, expected_paths, local_dir):
    for f in (EVARS_BAT, EVARS_INIT):
        if not f:
            raise LauncherError('未检测到 E3D 安装路径（evars.bat / evars.init）')
        cur = read_projects_dir(f)
        if util.normalize_path(cur or '').lower() != util.normalize_path(expected_dir).lower():
            raise LauncherError(f'验证失败: {os.path.basename(f)} 中项目库路径与预期不一致')

    for p in expected_paths:
        if not util.path_exists(p, timeout=6):
            raise LauncherError(f'项目文件不可访问: {p}')


def write_mode(mode, payload):
    """
    按模式写入配置：
      single / temp : 将目标项目 bat 写入本地 custom_evars.bat 托管区，projects_dir 指向本地项目库
      all           : 将我的全部项目 bat 写入本地 custom_evars.bat 托管区，projects_dir 指向本地项目库
      library       : projects_dir 直接指向目标路径库根目录（方式 A 整库载入）
    返回摘要 dict。
    """
    data = store.load_data()
    local_dir = get_local_projects_dir(data)

    if mode in ('single', 'temp'):
        paths = [util.normalize_path(payload.get('bat_path', ''))]
        if not paths[0]:
            raise LauncherError('项目 bat 文件路径为空')
        projects_dir_target = local_dir
    elif mode == 'all':
        my = data.get('my_projects') or []
        if not my:
            raise LauncherError('我的项目为空，无法载入全部')
        paths = [util.normalize_path(p.get('bat_path', '')) for p in my]
        if not all(paths):
            raise LauncherError('我的项目中存在无效的 bat 文件路径')
        projects_dir_target = local_dir
    elif mode == 'library':
        paths = []
        projects_dir_target = util.normalize_path(payload.get('path', ''))
        if not projects_dir_target:
            raise LauncherError('路径库路径为空')
    else:
        raise LauncherError(f'未知启动模式: {mode}')

    if mode == 'library' and not util.path_exists(projects_dir_target, timeout=8):
        raise LauncherError(f'路径库不可访问: {projects_dir_target}')

    if mode == 'library':
        ensure_library_custom_evars(projects_dir_target)
    else:
        for p in paths:
            sanitize_evars_bat(p)

    if not EVARS_BAT or not EVARS_INIT:
        if not resolve_e3d(force=False, verbose=False):
            raise LauncherError('未检测到 E3D 安装路径（evars.bat / evars.init）')

    touched = [EVARS_BAT, EVARS_INIT]
    custom = custom_file_path(local_dir)
    # custom_evars.bat 即使当前不存在也要纳入事务：写入过程可能新建它，
    # 失败时必须删除，否则会残留一个半成品配置文件。
    if mode in ('single', 'temp', 'all') or os.path.exists(custom):
        touched.append(custom)
    existed = {f: _backup(f) for f in touched}

    try:
        if mode in ('single', 'temp', 'all'):
            write_managed(local_dir, paths)
        for f in (EVARS_BAT, EVARS_INIT):
            set_projects_dir(f, projects_dir_target)
        _verify(projects_dir_target, paths, local_dir)
    except Exception:
        for f in touched:
            _restore(f, existed.get(f, True))
        raise
    else:
        for f in touched:
            _cleanup_backup(f)

    data = store.load_data()
    data['settings']['last_mode'] = mode
    data['settings']['last_launched'] = payload.get('name') or ''
    store.save_data(data)

    return {
        'mode': mode,
        'projects_dir': projects_dir_target,
        'managed_paths': paths,
        'custom_evars': custom,
    }



# ============================================================
# E3D 启动
# ============================================================

def launch_e3d(lnk=''):
    """
    启动 E3D：
    优先解析快捷方式（.lnk）中的 TargetPath、Arguments 和 WorkingDirectory，
    以标准的 D:\\AVEVA\\USERDATA 工作目录唤起 mon.exe；
    支持快捷方式、可执行程序（mon.exe / launch.bat）及 os.startfile 兜底。
    """
    found = util.find_e3d_lnk(lnk)
    if not found:
        # 若未找到快捷方式，尝试直接探测 mon.exe 或 launch.bat
        if EVARS_BAT and os.path.exists(EVARS_BAT):
            e3d_dir = os.path.dirname(EVARS_BAT)
            mon_exe = os.path.join(e3d_dir, 'mon.exe')
            if os.path.isfile(mon_exe):
                found = mon_exe

    if not found:
        raise LauncherError('未找到 E3D 启动快捷方式或可执行文件（AVEVA Everything3D 3.1.lnk / mon.exe）')
    if sys.platform != 'win32':
        raise LauncherError('当前系统不是 Windows，无法启动 E3D')

    try:
        # 1. 优先使用 Windows Shell 原生机制 (os.startfile) 前台拉起快捷方式或可执行文件
        try:
            os.startfile(found)
            return found
        except Exception:
            pass

        # 2. 如果是 .lnk 快捷方式，使用 WScript.Shell 精准解析 Target、Arguments 与 WorkingDirectory
        if found.lower().endswith('.lnk'):
            try:
                import win32com.client
                shell = win32com.client.Dispatch('WScript.Shell')
                shortcut = shell.CreateShortCut(found)
                target = shortcut.TargetPath
                args = shortcut.Arguments
                workdir = shortcut.WorkingDirectory or r'D:\AVEVA\USERDATA'
                if not os.path.isdir(workdir):
                    workdir = os.path.dirname(target) if target else r'D:\AVEVA\USERDATA'
                if target and os.path.isfile(target):
                    cmd = f'"{target}" {args}' if args else f'"{target}"'
                    subprocess.Popen(cmd, cwd=workdir, shell=True)
                    return found
            except Exception:
                pass

        # 3. 如果是 mon.exe 直接启动
        if found.lower().endswith('mon.exe'):
            init_file = os.path.join(os.path.dirname(found), 'launch.init')
            args = f'PROD E3D init "{init_file}"' if os.path.isfile(init_file) else ''
            workdir = r'D:\AVEVA\USERDATA'
            if not os.path.isdir(workdir):
                workdir = os.path.dirname(found)
            cmd = f'"{found}" {args}' if args else f'"{found}"'
            subprocess.Popen(cmd, cwd=workdir, shell=True)
            return found

        return found
    except Exception as e:
        raise LauncherError(f'启动 E3D 失败: {e}')


def launch(mode, payload, lnk=''):
    """写入配置并启动 E3D。"""
    summary = write_mode(mode, payload)
    lnk_found = launch_e3d(lnk)
    summary['lnk'] = lnk_found
    summary['launched'] = True
    return summary
