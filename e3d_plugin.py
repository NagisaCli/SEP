#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP — E3D 插件底层管理与诊断引擎
========================================
基于 AVEVA Everything3D 底层调用与寻址机制深度重构：
1. 逐文件深度解析：提取 Form(表单)、Object(对象及方法)、Function(函数)、Macro(宏)、
   PML.NET(程序集DLL)、UIC(Ribbon功能区定制)、PMLUI(模块重载) 的全部底层细节；
2. 全局符号与命名冲突检测：深度比对 %PMLLIB% 搜索链上的同名遮蔽与重复定义；
3. E3D 环境变量模拟器：可视化呈现 E3D 启动时实际接收的 PMLLIB/PMLNET/PMLUI 寻址流水线；
4. 运行时免重启动态热装载宏生成；
5. 源码与定义文件快速安全读取。

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
# 基础路径与配置
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
# 逐文件深度解析与符号提取 (AST / Regex Parser)
# ============================================================

def parse_pml_file_deep(file_path):
    """
    深度解析单个 PML / Macro / UIC / DLL 文件的底层细节与符号。
    """
    file_path = util.normalize_path(file_path)
    if not os.path.isfile(file_path):
        return None

    fname = os.path.basename(file_path)
    ext = os.path.splitext(fname)[1].lower()
    sz = os.path.getsize(file_path)
    mtime = os.path.getmtime(file_path)
    mtime_str = util.format_iso(mtime) if hasattr(util, 'format_iso') else str(int(mtime))

    meta = {
        'file': fname,
        'path': file_path,
        'ext': ext,
        'size': sz,
        'size_str': f"{sz / 1024:.1f} KB" if sz >= 1024 else f"{sz} B",
        'mtime': mtime_str,
        'category': 'other',
        'symbols': [],
        'call_cmd': None,
        'line_count': 0,
        'summary': '',
        'diagnostics': [],
    }

    # 1. PML.NET 程序集 (DLL)
    if ext == '.dll':
        meta['category'] = 'pmlnet'
        base_name = os.path.splitext(fname)[0]
        meta['assembly_name'] = base_name
        is_framework = base_name.startswith(('System.', 'Microsoft.', 'mscorlib', 'WindowsBase'))
        meta['is_framework'] = is_framework
        if not is_framework:
            meta['call_cmd'] = f"import '{base_name}'"
            meta['symbols'].append(f"Assembly: {base_name}")
            meta['summary'] = f"PML.NET 程序集 ({base_name})"
        else:
            meta['summary'] = f"依赖运行时组件 ({fname})"
        return meta

    # 2. UIC 功能区配置文件 (.uic / .xml)
    if ext in ('.uic', '.xml') and ('uic' in fname.lower() or 'ribbon' in fname.lower() or ext == '.uic'):
        meta['category'] = 'uic'
        try:
            with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                content = f.read()
            tabs = re.findall(r'<Tab[^>]*Name=[\'"]([^\'"]+)[\'"][^>]*>.*?<Caption>([^<]+)</Caption>', content, re.DOTALL)
            tools = re.findall(r'<ButtonTool[^>]*Name=[\'"]([^\'"]+)[\'"].*?<Caption>([^<]+)</Caption>.*?<Macro>([^<]+)</Macro>', content, re.DOTALL)
            meta['tabs'] = [{'name': t[0], 'caption': t[1]} for t in tabs]
            meta['tools'] = [{'name': b[0], 'caption': b[1], 'macro': b[2]} for b in tools]
            for b in tools:
                meta['symbols'].append(f"Button: {b[1]} ({b[2]})")
            meta['summary'] = f"Ribbon 功能区定制 ({len(tabs)} 个选项卡, {len(tools)} 个按钮工具)"
        except Exception as e:
            meta['diagnostics'].append(f"解析 UIC XML 失败: {e}")
        return meta

    # 3. 文本类 PML 文件 (.pmlfrm, .pmlobj, .pmlfnc, .mac, .pmlcmd)
    try:
        with open(file_path, 'r', encoding='latin-1', errors='ignore') as f:
            lines = f.readlines()
        content = ''.join(lines)
        meta['line_count'] = len(lines)
    except Exception as e:
        meta['diagnostics'].append(f"读取文件失败: {e}")
        return meta

    # 3.1 PML 表单 (Form)
    if ext == '.pmlfrm':
        meta['category'] = 'form'
        m = re.search(r'setup\s+form\s+!!([a-zA-Z0-9_]+)(?:\s+(dialog|modal|dockable|main))?', content, re.IGNORECASE)
        form_name = m.group(1) if m else os.path.splitext(fname)[0]
        wtype = m.group(2) if (m and m.group(2)) else 'standard'
        title_m = re.search(r'title\s+[\'"]([^\'"]+)[\'"]', content, re.IGNORECASE)
        title = title_m.group(1) if title_m else ''

        meta['form_name'] = form_name
        meta['window_type'] = wtype
        meta['title'] = title
        meta['call_cmd'] = f"show !!{form_name}"
        meta['symbols'].append(f"Form: !!{form_name} [{wtype}] {f'({title})' if title else ''}")
        meta['summary'] = f"PML 窗体 (show !!{form_name})"

    # 3.2 PML 对象 (Object)
    elif ext == '.pmlobj':
        meta['category'] = 'object'
        m = re.search(r'define\s+object\s+([a-zA-Z0-9_]+)', content, re.IGNORECASE)
        obj_name = m.group(1) if m else os.path.splitext(fname)[0]
        methods = re.findall(r'define\s+method\s+\.([a-zA-Z0-9_]+)\s*\((.*?)\)', content, re.IGNORECASE)

        meta['object_name'] = obj_name
        meta['methods'] = [{'name': met[0], 'args': met[1].strip(), 'sig': f".{met[0]}({met[1].strip()})"} for met in methods]
        meta['symbols'].append(f"Object: {obj_name}")
        for met in meta['methods']:
            meta['symbols'].append(f"Method: {met['sig']}")
        meta['summary'] = f"PML 数据对象 ({obj_name}, 含 {len(methods)} 个方法)"

    # 3.3 PML 函数 (Function)
    elif ext == '.pmlfnc':
        meta['category'] = 'function'
        m = re.search(r'define\s+function\s+!([a-zA-Z0-9_]+)\s*\((.*?)\)', content, re.IGNORECASE)
        fnc_name = m.group(1) if m else os.path.splitext(fname)[0]
        args = m.group(2).strip() if m else ''

        meta['function_name'] = fnc_name
        meta['args'] = args
        meta['call_cmd'] = f"!{fnc_name}({args})"
        meta['symbols'].append(f"Function: !{fnc_name}({args})")
        meta['summary'] = f"PML 全局函数 (!{fnc_name})"

    # 3.4 PML 宏 / 命令脚本 (Macro)
    elif ext in ('.mac', '.pmlcmd', '.pmlmac'):
        meta['category'] = 'macro'
        meta['call_cmd'] = f"$m {file_path}"
        meta['symbols'].append(f"Macro: {fname}")
        meta['summary'] = f"PML 命令宏 ($m {fname})"

    return meta


