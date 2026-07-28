#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
E3D 项目文件夹切换工具 (跨环境版)
==================================
自动检测 E3D 安装路径，修改 evars.bat / evars.init 中的 projects_dir= 路径。
支持多项目管理 — 可添加/删除/切换任意项目路径。

依赖: e3d_config.py (同目录) — E3D 路径自动检测模块
"""

import os
import sys

# ── Windows 控制台 UTF-8 编码修复（无需 .bat 的 chcp 65001）──
if sys.platform == 'win32':
    import ctypes
    try:
        ctypes.windll.kernel32.SetConsoleOutputCP(65001)
        ctypes.windll.kernel32.SetConsoleCP(65001)
    except Exception:
        pass

import json
import subprocess
import shutil
import re
import argparse
import http.server
import threading
import webbrowser
import urllib.parse
import socket

# ============================================================
# 路径自动检测
# ============================================================

# 目录定位（PyInstaller 兼容）
if getattr(sys, 'frozen', False):
    SCRIPT_DIR = os.path.dirname(sys.executable)
else:
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(SCRIPT_DIR, "e3d_projects.json")

# 动态获取 evars.bat / evars.init 路径
EVARS_BAT = None
EVARS_INIT = None


def resolve_e3d_paths(force_detect=False, verbose=True):
    """自动检测并设置 E3D 配置路径。返回 True 表示成功。"""
    global EVARS_BAT, EVARS_INIT

    # 导入自动检测模块
    sys.path.insert(0, SCRIPT_DIR)
    from e3d_config import get_e3d_paths

    bat, init = get_e3d_paths(force=force_detect, verbose=verbose)
    if bat and init:
        EVARS_BAT = bat
        EVARS_INIT = init
        return True
    return False


# ============================================================
# 项目管理 (JSON 持久化)
# ============================================================

def load_projects():
    """加载项目配置。返回 {名称: 路径} 字典。"""
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
            return data.get('projects', {})
        except (json.JSONDecodeError, IOError):
            pass
    return {}


def save_projects(projects):
    """保存项目配置到 JSON 文件。"""
    data = {'projects': projects}
    with open(CONFIG_FILE, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def add_project_interactive():
    """交互式添加新项目。"""
    projects = load_projects()

    print("\n  ── 添加新项目 ──")
    name = input("  项目名称: ").strip()
    if not name:
        print("  ✗ 名称不能为空")
        return

    if name in projects:
        print(f"  ⚠ 项目 '{name}' 已存在 (路径: {projects[name]})")
        choice = input("  是否覆盖? [y/N]: ").strip().lower()
        if choice != 'y':
            return

    path = input("  项目路径: ").strip()
    if not path:
        print("  ✗ 路径不能为空")
        return

    # 确保路径以反斜杠结尾
    if not path.endswith('\\'):
        path += '\\'

    # 验证路径可访问（本地路径检查，网络路径仅警告）
    if path.startswith('\\\\'):
        print(f"  ⚠ 网络路径无法本地验证，将直接保存。")
    elif not os.path.exists(path):
        print(f"  ⚠ 路径 '{path}' 当前不可访问，仍将保存。")
        choice = input("  确认保存? [Y/n]: ").strip().lower()
        if choice and choice != 'y':
            return

    projects[name] = path
    save_projects(projects)
    print(f"  ✓ 已保存: {name} → {path}")


def remove_project_interactive():
    """交互式删除项目。"""
    projects = get_all_projects()
    if not projects:
        print("\n  没有项目。")
        return

    print("\n  ── 删除项目 ──")
    names = list(projects.keys())
    for i, name in enumerate(names, 1):
        print(f"    [{i}] {name}: {projects[name]}")
    print(f"    [0] 取消")

    choice = input("\n  选择要删除的项目编号: ").strip()
    try:
        idx = int(choice)
        if idx == 0:
            return
        name = names[idx - 1]
    except (ValueError, IndexError):
        print("  ✗ 无效选择")
        return

    del projects[name]
    save_projects(projects)
    print(f"  ✓ 已删除: {name}")


def list_projects():
    """列出所有项目（含预设和用户保存的）。"""
    projects = get_all_projects()
    if not projects:
        print("\n  没有已保存的项目。")
        return

    print(f"\n  ── 所有项目 ({len(projects)} 个) ──")
    bat_dir = None
    if EVARS_BAT:
        bat_dir, _ = get_project_dir(EVARS_BAT)
    for name, path in projects.items():
        marker = ""
        if bat_dir and path.rstrip('\\') == bat_dir.rstrip('\\'):
            marker = " ← 当前"
        print(f"    • {name}: {path}{marker}")


def get_all_projects():
    """获取所有可用项目（预设 + 用户保存的）。"""
    projects = load_projects()
    all_projects = dict(projects)  # 用户项目优先

    # 预设作为后备（从注册表自动检测或默认值）
    presets = _get_preset_projects()
    for name, path in presets.items():
        if name not in all_projects:
            all_projects[name] = path
    return all_projects


def _get_preset_projects():
    """从 E3D 检测结果生成预设项目。"""
    presets = {}

    # 从 e3d_config 缓存获取 projects_dir
    cache_file = os.path.join(SCRIPT_DIR, "e3d_paths.json")
    if os.path.exists(cache_file):
        try:
            with open(cache_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
            pd = data.get("projects_dir", "")
            if pd:
                presets["本地 (auto)"] = pd.rstrip("\\/") + "\\"
        except Exception:
            pass

    # 始终保留一些默认标签
    if "本地" not in presets and "本地 (auto)" not in presets:
        presets["本地"] = "D:\\AVEVA\\Projects\\E3D3.1\\"
    presets.setdefault("远端服务器", "\\\\Pc-20220629mexd\\000E3D31\\")

    return presets


# ============================================================
# 文件操作
# ============================================================

def detect_encoding(filepath):
    """检测文件编码，优先 UTF-8，否则回退到 GBK。"""
    for enc in ['utf-8', 'gbk', 'latin-1']:
        try:
            with open(filepath, 'r', encoding=enc) as f:
                f.read()
            return enc
        except (UnicodeDecodeError, UnicodeError):
            continue
    return 'utf-8'


def get_project_dir(filepath):
    """读取文件中 set projects_dir= 后面的路径。"""
    enc = detect_encoding(filepath)
    with open(filepath, 'r', encoding=enc, errors='replace') as f:
        for line in f:
            m = re.match(r'^set\s+projects_dir=(.*)$', line.strip(), re.IGNORECASE)
            if m:
                return m.group(1).strip(), enc
    return None, enc


def read_raw_lines(filepath):
    """以二进制方式读取文件，保留原始换行符和编码。"""
    with open(filepath, 'rb') as f:
        return f.read()


def write_raw(filepath, data):
    """以二进制方式写入文件。"""
    with open(filepath, 'wb') as f:
        f.write(data)


def set_project_dir_raw(filepath, new_path):
    """
    直接在二进制层面替换 set projects_dir= 后面的路径。
    只修改 = 后面的部分，不触碰文件其余任何内容。
    返回 (是否修改成功, 备份路径)。
    """
    backup = filepath + '.bak'
    shutil.copy2(filepath, backup)

    data = read_raw_lines(filepath)
    pattern = rb'(set\s+projects_dir=)[^\r\n]*'
    new_value = new_path.encode('utf-8')

    if not re.search(pattern, data, re.IGNORECASE):
        return False, backup

    new_data = re.sub(pattern, lambda m: m.group(1) + new_value, data, flags=re.IGNORECASE)
    write_raw(filepath, new_data)
    return True, backup


def validate_bat():
    """运行 evars.bat 看是否报错。"""
    try:
        result = subprocess.run(
            ['cmd', '/c', EVARS_BAT],
            capture_output=True,
            text=True,
            timeout=15,
            encoding='utf-8',
            errors='replace'
        )
        output = (result.stdout or '') + (result.stderr or '')
        if result.returncode != 0:
            return False, output
        lower_out = output.lower()
        error_keywords = [
            'the system cannot find',
            'is not recognized',
            'syntax',
            'invalid',
            'cannot find the path',
            '找不到',
            '不是内部或外部命令',
        ]
        for kw in error_keywords:
            if kw in lower_out:
                return False, output
        return True, output
    except subprocess.TimeoutExpired:
        return False, "超时：evars.bat 运行超过15秒"


def restore_backup(filepath):
    """从 .bak 还原文件。"""
    backup = filepath + '.bak'
    if os.path.exists(backup):
        shutil.copy2(backup, filepath)
        os.remove(backup)
        return True
    return False


def cleanup_backups():
    """清理所有备份文件。"""
    for f in [EVARS_BAT, EVARS_INIT]:
        bak = f + '.bak'
        if os.path.exists(bak):
            os.remove(bak)


# ============================================================
# 项目选择
# ============================================================

def choose_project(all_projects, current_dir):
    """
    让用户从项目列表中选择。
    返回 (名称, 路径) 表示选中，(None, None) 表示取消，
    ('__ADD__', None) 表示用户添加了新项目（调用方应刷新列表重试）。
    """
    while True:
        names = list(all_projects.keys())
        print(f"\n  可用项目:")
        for i, name in enumerate(names, 1):
            proj_path = all_projects[name].rstrip('\\')
            cur = current_dir.rstrip('\\') if current_dir else ""
            marker = " ← 当前" if proj_path == cur else ""
            print(f"    [{i}] {name}: {all_projects[name]}{marker}")
        print(f"    [A] 添加新项目...")
        print(f"    [0] 取消")

        choice = input("\n  请选择: ").strip()
        if choice == '0':
            return None, None
        if choice.upper() == 'A':
            add_project_interactive()
            all_projects = get_all_projects()  # 刷新列表
            continue
        try:
            idx = int(choice)
            name = names[idx - 1]
            return name, all_projects[name]
        except (ValueError, IndexError):
            print("  ✗ 无效选择，请重新输入")


# ============================================================
# 切换流程
# ============================================================

def do_switch(target_path):
    """执行切换操作：备份→修改→验证。"""
    # 步骤 1: 创建备份
    print(f"\n  [1/3] 创建备份...")
    for f in [EVARS_BAT, EVARS_INIT]:
        shutil.copy2(f, f + '.bak')
        print(f"    ✓ {os.path.basename(f)} → {os.path.basename(f)}.bak")

    # 步骤 2: 修改文件
    print(f"\n  [2/3] 修改配置文件...")
    success = True
    for f in [EVARS_BAT, EVARS_INIT]:
        ok, bak = set_project_dir_raw(f, target_path)
        if ok:
            print(f"    ✓ {os.path.basename(f)} 已更新")
        else:
            print(f"    ✗ {os.path.basename(f)} 修改失败 (未找到目标行)")
            success = False

    if not success:
        print("\n  ✗ 修改过程中出现问题，正在还原备份...")
        for f in [EVARS_BAT, EVARS_INIT]:
            restore_backup(f)
            print(f"    ✓ {os.path.basename(f)} 已还原")
        sys.exit(1)

    # 步骤 3: 验证
    print(f"\n  [3/3] 验证配置...")
    ok, output = validate_bat()

    if ok:
        new_bat, _  = get_project_dir(EVARS_BAT)
        new_init, _ = get_project_dir(EVARS_INIT)

        if new_bat != target_path or new_init != target_path:
            print(f"    ⚠ 值验证不一致，正在还原...")
            for f in [EVARS_BAT, EVARS_INIT]:
                restore_backup(f)
            print(f"\n  ❌ 切换失败，文件已还原。")
            sys.exit(1)

        print(f"    ✓ evars.bat 运行无报错")
        print(f"\n  ╔══════════════════════════════════════════════╗")
        print(f"  ║  ✅ 切换成功！                              ║")
        print(f"  ╠══════════════════════════════════════════════╣")
        print(f"  ║  evars.bat  :  {new_bat}")
        print(f"  ║  evars.init :  {new_init}")
        print(f"  ╚══════════════════════════════════════════════╝")
        cleanup_backups()
    else:
        print(f"    ✗ evars.bat 运行出错！")
        err_tail = output[-600:] if len(output) > 600 else output
        print(f"\n  ── 错误输出 ──")
        for line in err_tail.strip().split('\n'):
            print(f"  {line}")
        print(f"\n  正在还原备份...")
        for f in [EVARS_BAT, EVARS_INIT]:
            restore_backup(f)
            print(f"    ✓ {os.path.basename(f)} 已还原")
        print(f"\n  ❌ 切换失败 (验证未通过)，文件已完整还原。")
        sys.exit(1)


# ============================================================
# 交互式菜单
# ============================================================

def show_menu():
    """交互式主菜单，循环直到退出。"""
    global EVARS_BAT, EVARS_INIT

    while True:
        # 解析路径（静默，用缓存）
        ok = resolve_e3d_paths(verbose=False)
        if not ok:
            print("\n" + "=" * 60)
            print("  E3D 项目文件夹切换工具 (跨环境版)")
            print("=" * 60)
            print(f"\n  E3D: 未检测到 (选[5]重新检测)")
        else:
            bat_dir, _ = get_project_dir(EVARS_BAT)
            print("\n" + "=" * 60)
            print("  E3D 项目文件夹切换工具 (跨环境版)")
            print("=" * 60)
            print(f"\n  当前项目: {bat_dir}" if bat_dir else "\n  当前项目: (无法读取)")

        print(f"\n  ------------------------------------")
        print(f"    [1] 切换项目       [4] 列出所有项目")
        print(f"    [2] 添加项目       [5] 重新检测 E3D")
        print(f"    [3] 删除项目       [0] 退出")
        print(f"  ------------------------------------")
        choice = input("  请选择: ").strip()

        if choice == '1':
            if not EVARS_BAT:
                print("\n  ✗ 请先检测 E3D 安装路径 (选[5])")
                input("  按回车继续...")
                continue
            bat_dir, _ = get_project_dir(EVARS_BAT)
            all_projects = get_all_projects()
            target_name, target_path = choose_project(all_projects, bat_dir)
            if target_name and target_path:
                print(f"\n  目标: {target_name} ({target_path})")
                confirm = input("  确认切换? [Y/n]: ").strip().lower()
                if not confirm or confirm == 'y':
                    do_switch(target_path)
                else:
                    print("  已取消。")
            elif target_name is None:
                pass  # 用户取消
            input("\n  按回车继续...")

        elif choice == '2':
            add_project_interactive()
            input("\n  按回车继续...")

        elif choice == '3':
            remove_project_interactive()
            input("\n  按回车继续...")

        elif choice == '4':
            list_projects()
            input("\n  按回车继续...")

        elif choice == '5':
            print("\n  正在重新检测 E3D 安装路径...")
            ok = resolve_e3d_paths(force_detect=True, verbose=True)
            if ok:
                print(f"\n  ✓ 检测成功: {os.path.dirname(EVARS_BAT)}")
            else:
                print(f"\n  ✗ 未检测到，请手动指定:")
                bat = input("  evars.bat 路径: ").strip()
                init = input("  evars.init 路径: ").strip()
                if bat and init and os.path.exists(bat) and os.path.exists(init):
                    EVARS_BAT = bat
                    EVARS_INIT = init
                    from e3d_config import save_cache
                    save_cache({
                        "evars_bat": bat, "evars_init": init,
                        "install_dir": os.path.dirname(bat), "source": "manual",
                    })
                    print(f"  ✓ 已保存")
            input("\n  按回车继续...")

        elif choice == '0':
            print("\n  再见!")
            break

        else:
            print("\n  ✗ 无效选择")

def main():
    global EVARS_BAT, EVARS_INIT

    parser = argparse.ArgumentParser(description="E3D 项目文件夹切换工具 (跨环境)")
    parser.add_argument("-y", "--yes", action="store_true",
                        help="跳过确认提示，直接切换")
    parser.add_argument("--to", type=str, metavar="NAME",
                        help="切换到指定名称的项目")
    parser.add_argument("--dry-run", action="store_true",
                        help="仅显示当前状态，不做修改")
    parser.add_argument("--add", action="store_true",
                        help="添加新项目")
    parser.add_argument("--remove", action="store_true",
                        help="删除已保存的项目")
    parser.add_argument("--list", action="store_true",
                        help="列出所有已保存的项目")
    parser.add_argument("--detect", action="store_true",
                        help="重新检测 E3D 安装路径")
    parser.add_argument("--status", action="store_true",
                        help="紧凑显示当前状态（菜单用）")
    parser.add_argument("--cli", action="store_true",
                        help="使用命令行菜单模式（默认启动 Web 界面）")
    args = parser.parse_args()

    # --- 紧凑状态模式 ---
    if args.status:
        ok = resolve_e3d_paths(force_detect=args.detect, verbose=args.detect)
        if ok:
            bat_dir, _ = get_project_dir(EVARS_BAT)
            if bat_dir:
                print(f"  当前项目: {bat_dir}")
            else:
                print(f"  当前项目: (无法读取)")
        else:
            print(f"  E3D: 未检测到 (选[5]重新检测)")
        return

    # --- 管理命令（不需要完整路径解析）---
    if args.add:
        resolve_e3d_paths(verbose=False)
        add_project_interactive()
        return

    if args.remove:
        resolve_e3d_paths(verbose=False)
        remove_project_interactive()
        return

    if args.list:
        resolve_e3d_paths(verbose=False)
        list_projects()
        return

    # --- 无参数 → Web GUI，--cli → 终端菜单 ---
    if not args.to and not args.dry_run and not args.cli:
        start_web_ui()
        return

    if not args.to and not args.dry_run:
        # --cli 模式
        if args.detect:
            resolve_e3d_paths(force_detect=True, verbose=True)
        show_menu()
        return

    # --- 以下: --to 或 --dry-run 模式（需要完整路径解析）---
    print("=" * 60)
    print("  E3D 项目文件夹切换工具 (跨环境版)")
    print("=" * 60)

    ok = resolve_e3d_paths(force_detect=args.detect)
    if not ok:
        print(f"\n  ✗ 未检测到 AVEVA Everything3D 安装。")
        print(f"  已尝试: 注册表 → Everything SDK → 常见路径 → 全盘扫描")
        print(f"\n  请确认 E3D 已正确安装，或手动指定路径:")
        bat = input("  evars.bat 路径: ").strip()
        init = input("  evars.init 路径: ").strip()
        if bat and init and os.path.exists(bat) and os.path.exists(init):
            EVARS_BAT = bat
            EVARS_INIT = init
            from e3d_config import save_cache
            save_cache({
                "evars_bat": bat, "evars_init": init,
                "install_dir": os.path.dirname(bat), "source": "manual",
            })
        else:
            print("\n  ✗ 未提供有效路径，退出。")
            sys.exit(1)

    print(f"\n  E3D 安装路径: {os.path.dirname(EVARS_BAT)}")

    # --- 读取当前状态 ---
    bat_dir, bat_enc = get_project_dir(EVARS_BAT)
    init_dir, init_enc = get_project_dir(EVARS_INIT)

    if bat_dir is None:
        print(f"\n✗ 错误: 在 {EVARS_BAT} 中未找到 'set projects_dir=' 行")
        sys.exit(1)
    if init_dir is None:
        print(f"\n✗ 错误: 在 {EVARS_INIT} 中未找到 'set projects_dir=' 行")
        sys.exit(1)

    # --- 命令行直接切换 (--to NAME) ---
    if args.to:
        all_projects = get_all_projects()
        if args.to in all_projects:
            target_path = all_projects[args.to]
            print(f"\n  目标: {args.to} ({target_path})")
            if args.dry_run:
                print("\n  (仅查看模式，不做修改)")
                return
            if not args.yes:
                confirm = input("  确认切换? [Y/n]: ").strip().lower()
                if confirm and confirm != 'y':
                    print("\n  已取消。")
                    return
            do_switch(target_path)
        else:
            print(f"\n  ✗ 未找到项目 '{args.to}'")
            print(f"  可用项目: {', '.join(all_projects.keys())}")
            sys.exit(1)
        return

    # --- 仅查看 (--dry-run) ---
    if args.dry_run:
        print(f"\n┌─ 当前项目文件夹 ─────────────────────────────")
        print(f"│ evars.bat  :  {bat_dir}")
        print(f"│ evars.init :  {init_dir}")
        print(f"└──────────────────────────────────────────────")
        if bat_dir != init_dir:
            print("\n  ⚠ 警告: 两个文件的项目路径不一致！")
        all_projects = get_all_projects()
        print(f"\n  可用项目 ({len(all_projects)} 个):")
        for name, path in all_projects.items():
            marker = " ← 当前" if path.rstrip('\\') == bat_dir.rstrip('\\') else ""
            print(f"    • {name}: {path}{marker}")
        return


# ============================================================
# Web GUI — 内嵌 HTTP 服务器 + 现代暗色 UI
# ============================================================

WEB_HTML = r"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>E3D 项目切换</title>
<style>
:root{--bg:#0f1117;--card:#1a1d27;--border:#2a2d3a;--text:#e1e4ea;--muted:#8890a4;--green:#10b981;--red:#ef4444;--blue:#60a5fa;--radius:10px;--gap:14px}
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif;background:var(--bg);color:var(--text);min-height:100vh;padding:24px}
h1{font-size:20px;font-weight:600;margin-bottom:4px}
h2{font-size:13px;font-weight:500;color:var(--muted);text-transform:uppercase;letter-spacing:.5px;margin-bottom:var(--gap)}
.card{background:var(--card);border:1px solid var(--border);border-radius:var(--radius);padding:18px;margin-bottom:var(--gap)}
.header{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:24px}
.header h1 span{color:var(--green)}
.path{font-size:12px;color:var(--muted);margin-top:2px;word-break:break-all}
.row{display:flex;align-items:center;justify-content:space-between;padding:10px 0;border-bottom:1px solid var(--border)}
.row:last-child{border-bottom:none}
.row .name{font-weight:500;font-size:14px}
.row .dir{font-size:12px;color:var(--muted);margin-top:2px}
.row .current{color:var(--green);font-size:12px;margin-left:6px}
.btn{padding:6px 14px;border-radius:6px;border:none;font-size:13px;cursor:pointer;font-weight:500;transition:opacity .15s}
.btn:hover{opacity:.85}
.btn-switch{background:var(--green);color:#fff}
.btn-del{background:var(--red);color:#fff}
.btn-primary{background:var(--blue);color:#fff;padding:10px 20px;font-size:14px}
.btn-ghost{background:transparent;color:var(--muted);border:1px solid var(--border)}
.actions{display:flex;gap:8px}
.add-form{display:flex;gap:8px;margin-top:var(--gap)}
.add-form input{flex:1;padding:10px 12px;border-radius:6px;border:1px solid var(--border);background:var(--bg);color:var(--text);font-size:13px;outline:none}
.add-form input:focus{border-color:var(--blue)}
.empty{text-align:center;color:var(--muted);padding:24px;font-size:14px}
.toast{position:fixed;top:16px;right:16px;padding:12px 20px;border-radius:8px;font-size:13px;z-index:99;animation:slide .3s ease;display:none}
.toast.ok{background:#065f46;color:#d1fae5}
.toast.err{background:#7f1d1d;color:#fee2e2}
@keyframes slide{from{transform:translateX(100px);opacity:0}to{transform:translateX(0);opacity:1}}
.spin{display:inline-block;width:14px;height:14px;border:2px solid var(--border);border-top-color:var(--blue);border-radius:50%;animation:spin .6s linear infinite;margin-right:6px}
@keyframes spin{to{transform:rotate(360deg)}}
</style>
</head>
<body>
<div class="header">
<div><h1>⚡ E3D <span>项目切换</span></h1><div class="path" id="e3d-path">检测中...</div></div>
<button class="btn btn-ghost" onclick="detect()" title="重新检测 E3D">🔄</button>
</div>

<div class="card">
<h2>📌 当前项目</h2>
<div id="current-project" style="font-size:15px;word-break:break-all">加载中...</div>
</div>

<div class="card">
<h2>📁 项目列表</h2>
<div id="project-list"><div class="empty">加载中...</div></div>
<div class="add-form">
<input id="add-name" placeholder="项目名称" autocomplete="off">
<input id="add-path" placeholder="项目路径 (如 D:\Projects\)" autocomplete="off">
<button class="btn btn-primary" onclick="addProject()">＋ 添加</button>
</div>
</div>

<div id="toast" class="toast"></div>

<script>
async function api(path,body){try{const r=await fetch(path,body?{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}:{});return r.json()}catch(e){return{error:String(e)}}}
function toast(msg,ok){const t=document.getElementById('toast');t.textContent=msg;t.className='toast '+(ok?'ok':'err');t.style.display='block';setTimeout(()=>t.style.display='none',2500)}

async function load(){
  const d=await api('/api/status');
  document.getElementById('e3d-path').textContent=d.e3d_path||'未检测到 E3D';
  document.getElementById('current-project').textContent=d.current||'(未设置)';
  const list=document.getElementById('project-list');
  if(!d.projects||!Object.keys(d.projects).length){list.innerHTML='<div class="empty">暂无项目，请添加</div>';return}
  let h='';
  for(const[name,path]of Object.entries(d.projects)){
    const isCur=path.replace(/\\+$/,'')===((d.current||'').replace(/\\+$/,''));
    h+=`<div class="row">
      <div><div class="name">${esc(name)}${isCur?'<span class="current">← 当前</span>':''}</div><div class="dir">${esc(path)}</div></div>
      <div class="actions">
        <button class="btn btn-switch" onclick="switchProject('${esc(name)}')">切换</button>
        <button class="btn btn-del" onclick="removeProject('${esc(name)}')">删除</button>
      </div></div>`;
  }
  list.innerHTML=h;
}
function esc(s){return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')}
async function switchProject(name){const r=await api('/api/switch',{name});toast(r.error||'✓ 切换成功',!r.error);load()}
async function removeProject(name){if(!confirm('确认删除 "'+name+'"?'))return;const r=await api('/api/remove',{name});toast(r.error||'✓ 已删除',!r.error);load()}
async function addProject(){const name=document.getElementById('add-name').value.trim();const path=document.getElementById('add-path').value.trim();if(!name||!path){toast('请填写名称和路径',false);return}const r=await api('/api/add',{name,path});toast(r.error||'✓ 已添加',!r.error);if(!r.error){document.getElementById('add-name').value='';document.getElementById('add-path').value='';load()}}
async function detect(){const r=await api('/api/detect');toast(r.error||'✓ 检测完成',!r.error);load()}
load();
</script>
</body>
</html>"""


