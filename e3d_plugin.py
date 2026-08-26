#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP — E3D 插件管理系统核心模块
========================================
负责 AVEVA Everything3D 插件的自动扫描、能力识别、
custom_evars.bat 独立托管区块注入、pml.index 极速索引重构、
PML.NET 程序集与 Ribbon UIC 定制识别，以及运行时热装载/热卸载宏生成。

默认插件根目录：D:\\AVEVA\\Plugins
"""

import os
import re
import shutil
import sys

import e3d_store as store
import e3d_util as util

PLUGINS_BLOCK_START = ':: >>> SEP MANAGED PLUGINS (do not edit) >>>'
PLUGINS_BLOCK_END = ':: <<< SEP MANAGED PLUGINS <<<'

PLUGINS_BLOCK_RE = re.compile(
    r'(?ms)^[ \t]*' + re.escape(PLUGINS_BLOCK_START) + r'.*?' + re.escape(PLUGINS_BLOCK_END) + r'[ \t]*\r?\n?'
)

DEFAULT_PLUGINS_DIR = r"D:\AVEVA\Plugins"


# ============================================================
# 基础路径与配置读取
# ============================================================

def get_plugins_dir():
    """获取当前配置的插件根目录，默认 D:\\AVEVA\\Plugins。"""
    data = store.load_data()
    settings = data.get('settings') or {}
    p = settings.get('plugins_dir')
    if p and os.path.isdir(p):
        return util.normalize_path(p)
    return util.normalize_path(DEFAULT_PLUGINS_DIR)


def set_plugins_dir(path):
    """设置并持久化插件根目录。"""
    norm = util.normalize_path(path)
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

def _extract_form_name_from_file(file_path):
    """从 .pmlfrm 文件中提取 setup form !!<Name> 的真实表单名。"""
    try:
        with open(file_path, 'r', encoding='latin-1', errors='ignore') as f:
            for _ in range(50):
                line = f.readline()
                if not line:
                    break
                m = re.search(r'setup\s+form\s+!!([a-zA-Z0-9_]+)', line, re.IGNORECASE)
                if m:
                    return m.group(1)
    except Exception:
        pass
    base = os.path.basename(file_path)
    return os.path.splitext(base)[0]


def scan_plugin_folder(folder_path):
    """
    深度解析单个插件文件夹的能力与组件：
    - PMLLIB (forms, objects, functions, macros)
    - PML.NET (C# DLL assemblies)
    - PMLUI / PDMSUI (自定义应用与菜单)
    - UIC (Ribbon 功能区界面定制)
    - DFLTS (默认配置)
    - 入口调用指令 (Entry Commands, 如 show !!nozSpecMgr)
    - 运行时热装载脚本 (Hot-load snippet)
    """
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
        'has_uic': False,
        'uic_files': [],
        'has_dflts': False,
        'dflts_path': None,
        'has_pml_index': False,
        'pml_files_count': 0,
        'forms_count': 0,
        'objects_count': 0,
        'functions_count': 0,
        'macros_count': 0,
        'dll_files': [],
        'doc_file': None,
        'entry_commands': [],
        'hotload_cmds': [],
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
                    dlls = [f for f in os.listdir(sub) if f.lower().endswith('.dll') and not f.lower().endswith('.old')]
                    info['dll_files'] = sorted(dlls)
                except OSError:
                    pass
            elif low in ('dflts', 'defaults'):
                info['has_dflts'] = True
                info['dflts_path'] = sub
        elif os.path.isfile(sub):
            if low.startswith(('readme', 'install', '安装说明')) and low.endswith(('.md', '.txt', '.docx', '.doc')):
                info['doc_file'] = entry
            elif low.endswith(('.uic', '.xml')) and ('uic' in low or 'ribbon' in low or low.endswith('.uic')):
                info['has_uic'] = True
                info['uic_files'].append(entry)

    # 检查是否有 install/ 下的 doc / uic
    install_dir = os.path.join(folder_path, 'install')
    if os.path.isdir(install_dir):
        try:
            for f in os.listdir(install_dir):
                low_f = f.lower()
                if not info['doc_file'] and low_f.startswith(('readme', 'install', '安装说明')):
                    info['doc_file'] = os.path.join('install', f)
                if low_f.endswith(('.uic', '.xml')) and ('uic' in low_f or 'ribbon' in low_f or low_f.endswith('.uic')):
                    info['has_uic'] = True
                    info['uic_files'].append(os.path.join('install', f))
        except OSError:
            pass

    # 若根目录下直接存在 pmllib 相关文件但没有 pmllib 子文件夹
    if not info['has_pmllib']:
        try:
            pml_files = [f for f in entries if os.path.isfile(os.path.join(folder_path, f)) and f.lower().endswith(('.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac'))]
            if pml_files:
                info['has_pmllib'] = True
                info['pmllib_path'] = folder_path
        except OSError:
            pass

    # 统计 PMLLIB 详细信息及提取入口调用命令
    forms = []
    objects = []
    functions = []
    macros = []
    entry_cmds = []

    if info['has_pmllib'] and info['pmllib_path']:
        pml_dir = info['pmllib_path']
        idx_file = os.path.join(pml_dir, 'pml.index')
        info['has_pml_index'] = os.path.isfile(idx_file)
        try:
            for root, dirs, files in os.walk(pml_dir):
                # 过滤掉备份与隐藏文件夹
                dirs[:] = [d for d in dirs if not d.startswith('.') and 'backup' not in d.lower() and 'bak' not in d.lower()]
                for f in files:
                    low = f.lower()
                    if low.endswith(('.bak', '.old', '.invalid')) or '.bak' in low:
                        continue
                    fp = os.path.join(root, f)
                    if low.endswith('.pmlfrm'):
                        form_name = _extract_form_name_from_file(fp)
                        forms.append(form_name)
                        entry_cmds.append(f'show !!{form_name}')
                    elif low.endswith('.pmlobj'):
                        obj_name = os.path.splitext(f)[0]
                        objects.append(obj_name)
                    elif low.endswith('.pmlfnc'):
                        fnc_name = os.path.splitext(f)[0]
                        functions.append(fnc_name)
                    elif low.endswith('.mac'):
                        macros.append(f)
        except OSError:
            pass

    info['forms_count'] = len(forms)
    info['objects_count'] = len(objects)
    info['functions_count'] = len(functions)
    info['macros_count'] = len(macros)
    info['pml_files_count'] = len(forms) + len(objects) + len(functions)

    # 针对 PML.NET 程序集提取导入命令
    for dll in info['dll_files']:
        dll_base = os.path.splitext(dll)[0]
        if dll_base.startswith(('System.', 'Microsoft.')):
            continue
        entry_cmds.append(f"import '{dll_base}'")

    # 针对典型函数与宏提取命令
    if not forms and functions:
        main_fnc = next((fn for fn in functions if name.lower() in fn.lower() or 'watch' in fn.lower() or 'main' in fn.lower()), functions[0])
        entry_cmds.append(f"{main_fnc}()")

    if not forms and not functions and macros:
        main_mac = next((m for m in macros if name.lower() in m.lower()), macros[0])
        entry_cmds.append(f"$m {main_mac}")

    info['entry_commands'] = entry_cmds

    # 生成单插件热加载指令
    hotloads = []
    if info['has_pmllib'] and info['pmllib_path']:
        hotloads.append(f"pml index '{info['pmllib_path']}'")
        hotloads.append("pml rehash all")
    for dll in info['dll_files']:
        dll_base = os.path.splitext(dll)[0]
        if not dll_base.startswith(('System.', 'Microsoft.')):
            hotloads.append(f"import '{dll_base}'")
    info['hotload_cmds'] = hotloads

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
            m_pml = re.search(r'set\s+(?:PMLLIB|PMLNET|PMLUI|AVEVA_DESIGN_DFLTS)=[^\r\n]*?\\Plugins\\([^\\]+)', line, re.IGNORECASE)
            if m_pml:
                enabled.add(m_pml.group(1))

    # 2. 从外部显式 set 解析 (兼容用户原有手写 D:\AVEVA\Plugins\xxx)
    for line in text.splitlines():
        if line.strip().startswith('rem') or line.strip().startswith('::'):
            continue
        m_pml = re.search(r'set\s+(?:PMLLIB|PMLNET|PMLUI|AVEVA_DESIGN_DFLTS)=[^\r\n]*?\\Plugins\\([^\\]+)', line, re.IGNORECASE)
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


def set_all_plugins_enabled(enabled=True, local_projects_dir=None, plugins_dir=None):
    """一键启用或禁用全部已发现的插件。"""
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    all_plugins = scan_plugins(p_dir, local_projects_dir)
    target = [p['name'] for p in all_plugins] if enabled else []
    write_plugins_block(target, local_projects_dir=local_projects_dir, plugins_dir=plugins_dir)
    return target


# ============================================================
# PML 索引 (pml.index) 重构引擎
# ============================================================

def rebuild_pml_index(pmllib_path):
    """
    遍历指定 pmllib 目录，自动生成标准 AVEVA PML pml.index 文件。
    能够准确识别子目录路径和根目录文件，过滤无害文件与临时备份，
    彻底解决 Form/Object/Function not found 错误。
    """
    pmllib_path = util.normalize_path(pmllib_path)
    if not os.path.isdir(pmllib_path):
        return False, '指定路径不是有效的目录'

    dir_files = {}
    pml_exts = {'.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac'}

    for root, dirs, files in os.walk(pmllib_path):
        # 过滤备份与隐藏文件夹
        dirs[:] = [d for d in dirs if not d.startswith('.') and 'backup' not in d.lower() and 'bak' not in d.lower()]
        valid_files = [
            f for f in files
            if os.path.splitext(f)[1].lower() in pml_exts
            and not f.lower().endswith(('.bak', '.old', '.invalid'))
            and '.bak' not in f.lower()
        ]
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


# ============================================================
# 运行时热装载宏生成器 (E3D Hot-Load Macro)
# ============================================================

def generate_hotload_macro(plugins_dir=None, local_projects_dir=None, output_path=None):
    """
    生成 AVEVA PML 运行时动态热装载宏文件 (load_all_plugins.mac)。
    在已经运行的 E3D 命令行中直接输入 $m D:\\AVEVA\\Plugins\\load_all_plugins.mac
    即可免重启即时装载全部启用的插件库、重构索引并导入 PML.NET 程序集。
    """
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    plugins = scan_plugins(p_dir, local_projects_dir)
    enabled_plugins = [p for p in plugins if p['enabled']]

    if not output_path:
        output_path = os.path.join(p_dir, 'load_all_plugins.mac')

    lines = [
        '-- ========================================================',
        '-- AVEVA E3D Plugin Dynamic Hot-Load Macro',
        '-- Auto-generated by SEP (Smart E3D Project Launcher)',
        '-- Usage in E3D: $m ' + output_path,
        '-- ========================================================',
        '',
    ]

    # 1. 重构/指定 PMLLIB 索引
    lines.append('-- 1. Index PML Libraries')
    for p in enabled_plugins:
        if p['has_pmllib'] and p['pmllib_path']:
            lines.append(f"pml index '{p['pmllib_path']}'")

    lines.append('')
    lines.append('-- 2. Rehash all PML definitions into memory')
    lines.append('pml rehash all')
    lines.append('')

    # 3. 导入 PML.NET 程序集
    lines.append('-- 3. Import PML.NET Assemblies')
    for p in enabled_plugins:
        for dll in p['dll_files']:
            dll_base = os.path.splitext(dll)[0]
            if not dll_base.startswith(('System.', 'Microsoft.')):
                lines.append(f"import '{dll_base}'")

    lines.append('')
    lines.append('$P ========================================================')
    lines.append(f'$P [SEP] {len(enabled_plugins)} E3D plugins successfully hot-loaded!')
    lines.append('$P Available Entry Commands:')
    for p in enabled_plugins:
        cmds = ', '.join(p['entry_commands']) if p['entry_commands'] else 'No direct form'
        lines.append(f"$P   * {p['name']}: {cmds}")
    lines.append('$P ========================================================')

    content = '\r\n'.join(lines) + '\r\n'
    try:
        with open(output_path, 'w', encoding='utf-8', errors='replace') as f:
            f.write(content)
        return output_path, content
    except OSError as e:
        raise OSError(f'生成热装载宏失败: {e}')


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