# ============================================================
# 插件全景扫描与结构化分析 (Deep Plugin Inspection)
# ============================================================

def inspect_plugin_deep(folder_path):
    """
    深度扫描单个插件的完整文件树，按 E3D 运行层级输出全景元数据。
    """
    folder_path = util.normalize_path(folder_path)
    if not os.path.isdir(folder_path):
        return None

    name = os.path.basename(folder_path)
    tree = {
        'name': name,
        'path': folder_path,
        'enabled': False,
        'has_pmllib': False,
        'pmllib_path': None,
        'has_pmlui': False,
        'pmlui_path': None,
        'has_pmlnet': False,
        'pmlnet_path': None,
        'has_dflts': False,
        'dflts_path': None,
        'has_pml_index': False,
        'pml_index_path': None,
        'pml_index_count': 0,
        'pml_index_actual_count': 0,
        'pml_index_status': 'none',  # 'ok', 'missing', 'outdated', 'none'
        'forms': [],
        'objects': [],
        'functions': [],
        'macros': [],
        'assemblies': [],
        'uic_configs': [],
        'pmlui_scripts': [],
        'doc_files': [],
        'other_files': [],
        'entry_commands': [],
        'hotload_cmds': [],
        'diagnostics': [],
    }

    # 1. 扫描子目录
    try:
        entries = os.listdir(folder_path)
    except OSError as e:
        tree['diagnostics'].append(f"无法读取插件目录: {e}")
        return tree

    for entry in entries:
        sub = os.path.join(folder_path, entry)
        low = entry.lower()
        if os.path.isdir(sub):
            if low == 'pmllib':
                tree['has_pmllib'] = True
                tree['pmllib_path'] = sub
            elif low in ('pdmsui', 'pmlui'):
                tree['has_pmlui'] = True
                tree['pmlui_path'] = sub
            elif low == 'bin':
                tree['has_pmlnet'] = True
                tree['pmlnet_path'] = sub
            elif low in ('dflts', 'defaults'):
                tree['has_dflts'] = True
                tree['dflts_path'] = sub

    # 兜底：若根目录直接存有 PML 文件
    if not tree['has_pmllib']:
        root_pml = [
            f for f in entries
            if os.path.isfile(os.path.join(folder_path, f))
            and os.path.splitext(f)[1].lower() in ('.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac')
        ]
        if root_pml:
            tree['has_pmllib'] = True
            tree['pmllib_path'] = folder_path

    # 2. 遍历并解析所有文件（排除 backup 垃圾目录）
    pml_file_count = 0
    for root, dirs, files in os.walk(folder_path):
        dirs[:] = [d for d in dirs if not d.startswith('.') and 'backup' not in d.lower() and 'bak' not in d.lower()]
        for f in files:
            fp = os.path.join(root, f)
            low = f.lower()
            if low.endswith(('.bak', '.old', '.invalid')) or '.bak' in low:
                continue

            parsed = parse_pml_file_deep(fp)
            if not parsed:
                continue

            cat = parsed['category']
            if cat == 'form':
                tree['forms'].append(parsed)
                pml_file_count += 1
                if parsed['call_cmd']:
                    tree['entry_commands'].append(parsed['call_cmd'])
            elif cat == 'object':
                tree['objects'].append(parsed)
                pml_file_count += 1
            elif cat == 'function':
                tree['functions'].append(parsed)
                pml_file_count += 1
                if parsed['call_cmd'] and not tree['forms']:
                    tree['entry_commands'].append(parsed['call_cmd'])
            elif cat == 'macro':
                tree['macros'].append(parsed)
                if parsed['call_cmd'] and not tree['forms'] and not tree['functions']:
                    tree['entry_commands'].append(parsed['call_cmd'])
            elif cat == 'pmlnet':
                tree['assemblies'].append(parsed)
                if not parsed.get('is_framework') and parsed.get('call_cmd'):
                    tree['entry_commands'].append(parsed['call_cmd'])
            elif cat == 'uic':
                tree['uic_configs'].append(parsed)
            else:
                if low.startswith(('readme', 'install', '安装说明')) and low.endswith(('.md', '.txt', '.docx', '.doc')):
                    tree['doc_files'].append(parsed)
                elif 'pdmsui' in fp.lower() or 'pmlui' in fp.lower():
                    tree['pmlui_scripts'].append(parsed)
                else:
                    tree['other_files'].append(parsed)

    tree['pml_index_actual_count'] = pml_file_count

    # 3. 检查 pml.index 状态
    if tree['has_pmllib'] and tree['pmllib_path']:
        idx_path = os.path.join(tree['pmllib_path'], 'pml.index')
        tree['pml_index_path'] = idx_path
        if os.path.isfile(idx_path):
            tree['has_pml_index'] = True
            try:
                with open(idx_path, 'r', encoding='ascii', errors='ignore') as f:
                    idx_lines = [line.strip() for line in f if line.strip() and not line.startswith('/')]
                tree['pml_index_count'] = len(idx_lines)
                if tree['pml_index_count'] == tree['pml_index_actual_count']:
                    tree['pml_index_status'] = 'ok'
                else:
                    tree['pml_index_status'] = 'outdated'
                    tree['diagnostics'].append(
                        f"pml.index 索引数目 ({tree['pml_index_count']}) 与实际 PML 文件数目 ({tree['pml_index_actual_count']}) 不一致，建议重建索引"
                    )
            except Exception as e:
                tree['pml_index_status'] = 'error'
                tree['diagnostics'].append(f"读取 pml.index 失败: {e}")
        else:
            tree['pml_index_status'] = 'missing'
            if tree['pml_index_actual_count'] > 0:
                tree['diagnostics'].append("缺少 pml.index 索引文件，E3D 无法在运行时直接寻址表单/对象")

    # 4. 生成单插件热加载命令序列
    hotloads = []
    if tree['has_pmllib'] and tree['pmllib_path']:
        hotloads.append(f"pml index '{tree['pmllib_path']}'")
        hotloads.append("pml rehash all")
    for asm in tree['assemblies']:
        if not asm.get('is_framework') and asm.get('call_cmd'):
            hotloads.append(asm['call_cmd'])
    tree['hotload_cmds'] = hotloads

    # 针对 DLL 依赖完整性检测
    if tree['has_pmlnet'] and tree['assemblies']:
        dll_names = {a['file'].lower() for a in tree['assemblies']}
        # 若包含主 DLL 但缺少 System.Text.Json 等常用依赖
        has_custom = any(not a.get('is_framework') for a in tree['assemblies'])
        if has_custom and len(tree['assemblies']) == 1:
            tree['diagnostics'].append("该插件仅包含单个 DLL，若其依赖第三方库，请确保依赖文件同置于 bin/ 目录下")

    return tree