def _get_free_port(start=8800):
    """获取一个空闲端口。"""
    for port in range(start, start + 100):
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.bind(('127.0.0.1', port))
                return port
        except OSError:
            continue
    return 8800


class _WebHandler(http.server.BaseHTTPRequestHandler):
    """Web GUI 的 HTTP 请求处理器。"""

    def log_message(self, format, *args):
        pass  # 静默日志

    def _send_json(self, data, status=200):
        body = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', len(body))
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self):
        length = int(self.headers.get('Content-Length', 0))
        return self.rfile.read(length) if length else b''

    def do_GET(self):
        if self.path == '/' or self.path == '/index.html':
            body = WEB_HTML.encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', len(body))
            self.end_headers()
            self.wfile.write(body)
        elif self.path == '/api/status':
            self._handle_status()
        else:
            self._send_json({'error': 'not found'}, 404)

    def do_POST(self):
        if self.path == '/api/switch':
            self._handle_switch()
        elif self.path == '/api/add':
            self._handle_add()
        elif self.path == '/api/remove':
            self._handle_remove()
        elif self.path == '/api/detect':
            self._handle_detect()
        else:
            self._send_json({'error': 'not found'}, 404)

    def _handle_status(self):
        global EVARS_BAT, EVARS_INIT
        resolve_e3d_paths(verbose=False)
        current = None
        if EVARS_BAT:
            d, _ = get_project_dir(EVARS_BAT)
            current = d
        projects = get_all_projects()
        e3d_path = os.path.dirname(EVARS_BAT) if EVARS_BAT else '未检测到'
        self._send_json({
            'current': current,
            'e3d_path': e3d_path,
            'projects': projects,
        })

    def _handle_switch(self):
        global EVARS_BAT, EVARS_INIT
        try:
            body = json.loads(self._read_body())
            name = body.get('name', '')
        except Exception:
            return self._send_json({'error': '无效请求'}, 400)

        resolve_e3d_paths(verbose=False)
        if not EVARS_BAT:
            return self._send_json({'error': '未检测到 E3D 安装'}, 500)

        projects = get_all_projects()
        if name not in projects:
            return self._send_json({'error': f'未找到项目: {name}'}, 404)

        try:
            do_switch(projects[name])
            self._send_json({'ok': True})
        except SystemExit as e:
            if e.code and e.code != 0:
                self._send_json({'error': '切换失败，文件已自动还原'}, 500)
            else:
                self._send_json({'ok': True})

    def _handle_add(self):
        try:
            body = json.loads(self._read_body())
            name = body.get('name', '').strip()
            path = body.get('path', '').strip()
        except Exception:
            return self._send_json({'error': '无效请求'}, 400)

        if not name or not path:
            return self._send_json({'error': '名称和路径不能为空'}, 400)

        if not path.endswith('\\'):
            path += '\\'

        projects = load_projects()
        projects[name] = path
        save_projects(projects)
        self._send_json({'ok': True})

    def _handle_remove(self):
        try:
            body = json.loads(self._read_body())
            name = body.get('name', '')
        except Exception:
            return self._send_json({'error': '无效请求'}, 400)

        projects = load_projects()
        if name not in projects:
            return self._send_json({'error': f'未找到项目: {name}'}, 404)

        del projects[name]
        save_projects(projects)
        self._send_json({'ok': True})

    def _handle_detect(self):
        global EVARS_BAT, EVARS_INIT
        ok = resolve_e3d_paths(force_detect=True, verbose=False)
        if ok:
            self._send_json({'ok': True, 'path': os.path.dirname(EVARS_BAT)})
        else:
            self._send_json({'error': '未检测到 E3D 安装'})


def start_web_ui():
    """启动 Web GUI 服务器并在浏览器中打开。"""
    port = _get_free_port()
    server = http.server.HTTPServer(('127.0.0.1', port), _WebHandler)
    url = f'http://127.0.0.1:{port}'

    # 后台线程运行服务器
    t = threading.Thread(target=server.serve_forever, daemon=True)
    t.start()

    # 打开浏览器
    print(f"\n  启动 Web 界面: {url}")
    webbrowser.open(url)

    print("  按 Enter 退出...")
    try:
        input()
    except (KeyboardInterrupt, EOFError):
        pass

    server.shutdown()
    print("  已退出。")


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print("\n\n  用户中断。正在还原备份...")
        for f in [EVARS_BAT, EVARS_INIT]:
            if EVARS_BAT and EVARS_INIT:
                restore_backup(f)
        print("  文件已还原。")
        sys.exit(1)
    except Exception as e:
        print(f"\n  ✗ 意外错误: {e}")
        import traceback
        traceback.print_exc()
        print("  正在还原备份...")
        for f in [EVARS_BAT, EVARS_INIT]:
            if EVARS_BAT and EVARS_INIT:
                restore_backup(f)
        sys.exit(1)
