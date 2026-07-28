#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
E3D 安装路径自动检测模块
========================
多策略查找 AVEVA Everything3D 的安装路径，定位 evars.bat 和 evars.init。

检测策略（按优先顺序）：
  1. 本地缓存 (e3d_paths.json) — 上次成功检测的结果
  2. 注册表查找 — 搜索 HKLM Uninstall 中的 AVEVA Everything3D 条目
  3. Everything SDK — 调用 Everything 搜索 evars.bat（闪电速度）
  4. 常见路径扫描 — 检查 D:/C:/E:/F: 盘下的 AVEVA 目录
  5. 全盘遍历 — 广度优先搜索所有盘符（最慢，兜底）

输出:
  成功: {"evars_bat": "D:\\...\\evars.bat", "evars_init": "D:\\...\\evars.init",
          "install_dir": "D:\\...\\Everything3D3.1", "projects_dir": "D:\\...\\Projects",
          "source": "registry|cache|everything|common|scan"}
  失败: None
"""

import os
import sys
import json
import re
import subprocess
import ctypes
from pathlib import Path

# 缓存文件与 .exe/脚本同目录（PyInstaller 兼容）
if getattr(sys, 'frozen', False):
    # 打包后的 .exe — 使用 .exe 所在目录
    SCRIPT_DIR = os.path.dirname(sys.executable)
else:
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CACHE_FILE = os.path.join(SCRIPT_DIR, "e3d_paths.json")

# ── 工具函数 ──────────────────────────────────────────────

def _winreg_query(key_path, value_name=None):
    """查询 Windows 注册表，返回字符串或 None。兼容 32/64 位。"""
    import winreg
    hives = {
        "HKLM": winreg.HKEY_LOCAL_MACHINE,
        "HKCU": winreg.HKEY_CURRENT_USER,
    }
    parts = key_path.split("\\", 1)
    if len(parts) != 2 or parts[0] not in hives:
        return None
    hive, subkey = hives[parts[0]], parts[1]

    # 64-bit 视图
    for access in [winreg.KEY_READ | winreg.KEY_WOW64_64KEY,
                   winreg.KEY_READ | winreg.KEY_WOW64_32KEY]:
        try:
            with winreg.OpenKey(hive, subkey, 0, access) as key:
                if value_name is None:
                    # 返回所有值
                    result = {}
                    i = 0
                    while True:
                        try:
                            n, v, t = winreg.EnumValue(key, i)
                            result[n] = v
                            i += 1
                        except OSError:
                            break
                    return result
                else:
                    return winreg.QueryValueEx(key, value_name)[0]
        except OSError:
            continue
    return None


def _winreg_enum_subkeys(key_path):
    """枚举注册表子键名列表。"""
    import winreg
    parts = key_path.split("\\", 1)
    if len(parts) != 2:
        return []
    hive_name, subkey = parts[0], parts[1]
    hives = {"HKLM": winreg.HKEY_LOCAL_MACHINE, "HKCU": winreg.HKEY_CURRENT_USER}
    if hive_name not in hives:
        return []

    names = []
    for access in [winreg.KEY_READ | winreg.KEY_WOW64_64KEY,
                   winreg.KEY_READ | winreg.KEY_WOW64_32KEY]:
        try:
            with winreg.OpenKey(hives[hive_name], subkey, 0, access) as key:
                i = 0
                while True:
                    try:
                        names.append(winreg.EnumKey(key, i))
                        i += 1
                    except OSError:
                        break
        except OSError:
            continue
    return names


# ── 策略 1: 缓存 ─────────────────────────────────────────

def detect_from_cache():
    """从本地缓存文件读取上次检测结果。"""
    if not os.path.exists(CACHE_FILE):
        return None
    try:
        with open(CACHE_FILE, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except (json.JSONDecodeError, IOError):
        return None

    bat = data.get("evars_bat", "")
    init = data.get("evars_init", "")
    if bat and init and os.path.exists(bat) and os.path.exists(init):
        data["source"] = "cache"
        return data
    return None


# ── 策略 2: 注册表 ───────────────────────────────────────

def detect_from_registry():
    """从注册表查找 AVEVA Everything3D 安装信息。"""
    uninstall_roots = [
        r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        r"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ]

    for root in uninstall_roots:
        subkeys = _winreg_enum_subkeys(root)
        for sk in subkeys:
            full = root + "\\" + sk
            values = _winreg_query(full)
            if not values:
                continue

            display_name = values.get("DisplayName", "")
            if not re.search(r'AVEVA\s+Everything\s*3D', display_name, re.IGNORECASE):
                continue

            install_dir = values.get("InstallLocation", "")
            if not install_dir:
                continue

            install_dir = install_dir.rstrip("\\/")
            evars_bat = os.path.join(install_dir, "evars.bat")
            evars_init = os.path.join(install_dir, "evars.init")

            if os.path.exists(evars_bat) and os.path.exists(evars_init):
                result = {
                    "evars_bat": evars_bat,
                    "evars_init": evars_init,
                    "install_dir": install_dir,
                    "e3d_version": display_name,
                }
                # 同时尝试找 Projects 目录
                projects_dir = _find_projects_from_registry()
                if projects_dir:
                    result["projects_dir"] = projects_dir
                result["source"] = "registry"
                return result

    return None


def _find_projects_from_registry():
    """从注册表查找 AVEVA Projects 安装路径。"""
    uninstall_roots = [
        r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        r"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ]
    for root in uninstall_roots:
        subkeys = _winreg_enum_subkeys(root)
        for sk in subkeys:
            full = root + "\\" + sk
            values = _winreg_query(full)
            if not values:
                continue
            dn = values.get("DisplayName", "")
            if re.search(r'AVEVA.*Projects', dn, re.IGNORECASE):
                loc = values.get("InstallLocation", "")
                if loc and os.path.exists(loc):
                    return loc.rstrip("\\/")
    return None


# ── 策略 3: Everything SDK ────────────────────────────────

def detect_from_everything():
    """
    通过 Everything SDK 极速搜索 evars.bat。
    需要 Everything 正在运行且能找到 Everything64.dll 或 Everything32.dll。
    """
    dll_paths = [
        os.path.expandvars(r"%ProgramFiles%\Everything\Everything64.dll"),
        os.path.expandvars(r"%ProgramFiles(x86)%\Everything\Everything64.dll"),
        os.path.expandvars(r"%ProgramFiles%\Everything\Everything32.dll"),
        os.path.expandvars(r"%ProgramFiles(x86)%\Everything\Everything32.dll"),
        r"C:\Everything\Everything64.dll",
    ]
    # 也尝试从 Everything.exe 进程路径推断
    dll = _find_everything_dll(dll_paths)
    if not dll:
        # 尝试从进程路径找
        dll = _find_everything_dll_from_process()
    if not dll:
        return None

    try:
        everything = ctypes.WinDLL(dll)
    except OSError:
        return None

    # Everything SDK 函数声明
    # Everything_SetSearchW
    everything.Everything_SetSearchW.argtypes = [ctypes.c_wchar_p]
    everything.Everything_SetSearchW.restype = None
    # Everything_QueryW
    everything.Everything_QueryW.argtypes = [ctypes.c_bool]
    everything.Everything_QueryW.restype = ctypes.c_bool
    # Everything_GetNumResults
    everything.Everything_GetNumResults.restype = ctypes.c_uint32
    # Everything_GetResultFullPathNameW
    everything.Everything_GetResultFullPathNameW.argtypes = [ctypes.c_uint32, ctypes.c_wchar_p, ctypes.c_uint32]
    everything.Everything_GetResultFullPathNameW.restype = None

    # 搜索 evars.bat
    everything.Everything_SetSearchW("evars.bat")
    if not everything.Everything_QueryW(True):
        return None

    num = everything.Everything_GetNumResults()
    buf = ctypes.create_unicode_buffer(260)

    for i in range(min(num, 50)):
        everything.Everything_GetResultFullPathNameW(i, buf, 260)
        path = buf.value
        if not path:
            continue
        parent = os.path.dirname(path)
        evars_init = os.path.join(parent, "evars.init")
        # 优先找路径中还包含 Everything3D 的（排除 PDMS/Plant 等其他 AVEVA 产品）
        if os.path.exists(evars_init) and "Everything3D" in parent:
            return {
                "evars_bat": path,
                "evars_init": evars_init,
                "install_dir": parent,
                "source": "everything",
            }

    # 宽松匹配：第一个同时有 evars.bat 和 evars.init 的目录
    for i in range(min(num, 50)):
        everything.Everything_GetResultFullPathNameW(i, buf, 260)
        path = buf.value
        if not path:
            continue
        parent = os.path.dirname(path)
        evars_init = os.path.join(parent, "evars.init")
        if os.path.exists(evars_init):
            return {
                "evars_bat": path,
                "evars_init": evars_init,
                "install_dir": parent,
                "source": "everything",
            }

    return None


def _find_everything_dll(paths):
    for p in paths:
        if os.path.exists(p):
            return p
    return None


def _find_everything_dll_from_process():
    """从运行的 Everything.exe 进程路径推断 DLL 位置。"""
    try:
        import subprocess
        result = subprocess.run(
            ['wmic', 'process', 'where', 'name="Everything.exe"', 'get', 'ExecutablePath'],
            capture_output=True, text=True, timeout=5
        )
        for line in result.stdout.splitlines():
            line = line.strip()
            if line.lower().endswith('everything.exe'):
                d = os.path.dirname(line)
                for name in ['Everything64.dll', 'Everything32.dll']:
                    p = os.path.join(d, name)
                    if os.path.exists(p):
                        return p
    except Exception:
        pass
    return None


# ── 策略 4: 常见路径扫描 ─────────────────────────────────

def detect_from_common_paths():
    """在常见的 AVEVA 安装路径下搜索。"""
    patterns = [
        r"D:\AVEVA\Everything3D*",
        r"C:\AVEVA\Everything3D*",
        r"E:\AVEVA\Everything3D*",
        r"F:\AVEVA\Everything3D*",
    ]
    for pat in patterns:
        import glob
        for d in sorted(glob.glob(pat), reverse=True):
            evars_bat = os.path.join(d, "evars.bat")
            evars_init = os.path.join(d, "evars.init")
            if os.path.exists(evars_bat) and os.path.exists(evars_init):
                return {
                    "evars_bat": evars_bat,
                    "evars_init": evars_init,
                    "install_dir": d,
                    "source": "common",
                }
    return None


# ── 策略 5: 全盘遍历（兜底） ──────────────────────────────

def detect_from_drive_scan():
    """
    广度优先遍历所有盘符上的 AVEVA 目录。
    作为最后的兜底手段，可能较慢（通常 10-30 秒）。
    """
    # 获取可用盘符
    drives = []
    try:
        import string
        for letter in string.ascii_uppercase:
            d = f"{letter}:\\"
            if os.path.exists(d):
                drives.append(d)
    except Exception:
        drives = ["C:\\", "D:\\"]

    # 优先扫描根目录下的 AVEVA 目录
    for drive in drives:
        aveva_root = os.path.join(drive, "AVEVA")
        if not os.path.isdir(aveva_root):
            continue
        try:
            for entry in os.listdir(aveva_root):
                full = os.path.join(aveva_root, entry)
                if not os.path.isdir(full):
                    continue
                if "everything3d" not in entry.lower():
                    continue
                evars_bat = os.path.join(full, "evars.bat")
                evars_init = os.path.join(full, "evars.init")
                if os.path.exists(evars_bat) and os.path.exists(evars_init):
                    return {
                        "evars_bat": evars_bat,
                        "evars_init": evars_init,
                        "install_dir": full,
                        "source": "scan",
                    }
        except PermissionError:
            continue

    # 广度遍历 AVEVA 目录下的所有子目录
    for drive in drives:
        aveva_root = os.path.join(drive, "AVEVA")
        if not os.path.isdir(aveva_root):
            continue
        try:
            for root, dirs, _ in os.walk(aveva_root):
                # 限制深度，2层足够（D:\AVEVA\Everything3D3.1\）
                depth = root[len(aveva_root):].count(os.sep)
                if depth > 2:
                    dirs.clear()
                    continue
                evars_bat = os.path.join(root, "evars.bat")
                evars_init = os.path.join(root, "evars.init")
                if os.path.exists(evars_bat) and os.path.exists(evars_init):
                    return {
                        "evars_bat": evars_bat,
                        "evars_init": evars_init,
                        "install_dir": root,
                        "source": "scan",
                    }
        except PermissionError:
            continue

    return None


# ── 缓存管理 ──────────────────────────────────────────────

def save_cache(config):
    """保存检测结果到本地缓存。"""
    data = {
        "evars_bat": config.get("evars_bat", ""),
        "evars_init": config.get("evars_init", ""),
        "install_dir": config.get("install_dir", ""),
        "projects_dir": config.get("projects_dir", ""),
        "e3d_version": config.get("e3d_version", ""),
    }
    with open(CACHE_FILE, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


# ── 主检测入口 ────────────────────────────────────────────

def detect_e3d(force=False, verbose=True):
    """
    自动检测 E3D 安装路径。

    参数:
      force: 忽略缓存，强制重新检测
      verbose: 是否打印检测日志

    返回:
      成功: {"evars_bat", "evars_init", "install_dir", "source", ...}
      失败: None
    """
    strategies = [
        ("缓存",     detect_from_cache if not force else (lambda: None)),
        ("注册表",   detect_from_registry),
        ("Everything SDK", detect_from_everything),
        ("常见路径", detect_from_common_paths),
        ("全盘扫描", detect_from_drive_scan),
    ]

    for name, fn in strategies:
        if verbose:
            print(f"  [{name}] 检测中...", end=" ", flush=True)
        try:
            result = fn()
        except Exception as e:
            if verbose:
                print(f"出错: {e}")
            continue

        if result:
            if verbose:
                print(f"✓ 找到!\n    路径: {result['install_dir']}")
            save_cache(result)
            return result
        else:
            if verbose:
                print("未找到")

    if verbose:
        print("\n  ✗ 所有检测策略均未找到 E3D 安装。")
        print("  请确保 AVEVA Everything3D 已正确安装。")
    return None


def get_e3d_paths(force=False, verbose=True):
    """
    获取 E3D 配置路径。先读缓存，缓存失效自动重新检测。

    返回 (evars_bat, evars_init) 或 (None, None)。
    """
    # 先试缓存
    if not force:
        cached = detect_from_cache()
        if cached:
            if verbose:
                print(f"  [缓存] 使用已保存的配置")
                print(f"    路径: {cached['install_dir']}")
            return cached["evars_bat"], cached["evars_init"]

    # 重新检测
    result = detect_e3d(force=force, verbose=verbose)
    if result:
        return result["evars_bat"], result["evars_init"]
    return None, None


# ── CLI 独立运行 ──────────────────────────────────────────

if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="E3D 路径自动检测工具")
    parser.add_argument("--force", action="store_true", help="强制重新检测（忽略缓存）")
    parser.add_argument("--json", action="store_true", help="以 JSON 格式输出结果")
    args = parser.parse_args()

    result = detect_e3d(force=args.force)

    if result and args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    elif result:
        print(f"\n✓ 检测成功 (来源: {result['source']})")
        print(f"  安装目录: {result['install_dir']}")
        print(f"  evars.bat: {result['evars_bat']}")
        print(f"  evars.init: {result['evars_init']}")
        if result.get("projects_dir"):
            print(f"  Projects:  {result['projects_dir']}")
    else:
        print("\n✗ 未找到 E3D 安装。")
        sys.exit(1)