def scan_plugins(plugins_dir=None, local_projects_dir=None, auto_heal=True):
    """
    扫描指定插件目录下的所有插件，并检查其当前启用状态。
    支持 auto_heal 自动为新加入的插件重构/修补缺失的 pml.index 索引，
    支持识别直接放入根目录的散落 PML 脚本。
    """
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    if not p_dir or not os.path.isdir(p_dir):
        return []

    enabled_names = set(read_enabled_plugins(local_projects_dir))
    plugins = []
    root_loose_files = []

    try:
        entries = sorted(os.listdir(p_dir))
    except OSError:
        return []

    for entry in entries:
        sub = os.path.join(p_dir, entry)
        if entry.startswith('.'):
            continue
        if os.path.isdir(sub):
            info = inspect_plugin_deep(sub)
            if info:
                # 自动愈合：如果发现新放入的插件缺少或索引过期，自动生成标准 pml.index
                if auto_heal and info.get('has_pmllib') and info.get('pmllib_path'):
                    if info.get('pml_index_status') in ('missing', 'outdated') and info.get('pml_index_actual_count', 0) > 0:
                        ok, _ = rebuild_pml_index(info['pmllib_path'])
                        if ok:
                            info['has_pml_index'] = True
                            info['pml_index_status'] = 'ok'
                            info['pml_index_count'] = info['pml_index_actual_count']
                            info['diagnostics'] = [d for d in info['diagnostics'] if 'pml.index' not in d]

                info['enabled'] = info['name'] in enabled_names
                plugins.append(info)
        elif os.path.isfile(sub):
            ext = os.path.splitext(entry)[1].lower()
            if ext in ('.pmlfrm', '.pmlobj', '.pmlfnc', '.mac', '.uic', '.dll', '.xml') and entry.lower() != 'load_all_plugins.mac':
                parsed = parse_pml_file_deep(sub)
                if parsed:
                    root_loose_files.append(parsed)

    # 若根目录存在散落文件，自动聚合成通用根插件
    if root_loose_files:
        root_plugin = {
            'name': '_Root_Scripts_',
            'path': p_dir,
            'enabled': '_Root_Scripts_' in enabled_names,
            'has_pmllib': True,
            'pmllib_path': p_dir,
            'has_pmlui': False,
            'pmlui_path': None,
            'has_pmlnet': False,
            'pmlnet_path': None,
            'has_dflts': False,
            'dflts_path': None,
            'has_pml_index': os.path.isfile(os.path.join(p_dir, 'pml.index')),
            'pml_index_path': os.path.join(p_dir, 'pml.index'),
            'pml_index_count': len(root_loose_files),
            'pml_index_actual_count': len(root_loose_files),
            'pml_index_status': 'ok',
            'forms': [f for f in root_loose_files if f['category'] == 'form'],
            'objects': [f for f in root_loose_files if f['category'] == 'object'],
            'functions': [f for f in root_loose_files if f['category'] == 'function'],
            'macros': [f for f in root_loose_files if f['category'] == 'macro'],
            'assemblies': [f for f in root_loose_files if f['category'] == 'pmlnet'],
            'uic_configs': [f for f in root_loose_files if f['category'] == 'uic'],
            'pmlui_scripts': [],
            'doc_files': [],
            'other_files': [],
            'entry_commands': [f['call_cmd'] for f in root_loose_files if f.get('call_cmd')],
            'hotload_cmds': [f"pml index '{p_dir}'", "pml rehash all"],
            'diagnostics': [],
        }
        if auto_heal and not root_plugin['has_pml_index']:
            rebuild_pml_index(p_dir)
            root_plugin['has_pml_index'] = True
        plugins.append(root_plugin)

    return plugins


