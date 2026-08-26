#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP — Smart E3D Project Launcher
================================
AVEVA Everything3D 项目启动器：
  - 路径库管理：本地 / SMB(UNC) 项目集合与单项目 bat 文件
  - 我的项目 / 全部项目双列表
  - 三种启动：单项目、载入我的全部、整库载入（方式 A）
  - 通过 custom_evars.bat 托管区写入 call，evars.bat / evars.init 双写 projects_dir
  - 通过 .lnk 启动 E3D，不弹出命令行窗口
"""

import argparse
import os
import sys

if sys.platform == 'win32':
    import ctypes
    try:
        ctypes.windll.kernel32.SetConsoleOutputCP(65001)
        ctypes.windll.kernel32.SetConsoleCP(65001)
    except Exception:
        pass

import e3d_launcher as launcher
import e3d_scanner as scanner
import e3d_store as store
import e3d_util as util
import e3d_web


_MUTEX_HANDLE = None


def _ensure_single_instance():
    """
    检查是否已有 SEP 实例正在运行（基于 Windows 命名 Mutex）。
    若已在运行，唤起已运行实例的 Web 页面并退出当前进程。
    """
    global _MUTEX_HANDLE
    if sys.platform != 'win32':
        return True
    try:
        mutex_name = "Local\\SEP_SmartE3DProject_SingleInstance"
        mutex = ctypes.windll.kernel32.CreateMutexW(None, False, mutex_name)
        last_error = ctypes.windll.kernel32.GetLastError()
        if last_error == 183:  # ERROR_ALREADY_EXISTS
            runtime_file = os.path.join(util.get_user_data_dir(), ".sep_runtime.json")
            url = "http://127.0.0.1:8800"
            if os.path.exists(runtime_file):
                try:
                    import json
                    with open(runtime_file, 'r', encoding='utf-8') as f:
                        info = json.load(f)
                    url = info.get('url', url)
                except Exception:
                    pass
            import webbrowser
            try:
                webbrowser.open(url)
            except Exception:
                pass
            return False
        _MUTEX_HANDLE = mutex
        return True
    except Exception:
        return True


def _fatal_alert(msg):
    """记录崩溃日志并在 GUI 模式下弹出 Windows 错误对话框。"""
    crash_log = os.path.join(util.SCRIPT_DIR, "sep_crash.log")
    try:
        with open(crash_log, "a", encoding="utf-8") as f:
            f.write(f"[{util.now_iso()}] {msg}\n")
    except Exception:
        pass
    if sys.platform == 'win32':
        try:
            ctypes.windll.user32.MessageBoxW(0, f"SEP 发生错误：\n\n{msg}", "SEP 错误", 0x10)
        except Exception:
            pass


def _hide_console():
    """GUI 模式下隐藏控制台窗口（打包后双击不弹黑窗）。"""
    if not getattr(sys, 'frozen', False) or sys.platform != 'win32':
        return
    try:
        hwnd = ctypes.windll.kernel32.GetConsoleWindow()
        if hwnd:
            ctypes.windll.user32.ShowWindow(hwnd, 0)
    except Exception:
        pass



def _print_status():
    ok = launcher.resolve_e3d(force=False, verbose=False)
    data = store.load_data()
    print('=' * 62)
    print('  SEP — E3D 启动器')
    print('=' * 62)
    if ok:
        print(f'  E3D 安装   : {os.path.dirname(launcher.EVARS_BAT)}')
        cur = launcher.read_projects_dir(launcher.EVARS_BAT) or '(无法读取)'
        print(f'  当前项目库 : {cur}')
    else:
        print('  E3D 安装   : 未检测到（可尝试 --detect）')
    local = launcher.get_local_projects_dir(data)
    managed = launcher.read_managed(local)
    print(f'  本地项目库 : {local}')
    print(f'  托管项目   : {len(managed)} 个')
    lnk = util.find_e3d_lnk(data['settings'].get('e3d_lnk', ''))
    print(f'  启动快捷   : {lnk or "未找到"}')
    print(f'  我的项目   : {len(data["my_projects"])} 个')
    print(f'  路径库     : {len(data["libraries"])} 个')
    print(f'  分类       : {len(data.get("categories") or [])} 个')
    print(f'  暂存项目   : {len(data["all_projects_cache"])} 个')


def _print_projects(title, projects):
    print(f'\n  {title} ({len(projects)} 个)')
    if not projects:
        print('    (空)')
        return
    for p in projects:
        print(f'    • {p.get("name", "?")}  [{p.get("id", "")}]')
        print(f'      {p.get("bat_path", "")}')


def _cmd_scan(path):
    info = scanner.classify(path, timeout=8)
    print(f'\n  路径: {util.normalize_path(path)}')
    print(f'  类型: {info.get("kind", "?")}')
    print(f'  说明: {info.get("reason", "")}')
    if info['kind'] in ('invalid', 'unsupported'):
        return 1
    projects, _ = scanner.scan_library(path, timeout=8)
    _print_projects('扫描到的项目', projects)
    return 0


def _cmd_lib_add(path):
    lib, info, projects = scanner.add_library(path, timeout=10)
    if not lib:
        print(f'\n  ✗ 无法添加: {info.get("reason", "未知原因")}')
        return 1
    data = store.load_data()
    existing = store.find_library(data, path=lib['path'])
    if existing:
        existing.update({
            'name': lib['name'],
            'type': lib['type'],
            'protocol': lib['protocol'],
            'last_scan': lib['last_scan'],
            'last_error': None,
        })
        lib = existing
    else:
        data['libraries'].append(lib)
    _replace_cache(data, lib['id'], projects)
    store.save_data(data)
    print(f'\n  ✓ 已添加路径库: {lib["name"]} ({lib["type"]})')
    print(f'    路径: {lib["path"]}')
    _print_projects('识别到项目', projects)
    return 0


def _cmd_lib_list():
    data = store.load_data()
    print(f'\n  路径库 ({len(data["libraries"])} 个)')
    if not data['libraries']:
        print('    (空)')
        return
    counts = {}
    for p in data['all_projects_cache']:
        counts[p.get('lib_id')] = counts.get(p.get('lib_id'), 0) + 1
    for lib in data['libraries']:
        err = f'  ⚠ {lib.get("last_error")}' if lib.get('last_error') else ''
        print(f'    • {lib.get("name", "?")}  [{lib.get("id", "")}]  ({lib.get("type")} / {lib.get("protocol")})'
              f'  {counts.get(lib.get("id"), 0)} 个项目{err}')
        print(f'      {lib.get("path", "")}')


def _cmd_lib_remove(lib_id):
    data = store.load_data()
    before = len(data['libraries'])
    data['libraries'] = [x for x in data['libraries'] if x.get('id') != lib_id]
    if len(data['libraries']) == before:
        print(f'\n  ✗ 未找到路径库: {lib_id}')
        return 1
    data['all_projects_cache'] = [p for p in data['all_projects_cache'] if p.get('lib_id') != lib_id]
    for p in data['my_projects']:
        if p.get('lib_id') == lib_id:
            p['lib_id'] = None
    store.save_data(data)
    print(f'\n  ✓ 已删除路径库: {lib_id}')
    return 0


def _cmd_lib_rescan(lib_id):
    data = store.load_data()
    lib = store.find_library(data, lib_id=lib_id)
    if not lib:
        print(f'\n  ✗ 未找到路径库: {lib_id}')
        return 1
    projects, lib = scanner.rescan_library(lib, timeout=10)
    _replace_cache(data, lib['id'], projects)
    store.save_data(data)
    _print_projects(f'重新扫描: {lib["name"]}', projects)
    return 0


def _cmd_my_add(project_id):
    data = store.load_data()
    proj = store.find_cached_project(data, project_id=project_id)
    if not proj:
        print(f'\n  ✗ 未找到项目: {project_id}（请先扫描路径库）')
        return 1
    if store.find_my_project(data, bat_path=proj['bat_path']):
        print(f'\n  ⚠ 项目已在“我的项目”中: {proj["name"]}')
        return 0
    data['my_projects'].append({
        'id': proj['id'],
        'name': proj['name'],
        'bat_path': proj['bat_path'],
        'lib_id': proj.get('lib_id'),
        'source': 'user',
        'added_at': util.now_iso(),
    })
    store.save_data(data)
    print(f'\n  ✓ 已添加: {proj["name"]} → {proj["bat_path"]}')
    return 0


def _cmd_my_list():
    data = store.load_data()
    meta_map = store.project_meta_map(data)
    projects = [store.merge_project_meta(p, meta_map.get(p.get('id'))) for p in data['my_projects']]
    _print_projects('我的项目', projects)


def _cmd_my_remove(project_id):
    data = store.load_data()
    before = len(data['my_projects'])
    data['my_projects'] = [p for p in data['my_projects'] if p.get('id') != project_id]
    if len(data['my_projects']) == before:
        print(f'\n  ✗ 未找到项目: {project_id}')
        return 1
    store.save_data(data)
    print(f'\n  ✓ 已移除: {project_id}')
    return 0


def _cmd_my_clear():
    data = store.load_data()
    data['my_projects'] = []
    store.save_data(data)
    print('\n  ✓ 已清空我的项目')
    return 0


def _resolve_my(data, key):
    proj = store.find_my_project(data, project_id=key)
    if proj:
        return proj
    return store.find_my_project(data, name=key)


def _resolve_cached(data, key):
    proj = store.find_cached_project(data, project_id=key)
    if proj:
        return proj
    return store.find_cached_project(data, name=key)


def _do_launch(mode, payload):
    data = store.load_data()
    lnk = data['settings'].get('e3d_lnk', '')
    summary = launcher.launch(mode, payload, lnk=lnk)
    print('\n  ✓ 已启动 E3D')
    print(f'    模式       : {summary["mode"]}')
    print(f'    项目库     : {summary["projects_dir"]}')
    if summary['managed_paths']:
        print(f'    托管项目   : {len(summary["managed_paths"])} 个')
        for p in summary['managed_paths']:
            print(f'      call "{p}"')
    print(f'    启动快捷   : {summary["lnk"]}')
    return 0


def _replace_cache(data, lib_id, projects):
    now = util.now_iso()
    data['all_projects_cache'] = [p for p in data['all_projects_cache'] if p.get('lib_id') != lib_id]
    for p in projects:
        item = dict(p)
        item['lib_id'] = lib_id
        item['discovered_at'] = now
        data['all_projects_cache'].append(item)


def _cmd_launch(key):
    data = store.load_data()
    proj = _resolve_my(data, key)
    if not proj:
        print(f'\n  ✗ 未在“我的项目”中找到: {key}')
        print('  可用: ' + ', '.join(p['name'] for p in data['my_projects']))
        return 1
    meta = store.get_project_meta(data, proj.get('id'))
    return _do_launch('single', {'bat_path': proj['bat_path'], 'name': meta.get('name') or proj['name']})


def _cmd_launch_all():
    return _do_launch('all', {'name': '全部'})


def _cmd_load(key):
    data = store.load_data()
    proj = _resolve_cached(data, key)
    if not proj:
        print(f'\n  ✗ 未找到项目: {key}（请先扫描路径库）')
        return 1
    meta = store.get_project_meta(data, proj.get('id'))
    return _do_launch('temp', {'bat_path': proj['bat_path'], 'name': meta.get('name') or proj['name']})


def _cmd_launch_lib(lib_id):
    data = store.load_data()
    lib = store.find_library(data, lib_id=lib_id)
    if not lib:
        print(f'\n  ✗ 未找到路径库: {lib_id}')
        return 1
    return _do_launch('library', {'path': lib['path'], 'name': lib['name']})


def _cmd_diag_e3d():
    import e3d_diag
    print('=' * 62)
    print('  SEP — E3D 配置文件与运行环境体检')
    print('=' * 62)
    print('正在全面排查 evars.bat / evars.init / custom_evars.bat 及网络项...\n')
    report = e3d_diag.diagnose_e3d_config()
    print(f'E3D 安装路径 : {report.get("install_dir") or "(未找到)"}')
    print(f'项目库目录   : {report.get("projects_dir") or "(未找到)"}')
    print(f'evars.init   : {report.get("evars_init") or "(未找到)"}')
    print(f'custom_evars : {report.get("custom_evars") or "(未找到)"}')
    print('-' * 62)

    status_icon = {'ok': '✓', 'warn': '⚠', 'fail': '✗', 'skip': '-'}
    for c in report.get('checks', []):
        icon = status_icon.get(c['status'], '?')
        print(f' [{icon}] {c["name"]}: {c["detail"]}')
        if c.get('invalid_lines'):
            for inv in c['invalid_lines']:
                print(f'     - 行 {inv.get("line_num")}: {inv.get("path")} ({inv.get("reason")})')
        if c.get('timeout_lines'):
            for tm in c['timeout_lines']:
                print(f'     - 行 {tm.get("line_num")}: {tm.get("path")} ({tm.get("reason")})')
        if c.get('offline_projects'):
            for op in c['offline_projects']:
                print(f'     - 项目 {op.get("name")}: {op.get("bat_path")} ({op.get("reason")})')

    print('-' * 62)
    if report.get('ok'):
        print('✓ E3D 配置文件状态良好，未发现可能导致启动卡顿或报错的配置项。')
    else:
        print('⚠ 发现上述异常配置项。若需自动安全备份并修复，请运行：')
        print('  python switch_e3d_project.py --fix-e3d')
    return 0 if report.get('ok') else 1


def _cmd_fix_e3d():
    import e3d_diag
    print('=' * 62)
    print('  SEP — E3D 配置文件一键安全修复')
    print('=' * 62)
    print('正在备份原文件并清理死路径、离线网络挂载及超时阻塞项...\n')
    res = e3d_diag.fix_e3d_config()
    if res.get('ok'):
        print('✓ 修复成功！')
        changes = res.get('changes', [])
        if changes:
            print('修复明细：')
            for ch in changes:
                print(f'  • {ch}')
        else:
            print('未发现需要修复的配置项。')
        return 0
    else:
        print(f'✗ 修复失败: {res.get("message")}')
        return 1


def _cmd_plugin_list():
    import e3d_plugin
    p_dir = e3d_plugin.get_plugins_dir()
    plugins = e3d_plugin.scan_plugins(p_dir)
    print('=' * 66)
    print('  SEP — E3D 插件管理器')
    print(f'  插件目录: {p_dir}')
    print('=' * 66)
    if not plugins:
        print('  未在插件目录下发现任何插件子文件夹。')
        return 0
    for p in plugins:
        status = '✓ 已启用' if p['enabled'] else '○ 已禁用'
        comps = []
        if p['has_pmllib']:
            pml_details = []
            if p.get('forms_count'): pml_details.append(f"{p['forms_count']}表单")
            if p.get('objects_count'): pml_details.append(f"{p['objects_count']}对象")
            if p.get('functions_count'): pml_details.append(f"{p['functions_count']}函数")
            if p.get('macros_count'): pml_details.append(f"{p['macros_count']}宏")
            cnt_str = '+'.join(pml_details) if pml_details else f"{p['pml_files_count']}个文件"
            comps.append(f"PMLLIB({cnt_str}{', 有索引' if p['has_pml_index'] else ', 无索引!'})")
        if p['has_pmlnet']:
            comps.append(f"PML.NET({len(p['dll_files'])}个DLL)")
        if p['has_pmlui']:
            comps.append('PMLUI(自定义菜单)')
        if p.get('has_uic'):
            comps.append(f"UIC({', '.join(p['uic_files'])})")
        if p['has_dflts']:
            comps.append('DFLTS')
        if p.get('doc_file'):
            comps.append(f"📄{p['doc_file']}")

        print(f"\n  [{status}] {p['name']}")
        print(f"      能力组件: {', '.join(comps) if comps else '基础插件'}")
        if p.get('entry_commands'):
            print(f"      入口指令: {', '.join(p['entry_commands'])}")
        if p.get('hotload_cmds'):
            print(f"      热加载:   { ' ; '.join(p['hotload_cmds']) }")
    print('\n' + '=' * 66)
    return 0


def _cmd_plugin_enable(name):
    import e3d_plugin
    e3d_plugin.set_plugin_enabled(name, True)
    print(f'✓ 已启用插件: {name}')
    return 0


def _cmd_plugin_disable(name):
    import e3d_plugin
    e3d_plugin.set_plugin_enabled(name, False)
    print(f'○ 已禁用插件: {name}')
    return 0


def _cmd_plugin_enable_all():
    import e3d_plugin
    target = e3d_plugin.set_all_plugins_enabled(True)
    print(f'✓ 已一键启用全部 {len(target)} 个插件: {", ".join(target)}')
    return 0


def _cmd_plugin_disable_all():
    import e3d_plugin
    e3d_plugin.set_all_plugins_enabled(False)
    print('○ 已一键禁用全部插件')
    return 0


def _cmd_plugin_hotload():
    import e3d_plugin
    path, content = e3d_plugin.generate_hotload_macro()
    print('=' * 66)
    print(f'  ✓ 成功生成 E3D 运行时热装载宏: {path}')
    print('  在已运行的 E3D 命令行输入以下指令即可即时载入全部启用插件：')
    print(f'  $m {path}')
    print('=' * 66)
    return 0


def _cmd_plugin_reindex(name=None):
    import e3d_plugin
    if name:
        p_path = os.path.join(e3d_plugin.get_plugins_dir(), name)
        info = e3d_plugin.scan_plugin_folder(p_path)
        if not info or not info.get('has_pmllib'):
            print(f'✗ 未找到插件或无 pmllib: {name}')
            return 1
        ok, msg = e3d_plugin.rebuild_pml_index(info['pmllib_path'])
        print(f"{'✓' if ok else '✗'} {name}: {msg}")
        return 0 if ok else 1
    else:
        results = e3d_plugin.rebuild_all_pml_indexes()
        for k, v in results.items():
            print(f"  {'✓' if v['ok'] else '✗'} {k}: {v['message']}")
        return 0


def _cmd_clean_userdata():
    import e3d_diag
    print('=' * 62)
    print('  SEP — E3D USERDATA 临时缓存与死锁清理')
    print('=' * 62)
    res = e3d_diag.clean_userdata_cache()
    print(f"  {res.get('message')}")
    if res.get('cleaned'):
        for c in res.get('cleaned')[:20]:
            print(f"    - 清理: {c}")
        if len(res.get('cleaned')) > 20:
            print(f"    ... 以及另外 {len(res.get('cleaned')) - 20} 个文件")
    return 0 if res.get('ok') else 1


def _cmd_fix_cad_fonts():
    import e3d_diag
    print('=' * 62)
    print('  SEP — AutoCAD 缺失字体静默修复')
    print('=' * 62)
    res = e3d_diag.fix_cad_fonts_tool()
    print(f"  {res.get('message')}")
    for ch in res.get('changes', []):
        print(f"    • {ch}")
    return 0 if res.get('ok') else 1


def _cmd_cli():
    while True:
        _print_status()
        print('\n  ------------------------------------')
        print('    [1] 我的项目列表')
        print('    [2] 全部项目列表（路径库）')
        print('    [3] 添加路径库')
        print('    [4] 启动我的项目（单选）')
        print('    [5] 载入我的全部项目')
        print('    [6] 临时载入单个项目')
        print('    [7] 整库载入（方式 A）')
        print('    [8] 重新检测 E3D')
        print('    [0] 退出')
        print('  ------------------------------------')
        choice = input('  请选择: ').strip()
        try:
            if choice == '1':
                _cmd_my_list()
            elif choice == '2':
                _cmd_lib_list()
            elif choice == '3':
                path = input('  路径库路径: ').strip()
                if path:
                    _cmd_lib_add(path)
            elif choice == '4':
                key = input('  项目名称或 ID: ').strip()
                if key:
                    _cmd_launch(key)
            elif choice == '5':
                _cmd_launch_all()
            elif choice == '6':
                key = input('  项目名称或 ID: ').strip()
                if key:
                    _cmd_load(key)
            elif choice == '7':
                _cmd_lib_list()
                lid = input('  路径库 ID: ').strip()
                if lid:
                    _cmd_launch_lib(lid)
            elif choice == '8':
                launcher.resolve_e3d(force=True, verbose=True)
            elif choice == '0':
                print('\n  再见!')
                break
            else:
                print('\n  ✗ 无效选择')
        except (launcher.LauncherError, OSError) as e:
            print(f'\n  ✗ {e}')
        input('\n  按回车继续...')
    return 0


def main():
    parser = argparse.ArgumentParser(
        description='SEP — E3D 启动器（默认启动 Web 面板）',
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument('--status', action='store_true', help='显示当前状态')
    parser.add_argument('--scan', metavar='PATH', help='识别并扫描路径（集合或单项目）')
    parser.add_argument('--lib', nargs='+', metavar=('CMD', 'ARG'),
                        help='路径库管理: add <路径> / list / remove <ID> / rescan <ID>')
    parser.add_argument('--my', nargs='+', metavar=('CMD', 'ARG'),
                        help='我的项目管理: add <项目ID> / list / remove <ID> / clear')
    parser.add_argument('--launch', metavar='NAME_OR_ID', help='启动我的项目（单项目）')
    parser.add_argument('--launch-all', action='store_true', help='载入我的全部项目并启动')
    parser.add_argument('--load', metavar='NAME_OR_ID', help='临时载入单个项目（不加入我的项目）')
    parser.add_argument('--launch-lib', metavar='LIB_ID', help='整库载入（方式 A）')
    parser.add_argument('--detect', action='store_true', help='重新检测 E3D 安装路径')
    parser.add_argument('--diag-e3d', action='store_true', help='全面诊断 E3D 配置文件与运行环境')
    parser.add_argument('--fix-e3d', action='store_true', help='一键安全修复 E3D 配置文件中的失效与阻塞项')
    parser.add_argument('--plugin', nargs='+', metavar=('CMD', 'ARG'),
                        help='插件管理: list / enable <名称> / disable <名称> / reindex [名称]')
    parser.add_argument('--clean-userdata', action='store_true', help='清理 USERDATA 临时缓存与死锁')
    parser.add_argument('--fix-cad-fonts', action='store_true', help='一键修复 AutoCAD 缺失字体弹窗与映射')
    parser.add_argument('--cli', action='store_true', help='使用终端菜单模式')
    parser.add_argument('--web', action='store_true', help='启动 Web 面板（默认）')

    # 旧命令兼容
    parser.add_argument('--add', action='store_true', help='(兼容) 添加路径库')
    parser.add_argument('--remove', action='store_true', help='(兼容) 移除我的项目')
    parser.add_argument('--list', action='store_true', help='(兼容) 列出我的项目')
    args = parser.parse_args()

    if args.diag_e3d:
        return _cmd_diag_e3d()
    if args.fix_e3d:
        return _cmd_fix_e3d()
    if args.clean_userdata:
        return _cmd_clean_userdata()
    if args.fix_cad_fonts:
        return _cmd_fix_cad_fonts()
    if args.plugin:
        cmd = args.plugin[0].lower()
        rest = args.plugin[1:]
        if cmd == 'list':
            return _cmd_plugin_list()
        if cmd in ('enable', 'on'):
            if not rest:
                print('用法: --plugin enable <插件名称>')
                return 1
            return _cmd_plugin_enable(rest[0])
        if cmd in ('disable', 'off'):
            if not rest:
                print('用法: --plugin disable <插件名称>')
                return 1
            return _cmd_plugin_disable(rest[0])
        if cmd in ('enable-all', 'all-on'):
            return _cmd_plugin_enable_all()
        if cmd in ('disable-all', 'all-off'):
            return _cmd_plugin_disable_all()
        if cmd in ('hotload-mac', 'macro', 'mac'):
            return _cmd_plugin_hotload()
        if cmd == 'reindex':
            return _cmd_plugin_reindex(rest[0] if rest else None)
        print(f'未知 --plugin 命令: {cmd} (可用: list, enable, disable, enable-all, disable-all, reindex, hotload-mac)')
        return 1
    if args.status:
        _print_status()
        return 0
    if args.scan:
        return _cmd_scan(args.scan)
    if args.lib:
        cmd = args.lib[0].lower()
        rest = args.lib[1:]
        if cmd == 'add':
            if not rest:
                print('用法: --lib add <路径>')
                return 1
            return _cmd_lib_add(rest[0])
        if cmd == 'list':
            return _cmd_lib_list()
        if cmd == 'remove':
            if not rest:
                print('用法: --lib remove <ID>')
                return 1
            return _cmd_lib_remove(rest[0])
        if cmd == 'rescan':
            if not rest:
                print('用法: --lib rescan <ID>')
                return 1
            return _cmd_lib_rescan(rest[0])
        print(f'未知 --lib 命令: {cmd}')
        return 1
    if args.my:
        cmd = args.my[0].lower()
        rest = args.my[1:]
        if cmd == 'add':
            if not rest:
                print('用法: --my add <项目ID>')
                return 1
            return _cmd_my_add(rest[0])
        if cmd == 'list':
            return _cmd_my_list()
        if cmd == 'remove':
            if not rest:
                print('用法: --my remove <ID>')
                return 1
            return _cmd_my_remove(rest[0])
        if cmd == 'clear':
            return _cmd_my_clear()
        print(f'未知 --my 命令: {cmd}')
        return 1
    if args.launch:
        return _cmd_launch(args.launch)
    if args.launch_all:
        return _cmd_launch_all()
    if args.load:
        return _cmd_load(args.load)
    if args.launch_lib:
        return _cmd_launch_lib(args.launch_lib)
    if args.detect:
        ok = launcher.resolve_e3d(force=True, verbose=True)
        return 0 if ok else 1
    if args.add:
        path = input('  路径库路径: ').strip()
        return _cmd_lib_add(path) if path else 1
    if args.remove:
        _cmd_my_list()
        key = input('  项目名称或 ID: ').strip()
        data = store.load_data()
        proj = _resolve_my(data, key) if key else None
        return _cmd_my_remove(proj['id']) if proj else 1
    if args.list:
        _cmd_my_list()
        return 0

    # 默认 / --web：启动 Web 面板（GUI 模式隐藏控制台）
    if not args.cli:
        if not _ensure_single_instance():
            return 0
        _hide_console()
    e3d_web.start_web_ui()
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print('\n  已退出。')
        sys.exit(0)
    except launcher.LauncherError as e:
        msg = f"{e}"
        print(f'\n  ✗ {msg}')
        if getattr(sys, 'frozen', False):
            _fatal_alert(msg)
        sys.exit(1)
    except Exception as e:
        import traceback
        err_detail = traceback.format_exc()
        print(f'\n  ✗ 意外错误: {e}')
        traceback.print_exc()
        if getattr(sys, 'frozen', False):
            _fatal_alert(f"{e}\n\n{err_detail}")
        sys.exit(1)

