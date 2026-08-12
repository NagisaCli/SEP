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
    print(f'\n  ✓ 已启动 E3D')
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
    parser.add_argument('--cli', action='store_true', help='使用终端菜单模式')
    parser.add_argument('--web', action='store_true', help='启动 Web 面板（默认）')

    # 旧命令兼容
    parser.add_argument('--add', action='store_true', help='(兼容) 添加路径库')
    parser.add_argument('--remove', action='store_true', help='(兼容) 移除我的项目')
    parser.add_argument('--list', action='store_true', help='(兼容) 列出我的项目')
    args = parser.parse_args()

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
        print(f'\n  ✗ {e}')
        sys.exit(1)
    except Exception as e:
        print(f'\n  ✗ 意外错误: {e}')
        import traceback
        traceback.print_exc()
        sys.exit(1)