def import_plugin_from_path(src_path, plugins_dir=None, local_projects_dir=None, auto_enable=True):
    """
    智能导入外部插件目录或压缩包（.zip）：
    1. 自动解压或复制至 D:\\AVEVA\\Plugins\\<名称>；
    2. 自动检测并重构 pml.index 索引；
    3. 自动同步至 custom_evars.bat 并更新热装载宏。
    """
    import zipfile
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    src_path = util.normalize_path(src_path)
    if not os.path.exists(src_path):
        raise FileNotFoundError(f'源路径不存在: {src_path}')

    os.makedirs(p_dir, exist_ok=True)
    base_name = os.path.splitext(os.path.basename(src_path))[0]
    safe_name = re.sub(r'[^a-zA-Z0-9_\-\u4e00-\u9fa5]', '_', base_name).strip() or 'Plugin_' + util.random_id()[:6]
    target_dir = os.path.join(p_dir, safe_name)

    if os.path.isdir(src_path):
        # 复制整个目录
        if os.path.exists(target_dir):
            target_dir = os.path.join(p_dir, f"{safe_name}_{int(os.path.getmtime(src_path))}")
        shutil.copytree(src_path, target_dir)
    elif zipfile.is_zipfile(src_path):
        # 解压 zip
        os.makedirs(target_dir, exist_ok=True)
        with zipfile.ZipFile(src_path, 'r') as zf:
            zf.extractall(target_dir)

        # 检查是否包含单层多余根文件夹（例如 MyPlugin.zip 内部直接包含 MyPlugin/ 目录）
        sub_items = [i for i in os.listdir(target_dir) if not i.startswith('.')]
        if len(sub_items) == 1 and os.path.isdir(os.path.join(target_dir, sub_items[0])):
            single_inner = os.path.join(target_dir, sub_items[0])
            for inner_item in os.listdir(single_inner):
                shutil.move(os.path.join(single_inner, inner_item), target_dir)
            os.rmdir(single_inner)
    else:
        # 单个独立脚本文件
        os.makedirs(target_dir, exist_ok=True)
        shutil.copy2(src_path, target_dir)

    # 深度分析与自动建索引
    info = inspect_plugin_deep(target_dir)
    if info and info.get('has_pmllib') and info.get('pmllib_path'):
        rebuild_pml_index(info['pmllib_path'])

    if auto_enable:
        set_plugin_enabled(safe_name, True, local_projects_dir=local_projects_dir, plugins_dir=p_dir)
        try:
            generate_hotload_macro(plugins_dir=p_dir, local_projects_dir=local_projects_dir)
        except Exception:
            pass

    return target_dir, safe_name


