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
import sys

import e3d_store as store
import e3d_util as util


MANAGED_START = ':: >>> SEP MANAGED PROJECTS (do not edit) >>>'
MANAGED_END = ':: <<< SEP MANAGED PROJECTS <<<'
MANAGED_BLOCK_RE = re.compile(
    r'(?ms)^[ \t]*' + re.escape(MANAGED_START) + r'.*?' + re.escape(MANAGED_END) + r'[ \t]*\r?\n?'
)
SET_PROJECTS_RE = re.compile(rb'(set\s+projects_dir=)[^\r\n]*', re.IGNORECASE)

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
    """读取 evars.bat / evars.init 中的 projects_dir 值。"""
    if not filepath or not os.path.exists(filepath):
        return None
    enc = util.detect_encoding(filepath)
    try:
        with open(filepath, 'r', encoding=enc, errors='replace') as f:
            for line in f:
                m = re.match(r'^set\s+projects_dir=(.*)$', line.strip(), re.IGNORECASE)
                if m:
                    return m.group(1).strip()
    except OSError:
        return None
    return None


def get_local_projects_dir(data=None):
    """本地项目库路径：优先配置，其次 E3D 检测缓存，最后默认路径。"""
    data = data or store.load_data()
    d = (data.get('settings') or {}).get('local_projects_dir') or ''
    if not d:
        d = store.read_paths_cache().get('projects_dir') or r'D:\AVEVA\Projects\E3D3.1'
    d = util.normalize_path(d)
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
        lines.append(f'call "{util.normalize_path(p)}"')
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
        mm = re.match(r'^\s*call\s+(?:"([^"]+)"|(\S+))', line, re.IGNORECASE)
        if mm:
            out.append(util.normalize_path(mm.group(1) or mm.group(2)))
    return out


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
    except OSError as e:
        raise LauncherError(f'无法创建本地项目库目录 {local_dir}: {e}')

    path = custom_file_path(local_dir)
    existed = os.path.exists(path)
    if existed:
        text, enc = util.read_text_smart(path)
    else:
        text, enc = '', util.default_bat_encoding()

    text = MANAGED_BLOCK_RE.sub('', text)
    block = managed_block(paths) + '\r\n'

    if text.strip():
        text = text.rstrip('\r\n') + '\r\n\r\n' + block
    else:
        text = '@echo off\r\n\r\n' + block

    util.write_text_preserve(path, text, enc)
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
    二进制安全替换 set projects_dir= 后的路径。
    编码按原文件检测（UTF-8 / GBK），中文路径不乱码。
    返回备份路径（不存在则 None）。
    """
    if not os.path.exists(filepath):
        raise LauncherError(f'文件不存在: {filepath}')
    with open(filepath, 'rb') as f:
        data = f.read()
    if not SET_PROJECTS_RE.search(data):
        raise LauncherError(f'{filepath} 中未找到 set projects_dir= 行')

    enc = _bytes_encoding(data)
    if enc == 'utf-8-sig':
        enc = 'utf-8'  # BOM 保留在文件头，替换段不能再带 BOM
    new_value = ensure_trailing_slash(new_path).encode(enc)
    new_data = SET_PROJECTS_RE.sub(lambda m: m.group(1) + new_value, data)

    backup = filepath + '.sep.bak'
    shutil.copy2(filepath, backup)
    with open(filepath, 'wb') as f:
        f.write(new_data)
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
    bak = path + '.sep.bak'
    if os.path.exists(path):
        shutil.copy2(path, bak)


def _restore(path):
    bak = path + '.sep.bak'
    if os.path.exists(bak):
        shutil.copy2(bak, path)
        os.remove(bak)


def _cleanup_backup(path):
    bak = path + '.sep.bak'
    if os.path.exists(bak):
        try:
            os.remove(bak)
        except OSError:
            pass


def _verify(expected_dir, expected_paths, local_dir):
    for f in (EVARS_BAT, EVARS_INIT):
        cur = read_projects_dir(f)
        if util.normalize_path(cur or '').lower() != util.normalize_path(expected_dir).lower():
            raise LauncherError(f'验证失败: {os.path.basename(f)} 中项目库路径与预期不一致')

    managed = read_managed(local_dir)
    expected = [util.normalize_path(p) for p in expected_paths]
    if managed != expected:
        raise LauncherError('验证失败: custom_evars.bat 托管区与预期不一致')

    for p in expected:
        if not util.path_exists(p, timeout=6):
            raise LauncherError(f'项目文件不可访问: {p}')


def write_mode(mode, payload):
    """
    按模式写入配置：
      single / temp : 托管区只写一个项目，projects_dir 指向本地库
      all           : 托管区写入我的全部项目，projects_dir 指向本地库
      library       : 清空托管区，projects_dir 指向整个路径库（方式 A）
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

    if not EVARS_BAT or not EVARS_INIT:
        if not resolve_e3d(force=False, verbose=False):
            raise LauncherError('未检测到 E3D 安装路径（evars.bat / evars.init）')

    touched = [EVARS_BAT, EVARS_INIT]
    custom = custom_file_path(local_dir)
    if os.path.exists(custom):
        touched.append(custom)
    for f in touched:
        _backup(f)

    try:
        write_managed(local_dir, paths)
        for f in (EVARS_BAT, EVARS_INIT):
            set_projects_dir(f, projects_dir_target)
        _verify(projects_dir_target, paths, local_dir)
    except Exception:
        for f in touched:
            _restore(f)
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
    """通过 .lnk 启动 E3D，不经过命令行。返回快捷方式路径。"""
    found = util.find_e3d_lnk(lnk)
    if not found:
        raise LauncherError('未找到 E3D 启动快捷方式（AVEVA Everything3D 3.1.lnk）')
    if sys.platform != 'win32':
        raise LauncherError('当前系统不是 Windows，无法启动 E3D')
    try:
        os.startfile(found)
    except Exception as e:
        raise LauncherError(f'启动 E3D 失败: {e}')
    return found


def launch(mode, payload, lnk=''):
    """写入配置并启动 E3D。"""
    summary = write_mode(mode, payload)
    lnk_found = launch_e3d(lnk)
    summary['lnk'] = lnk_found
    summary['launched'] = True
    return summary
