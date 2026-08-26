#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 插件管理模块 (E3D Plugin Manager)
====================================
默认插件根目录: D:\\AVEVA\\Plugins
功能：
  1. 插件自动扫描与能力识别 (PMLLIB, PML.NET / bin, PMLUI / pdmsui, DFLTS, 表单与文档)
  2. 插件启用 / 禁用托管（自动写入 custom_evars.bat 的 SEP MANAGED PLUGINS 区块）
  3. PML 索引 (pml.index) 极速重构器（遍历 .pmlfrm, .pmlobj, .pmlfnc 自动生成标准索引）
  4. 插件骨架生成与目录管理
"""

import os
import re
import shutil
import subprocess
import sys

import e3d_store as store
import e3d_util as util

DEFAULT_PLUGINS_DIR = r"D:\AVEVA\Plugins"

PLUGINS_BLOCK_START = ':: >>> SEP MANAGED PLUGINS (do not edit) >>>'
PLUGINS_BLOCK_END = ':: <<< SEP MANAGED PLUGINS <<<'
PLUGINS_BLOCK_RE = re.compile(
    r'(?ms)^[ \t]*' + re.escape(PLUGINS_BLOCK_START) + r'.*?' + re.escape(PLUGINS_BLOCK_END) + r'[ \t]*\r?\n?'
)


def get_plugins_dir(data=None):
    """获取当前插件根目录（默认 D:\\AVEVA\\Plugins，支持在设置中自定义）。"""
    data = data or store.load_data()
    settings = data.get('settings') or {}
    p_dir = settings.get('plugins_dir') or DEFAULT_PLUGINS_DIR
    p_norm = util.normalize_path(p_dir)
    if p_norm and not os.path.exists(p_norm):
        try:
            os.makedirs(p_norm, exist_ok=True)
        except OSError:
            pass
    return p_norm or DEFAULT_PLUGINS_DIR


def set_plugins_dir(new_dir):
    """设置插件根目录。"""
    norm = util.normalize_path(new_dir)
    if not norm:
        raise ValueError('插件目录路径不能为空')
    os.makedirs(norm, exist_ok=True)
    data = store.load_data()
    if 'settings' not in data:
        data['settings'] = {}
    data['settings']['plugins_dir'] = norm
    store.save_data(data)
    return norm


def custom_file_path(local_dir=None):
    """获取本地 custom_evars.bat 路径。"""
    if not local_dir:
        import e3d_launcher as launcher
        local_dir = launcher.get_local_projects_dir()
    for name in ('custom_evars.bat', 'custom_evar.bat'):
        p = os.path.join(local_dir, name)
        if os.path.isfile(p):
            return p
    return os.path.join(local_dir, 'custom_evars.bat')


# ============================================================
# 插件扫描与能力识别
# ============================================================

def scan_plugin_folder(folder_path):
    """深度解析单个插件文件夹的能力与组件。"""
    folder_path = util.normalize_path(folder_path)
    if not os.path.isdir(folder_path):
        return None

    name = os.path.basename(folder_path)
    info = {
        'name': name,
        'path': folder_path,
        'has_pmllib': False,
        'pmllib_path': None,
        'has_pmlui': False,
        'pmlui_path': None,
        'has_pmlnet': False,
        'pmlnet_path': None,
        'has_dflts': False,
        'dflts_path': None,
        'has_pml_index': False,
        'pml_files_count': 0,
        'dll_files': [],
        'doc_file': None,
        'enabled': False,
    }

    try:
        entries = os.listdir(folder_path)
    except OSError:
        return info

    for entry in entries:
        sub = os.path.join(folder_path, entry)
        low = entry.lower()
        if os.path.isdir(sub):
            if low == 'pmllib':
                info['has_pmllib'] = True
                info['pmllib_path'] = sub
            elif low in ('pdmsui', 'pmlui'):
                info['has_pmlui'] = True
                info['pmlui_path'] = sub
            elif low == 'bin':
                info['has_pmlnet'] = True
                info['pmlnet_path'] = sub
                try:
                    dlls = [f for f in os.listdir(sub) if f.lower().endswith('.dll')]
                    info['dll_files'] = dlls
                except OSError:
                    pass
            elif low in ('dflts', 'defaults'):
                info['has_dflts'] = True
                info['dflts_path'] = sub
        elif os.path.isfile(sub):
            if low.startswith(('readme', 'install', '安装说明')) and low.endswith(('.md', '.txt', '.docx', '.doc')):
                info['doc_file'] = entry

    # 若根目录下直接存在 pmllib 相关文件但没有 pmllib 子文件夹
    if not info['has_pmllib']:
        try:
            pml_files = [f for f in entries if os.path.isfile(os.path.join(folder_path, f)) and f.lower().endswith(('.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac'))]
            if pml_files:
                info['has_pmllib'] = True
                info['pmllib_path'] = folder_path
                info['pml_files_count'] = len(pml_files)
        except OSError:
            pass

    # 统计 PMLLIB 详细信息
    if info['has_pmllib'] and info['pmllib_path']:
        pml_dir = info['pmllib_path']
        idx_file = os.path.join(pml_dir, 'pml.index')
        info['has_pml_index'] = os.path.isfile(idx_file)
        cnt = 0
        try:
            for root, _, files in os.walk(pml_dir):
                for f in files:
                    if f.lower().endswith(('.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac')):
                        cnt += 1
            info['pml_files_count'] = cnt
        except OSError:
            pass

    return info


def scan_plugins(plugins_dir=None, local_projects_dir=None):
    """扫描指定插件目录下的所有插件，并检查其当前启用状态。"""
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    if not p_dir or not os.path.isdir(p_dir):
        return []

    enabled_names = set(read_enabled_plugins(local_projects_dir))
    plugins = []
    try:
        for entry in sorted(os.listdir(p_dir)):
            sub = os.path.join(p_dir, entry)
            if os.path.isdir(sub) and not entry.startswith('.'):
                info = scan_plugin_folder(sub)
                if info:
                    info['enabled'] = info['name'] in enabled_names
                    plugins.append(info)
    except OSError:
        pass
    return plugins


# ============================================================
# custom_evars.bat 插件托管区读写
# ============================================================

def read_enabled_plugins(local_projects_dir=None):
    """读取 custom_evars.bat 中已启用的插件名称列表。"""
    c_path = custom_file_path(local_projects_dir)
    if not os.path.isfile(c_path):
        return []
    try:
        text, _ = util.read_text_smart(c_path)
    except OSError:
        return []

    enabled = set()
    # 1. 从托管区解析
    m = PLUGINS_BLOCK_RE.search(text)
    if m:
        block = m.group(0)
        for line in block.splitlines():
            m_pml = re.search(r'set\s+(?:PMLLIB|PMLNET|PMLUI)=[^\r\n]*?\\Plugins\\([^\\]+)', line, re.IGNORECASE)
            if m_pml:
                enabled.add(m_pml.group(1))

    # 2. 从外部显式 set 解析 (兼容用户原有手写 D:\AVEVA\Plugins\xxx)
    for line in text.splitlines():
        if line.strip().startswith('rem') or line.strip().startswith('::'):
            continue
        m_pml = re.search(r'set\s+(?:PMLLIB|PMLNET|PMLUI)=[^\r\n]*?\\Plugins\\([^\\]+)', line, re.IGNORECASE)
        if m_pml:
            enabled.add(m_pml.group(1))

    return sorted(list(enabled))


def _generate_plugins_block(enabled_plugins, plugins_dir=None):
    """生成插件托管区块内容。"""
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    lines = [PLUGINS_BLOCK_START]
    if not enabled_plugins:
        lines.append(PLUGINS_BLOCK_END)
        return '\r\n'.join(lines)

    for p in enabled_plugins:
        p_path = os.path.join(p_dir, p)
        info = scan_plugin_folder(p_path)
        if not info:
            continue

        lines.append(f'rem --- E3D Plugin: {p} ---')
        # 1. PMLLIB
        if info['has_pmllib'] and info['pmllib_path']:
            lines.append(f'if exist "{info["pmllib_path"]}" set PMLLIB=%PMLLIB%;{info["pmllib_path"]}')
        # 2. PMLUI / PDMSUI
        if info['has_pmlui'] and info['pmlui_path']:
            lines.append(f'if exist "{info["pmlui_path"]}" set PMLUI=%PMLUI%;{info["pmlui_path"]}')
        # 3. PMLNET
        if info['has_pmlnet'] and info['pmlnet_path']:
            lines.append(f'if exist "{info["pmlnet_path"]}" set PMLNET=%PMLNET%;{info["pmlnet_path"]}')
        # 4. DFLTS
        if info['has_dflts'] and info['dflts_path']:
            lines.append(f'if exist "{info["dflts_path"]}" set AVEVA_DESIGN_DFLTS=%AVEVA_DESIGN_DFLTS%;{info["dflts_path"]}')

    lines.append(PLUGINS_BLOCK_END)
    return '\r\n'.join(lines)


def write_plugins_block(enabled_plugins, local_projects_dir=None, plugins_dir=None):
    """将启用的插件区块安全写入 local custom_evars.bat。"""
    import e3d_launcher as launcher
    local_dir = util.normalize_path(local_projects_dir or launcher.get_local_projects_dir())
    os.makedirs(local_dir, exist_ok=True)
    c_path = custom_file_path(local_dir)

    existed = os.path.exists(c_path)
    if existed:
        text, enc = util.read_text_smart(c_path)
    else:
        text, enc = '@echo off\r\n', util.default_bat_encoding()

    # 移除旧插件托管区块
    text = PLUGINS_BLOCK_RE.sub('', text)

    block = _generate_plugins_block(enabled_plugins, plugins_dir=plugins_dir)

    # 保持 SEP MANAGED PROJECTS 在最末尾，插件区块紧随其上
    managed_proj_re = re.compile(
        r'(?ms)^[ \t]*' + re.escape(launcher.MANAGED_START) + r'.*?' + re.escape(launcher.MANAGED_END) + r'[ \t]*\r?\n?'
    )
    proj_m = managed_proj_re.search(text)
    if proj_m:
        proj_block = proj_m.group(0).strip()
        text_before = managed_proj_re.sub('', text).rstrip('\r\n')
        text = text_before + '\r\n\r\n' + block + '\r\n\r\n' + proj_block + '\r\n'
    else:
        text = text.rstrip('\r\n') + '\r\n\r\n' + block + '\r\n'

    # 备份
    bak = c_path + '.sep.bak'
    if not os.path.exists(bak) and existed:
        shutil.copy2(c_path, bak)

    util.write_text_preserve(c_path, text, enc)
    return c_path


def set_plugin_enabled(name, enabled=True, local_projects_dir=None, plugins_dir=None):
    """启用或禁用指定名称的插件。"""
    current = set(read_enabled_plugins(local_projects_dir))
    if enabled:
        current.add(name)
    else:
        current.discard(name)
    write_plugins_block(sorted(list(current)), local_projects_dir=local_projects_dir, plugins_dir=plugins_dir)
    return enabled


# ============================================================
# PML 索引 (pml.index) 重构引擎
# ============================================================

def rebuild_pml_index(pmllib_path):
    """
    遍历指定 pmllib 目录，自动生成标准 AVEVA PML pml.index 文件。
    能够准确识别子目录路径和根目录文件，彻底解决 Form/Object/Function not found 错误。
    """
    pmllib_path = util.normalize_path(pmllib_path)
    if not os.path.isdir(pmllib_path):
        return False, '指定路径不是有效的目录'

    # 收集全部文件并按相对子目录归类
    # 结构: rel_dir -> [filename1, filename2]
    # 例: "/" -> ["cadaidbridge.pmlobj"], "/functions/" -> ["cad2e3ddiag.pmlfnc", ...]
    dir_files = {}

    pml_exts = {'.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac'}

    for root, _, files in os.walk(pmllib_path):
        valid_files = [f for f in files if os.path.splitext(f)[1].lower() in pml_exts]
        if not valid_files:
            continue

        rel = os.path.relpath(root, pmllib_path).replace('\\', '/')
        if rel == '.':
            key = '/'
        else:
            key = '/' + rel.strip('/') + '/'

        dir_files[key] = sorted(valid_files, key=lambda s: s.lower())

    if not dir_files:
        return False, '未在目录中找到任何 PML 文件 (.pmlfrm, .pmlobj, .pmlfnc, .pmlcmd, .pmlmac)'

    # 按照标准 PML 结构生成内容：先写非根目录，最后写根目录 /
    lines = []
    # 优先排序子目录，最后放根目录 /
    sorted_keys = sorted([k for k in dir_files.keys() if k != '/'])
    if '/' in dir_files:
        sorted_keys.append('/')

    for k in sorted_keys:
        lines.append(k)
        for f in dir_files[k]:
            lines.append(f)

    content = '\r\n'.join(lines) + '\r\n'
    idx_path = os.path.join(pmllib_path, 'pml.index')

    try:
        with open(idx_path, 'w', encoding='ascii', errors='replace') as f:
            f.write(content)
        return True, f'成功重构 pml.index (收录 {sum(len(v) for v in dir_files.values())} 个 PML 定义)'
    except OSError as e:
        return False, f'写入 pml.index 失败: {e}'


def rebuild_all_pml_indexes(plugins_dir=None):
    """一键为所有包含 pmllib 的插件重构 pml.index。"""
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    plugins = scan_plugins(p_dir)
    results = {}
    for p in plugins:
        if p['has_pmllib'] and p['pmllib_path']:
            ok, msg = rebuild_pml_index(p['pmllib_path'])
            results[p['name']] = {'ok': ok, 'message': msg}
    return results


def create_plugin_skeleton(name, plugins_dir=None, has_pmllib=True, has_pmlnet=True, has_pmlui=False):
    """在插件根目录下创建新的标准 E3D 插件目录骨架。"""
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    safe_name = re.sub(r'[^a-zA-Z0-9_\-]', '', name.strip())
    if not safe_name:
        raise ValueError('插件名称不合法')

    target_dir = os.path.join(p_dir, safe_name)
    if os.path.exists(target_dir):
        raise ValueError(f'插件目录 {safe_name} 已存在')

    os.makedirs(target_dir, exist_ok=True)
    if has_pmllib:
        pml_dir = os.path.join(target_dir, 'pmllib')
        os.makedirs(os.path.join(pml_dir, 'objects'), exist_ok=True)
        os.makedirs(os.path.join(pml_dir, 'forms'), exist_ok=True)
        os.makedirs(os.path.join(pml_dir, 'functions'), exist_ok=True)
        # 写一个样例函数
        sample_fnc = os.path.join(pml_dir, 'functions', f'{safe_name.lower()}_hello.pmlfnc')
        with open(sample_fnc, 'w', encoding='utf-8') as f:
            f.write(f'define function !{safe_name.lower()}_hello()\r\n  $P Hello from {safe_name} plugin!\r\nendfunction\r\n')
        rebuild_pml_index(pml_dir)

    if has_pmlnet:
        bin_dir = os.path.join(target_dir, 'bin')
        os.makedirs(bin_dir, exist_ok=True)

    if has_pmlui:
        ui_dir = os.path.join(target_dir, 'pdmsui')
        os.makedirs(ui_dir, exist_ok=True)

    readme = os.path.join(target_dir, 'README.md')
    with open(readme, 'w', encoding='utf-8') as f:
        f.write(f'# {safe_name} E3D 插件\r\n\r\n由 SEP 插件管理系统自动创建。\r\n')

    return target_dir


def open_plugins_folder(plugins_dir=None):
    """在文件资源管理器中打开插件根目录。"""
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    os.makedirs(p_dir, exist_ok=True)
    if sys.platform == 'win32':
        os.startfile(p_dir)
    return p_dir