# ============================================================
# 全局符号冲突与遮蔽检测器 (Global Conflict & Shadowing Detector)
# ============================================================

def detect_global_conflicts(plugins_list=None, plugins_dir=None, local_projects_dir=None):
    """
    深度比对所有启用插件中的表单、对象、函数与宏名称，
    检测是否存在命名冲突与搜索路径遮蔽（Shadowing）。
    这是 AVEVA 报错 'Duplicate files in searchpath ignored' 的根本原因。
    """
    if plugins_list is None:
        plugins_list = scan_plugins(plugins_dir, local_projects_dir)

    enabled_plugins = [p for p in plugins_list if p.get('enabled')]

    symbol_map = {}  # symbol_key -> [ {plugin, file, type, path} ]
    conflicts = []

    for p in enabled_plugins:
        pname = p['name']
        # 1. Forms
        for f in p.get('forms', []):
            k = f"Form:!!{f.get('form_name', '').lower()}"
            symbol_map.setdefault(k, []).append({'plugin': pname, 'file': f['file'], 'type': '表单', 'path': f['path']})
        # 2. Objects
        for o in p.get('objects', []):
            k = f"Object:{o.get('object_name', '').lower()}"
            symbol_map.setdefault(k, []).append({'plugin': pname, 'file': o['file'], 'type': '对象', 'path': o['path']})
        # 3. Functions
        for fn in p.get('functions', []):
            k = f"Function:!{fn.get('function_name', '').lower()}"
            symbol_map.setdefault(k, []).append({'plugin': pname, 'file': fn['file'], 'type': '函数', 'path': fn['path']})
        # 4. DLLs
        for dll in p.get('assemblies', []):
            if not dll.get('is_framework'):
                k = f"DLL:{dll.get('assembly_name', '').lower()}"
                symbol_map.setdefault(k, []).append({'plugin': pname, 'file': dll['file'], 'type': '程序集', 'path': dll['path']})

    for sym, occurrences in symbol_map.items():
        if len(occurrences) > 1:
            conflicts.append({
                'symbol': sym,
                'type': occurrences[0]['type'],
                'count': len(occurrences),
                'occurrences': occurrences,
                'winner': occurrences[0],  # 按照 PMLLIB 顺序最先加载的胜出
                'shadowed': occurrences[1:],
                'warning': f"符号 '{sym}' 在多个插件中重复定义 ({', '.join(o['plugin'] for o in occurrences)})，后加载者将被 E3D 忽略！",
            })

    return {
        'total_symbols_checked': len(symbol_map),
        'conflict_count': len(conflicts),
        'conflicts': conflicts,
    }


# ============================================================
# E3D 环境变量模拟器 (E3D Resolution Chain Simulator)
# ============================================================

def simulate_e3d_resolution_chain(plugins_dir=None, local_projects_dir=None):
    """
    模拟 E3D 启动时的真实环境变量解析顺序与路径堆栈。
    """
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    plugins = scan_plugins(p_dir, local_projects_dir)
    enabled = [p for p in plugins if p.get('enabled')]

    pmllib_chain = []
    pmlnet_chain = []
    pmlui_chain = []
    dflts_chain = []

    for p in enabled:
        if p.get('has_pmllib') and p.get('pmllib_path'):
            pmllib_chain.append({'plugin': p['name'], 'path': p['pmllib_path'], 'has_index': p.get('has_pml_index')})
        if p.get('has_pmlnet') and p.get('pmlnet_path'):
            pmlnet_chain.append({'plugin': p['name'], 'path': p['pmlnet_path'], 'dll_count': len(p.get('assemblies', []))})
        if p.get('has_pmlui') and p.get('pmlui_path'):
            pmlui_chain.append({'plugin': p['name'], 'path': p['pmlui_path']})
        if p.get('has_dflts') and p.get('dflts_path'):
            dflts_chain.append({'plugin': p['name'], 'path': p['dflts_path']})

    conflicts = detect_global_conflicts(plugins)

    return {
        'enabled_plugins_count': len(enabled),
        'total_plugins_count': len(plugins),
        'pmllib': pmllib_chain,
        'pmlnet': pmlnet_chain,
        'pmlui': pmlui_chain,
        'dflts': dflts_chain,
        'conflicts': conflicts,
    }


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

    # 2. 从外部显式 set 解析 (兼容原有手写配置)
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
        info = inspect_plugin_deep(p_path)
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
    """
    pmllib_path = util.normalize_path(pmllib_path)
    if not os.path.isdir(pmllib_path):
        return False, '指定路径不是有效的目录'

    dir_files = {}
    pml_exts = {'.pmlfrm', '.pmlobj', '.pmlfnc', '.pmlcmd', '.pmlmac'}

    for root, dirs, files in os.walk(pmllib_path):
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
        if p.get('has_pmllib') and p.get('pmllib_path'):
            ok, msg = rebuild_pml_index(p['pmllib_path'])
            results[p['name']] = {'ok': ok, 'message': msg}
    return results


# ============================================================
# 运行时动态热装载宏生成器
# ============================================================

def generate_hotload_macro(plugins_dir=None, local_projects_dir=None, output_path=None):
    """
    生成 AVEVA PML 运行时动态热装载宏文件 (load_all_plugins.mac)。
    """
    p_dir = util.normalize_path(plugins_dir or get_plugins_dir())
    plugins = scan_plugins(p_dir, local_projects_dir)
    enabled_plugins = [p for p in plugins if p.get('enabled')]

    if not output_path:
        output_path = os.path.join(p_dir, 'load_all_plugins.mac')

    lines = [
        '-- ========================================================',
        '-- AVEVA E3D Plugin Dynamic Hot-Load Macro',
        '-- Auto-generated by SEP (Smart E3D Project Launcher)',
        '-- Usage in E3D: $m ' + output_path,
        '-- ========================================================',
        '',
        '-- 1. Index PML Libraries',
    ]

    for p in enabled_plugins:
        if p.get('has_pmllib') and p.get('pmllib_path'):
            lines.append(f"pml index '{p['pmllib_path']}'")

    lines.append('')
    lines.append('-- 2. Rehash all PML definitions into memory')
    lines.append('pml rehash all')
    lines.append('')
    lines.append('-- 3. Import PML.NET Assemblies')

    for p in enabled_plugins:
        for asm in p.get('assemblies', []):
            if not asm.get('is_framework') and asm.get('assembly_name'):
                lines.append(f"import '{asm['assembly_name']}'")

    lines.append('')
    lines.append('$P ========================================================')
    lines.append(f'$P [SEP] {len(enabled_plugins)} E3D plugins successfully hot-loaded!')
    lines.append('$P Available Entry Commands:')
    for p in enabled_plugins:
        cmds = ', '.join(p.get('entry_commands', [])) if p.get('entry_commands') else 'No direct form'
        lines.append(f"$P   * {p['name']}: {cmds}")
    lines.append('$P ========================================================')

    content = '\r\n'.join(lines) + '\r\n'
    try:
        with open(output_path, 'w', encoding='utf-8', errors='replace') as f:
            f.write(content)
        return output_path, content
    except OSError as e:
        raise OSError(f'生成热装载宏失败: {e}')


# ============================================================
# 源码文件查看与辅助工具
# ============================================================

def read_plugin_file_content(file_path, max_lines=1000):
    """安全读取插件文件内容以供前端/CLI展示。"""
    file_path = util.normalize_path(file_path)
    plugins_dir = get_plugins_dir()
    # 安全校验：确保只读取 plugins_dir 下的文件
    if not os.path.isfile(file_path) or not file_path.lower().startswith(plugins_dir.lower()):
        return {'ok': False, 'error': '非法文件路径或文件不存在'}

    meta = parse_pml_file_deep(file_path)
    try:
        with open(file_path, 'r', encoding='latin-1', errors='ignore') as f:
            lines = [f.readline() for _ in range(max_lines)]
        content = ''.join(lines)
        return {
            'ok': True,
            'meta': meta,
            'content': content,
            'is_truncated': len(lines) == max_lines,
        }
    except Exception as e:
        return {'ok': False, 'error': f'读取文件失败: {e}'}


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
