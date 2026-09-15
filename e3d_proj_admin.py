#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
E3D 项目优雅创建与下线归档管理模块 (E3D Project Lifecycle Admin)
============================================================
提供符合 AVEVA E3D 3.1 / 2.1 规范的工程生命周期管理：
- 优雅新建：标准 3 位代号派生、6~13大子目录骨架、种子库映射克隆、evars 生成、ProjectInfo.xml & projects.ini 自动登记
- 优雅下线：DABACON 锁文件安全检测拦截、冷备 ZIP 压缩归档、消除 ProjectInfo.xml / custom_evars.bat / projects.ini 幽灵残留、物理清理
"""

import os
import re
import shutil
import zipfile
import datetime
import xml.etree.ElementTree as ET
import e3d_config
import e3d_util as util

# 默认归档保存目录
DEFAULT_ARCHIVE_DIR = r"D:\AVEVA\Archive"

def get_default_projects_dir():
    """获取当前系统主要项目根目录（优先使用 E3D3.1 检测结果）"""
    try:
        cfg = e3d_config.detect_e3d()
        if cfg and cfg.get("projects_dir") and os.path.exists(cfg["projects_dir"]):
            return cfg["projects_dir"]
    except Exception:
        pass
    for candidate in [r"D:\AVEVA\Projects\E3D3.1", r"D:\AVEVA\Projects", r"C:\AVEVA\Projects"]:
        if os.path.exists(candidate):
            return candidate
    return r"D:\AVEVA\Projects"


def list_clone_templates(projects_dir=None):
    """扫描可用于克隆的工程模板列表"""
    pdir = projects_dir or get_default_projects_dir()
    templates = []
    if not os.path.isdir(pdir):
        return templates

    try:
        for entry in os.scandir(pdir):
            if entry.is_dir():
                sub_000 = False
                evars_file = False
                for item in os.scandir(entry.path):
                    if item.is_dir() and item.name.lower().endswith("000"):
                        sub_000 = True
                    if item.is_file() and item.name.lower().startswith("evars") and item.name.lower().endswith(".bat"):
                        evars_file = True
                if sub_000 or evars_file:
                    templates.append({
                        "name": entry.name,
                        "path": entry.path,
                        "has_000": sub_000,
                        "has_evars": evars_file
                    })
    except Exception:
        pass
    return templates


def inspect_project(proj_path):
    """
    深度体检指定项目的物理健康状况与锁文件
    返回字典包含: code, size_mb, size_human, lock_files, is_locked, exists
    """
    proj_path = os.path.abspath(proj_path)
    if not os.path.exists(proj_path):
        return {
            "exists": False,
            "path": proj_path,
            "code": os.path.basename(proj_path).upper(),
            "size_mb": 0,
            "size_human": "0 MB",
            "lock_files": [],
            "is_locked": False,
            "file_count": 0
        }

    code = os.path.basename(proj_path).upper()
    lock_files = []
    total_bytes = 0
    file_count = 0

    try:
        stack = [proj_path]
        while stack:
            curr = stack.pop()
            try:
                with os.scandir(curr) as it:
                    for entry in it:
                        try:
                            if entry.is_dir(follow_symlinks=False):
                                stack.append(entry.path)
                            elif entry.is_file(follow_symlinks=False):
                                file_count += 1
                                total_bytes += entry.stat(follow_symlinks=False).st_size
                                ext = os.path.splitext(entry.name)[1].lower()
                                if ext in ('.lck', '.lok'):
                                    lock_files.append(entry.path)
                        except OSError:
                            pass
            except OSError:
                pass
    except Exception:
        pass

    size_mb = round(total_bytes / (1024 * 1024), 2)
    size_human = f"{round(size_mb / 1024, 2)} GB" if size_mb > 1024 else f"{size_mb} MB"

    return {
        "exists": True,
        "path": proj_path,
        "code": code,
        "size_mb": size_mb,
        "size_human": size_human,
        "lock_files": lock_files,
        "is_locked": len(lock_files) > 0,
        "file_count": file_count
    }


def create_project(code, name, root_dir=None, template_dir=None, register_e3d=True):
    """
    优雅新建 AVEVA E3D 工程
    :param code: 3 位大写字母/数字 (如 PRJ)
    :param name: 工程全称或描述
    :param root_dir: 存放根目录 (默认自动探测 projects_dir)
    :param template_dir: 可选克隆源模板路径
    :param register_e3d: 是否自动写入 ProjectInfo.xml & projects.ini
    """
    code = (code or "").strip().upper()
    name = (name or code).strip()

    if not (2 <= len(code) <= 5) or not re.match(r'^[A-Z0-9]{2,5}$', code):
        raise ValueError(f"项目代号 [{code}] 无效！必须为 2~5 位英文字母或数字（例如 PRJ, ABC, M01）。")

    root = os.path.abspath(root_dir or get_default_projects_dir())
    os.makedirs(root, exist_ok=True)
    target_dir = os.path.join(root, code)
    os.makedirs(target_dir, exist_ok=True)

    lc = code.lower()
    subdirs = [
        f"{lc}000", f"{lc}iso", f"{lc}dwg", f"{lc}mac", f"{lc}pic",
        f"{lc}dflts", f"{lc}dia", f"{lc}etm", f"{lc}gcd", f"{lc}info",
        f"{lc}psi", f"{lc}ste", f"{lc}tpl"
    ]
    for sub in subdirs:
        os.makedirs(os.path.join(target_dir, sub), exist_ok=True)

    # 模板派生克隆
    if template_dir and os.path.isdir(template_dir):
        src_code = os.path.basename(template_dir.rstrip(r"\/"))
        src_lc = src_code.lower()

        # 1. 复制 000 数据库并自动重命名映射
        for item in os.scandir(template_dir):
            if item.is_dir() and item.name.lower().endswith("000"):
                dest_000 = os.path.join(target_dir, f"{lc}000")
                for subitem in os.scandir(item.path):
                    if subitem.is_file():
                        orig_fn = subitem.name
                        new_fn = re.sub(f"^{re.escape(src_lc)}", lc, orig_fn, flags=re.IGNORECASE)
                        shutil.copy2(subitem.path, os.path.join(dest_000, new_fn))

            # 2. 复制出图与宏配置
            elif item.is_dir() and any(k in item.name.lower() for k in ("dwg", "mac", "iso", "pic", "dflts")):
                target_sub = re.sub(f"^{re.escape(src_lc)}", lc, item.name, flags=re.IGNORECASE)
                dest_sub_path = os.path.join(target_dir, target_sub)
                if os.path.exists(dest_sub_path):
                    shutil.copytree(item.path, dest_sub_path, dirs_exist_ok=True)

    # 生成 evars<CODE>.bat
    evars_bat = os.path.join(target_dir, f"evars{code}.bat")
    evars_content = f"""@echo off
rem ============================================================
rem   AVEVA Everything3D Project Environment: {code}
rem   Auto-generated by SEP Project Hub on {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}
rem ============================================================
SET {code}000=%projects_dir%{code}\\{lc}000
SET {code}MAC=%projects_dir%{code}\\{lc}mac
SET {code}ISO=%projects_dir%{code}\\{lc}iso
SET {code}PIC=%projects_dir%{code}\\{lc}pic
SET {code}DFLTS=%projects_dir%{code}\\{lc}dflts
SET {code}DIA=%projects_dir%{code}\\{lc}dia
SET {code}TPL=%projects_dir%{code}\\{lc}tpl
SET {code}STE=%projects_dir%{code}\\{lc}ste
SET {code}INFO=%projects_dir%{code}\\{lc}info
SET {code}REPORTS=%projects_dir%{code}\\{lc}info\\REPORTS\\{lc}
SET {code}PSI=%projects_dir%{code}\\{lc}psi
SET {code}GCD=%projects_dir%{code}\\{lc}gcd
SET {code}DATA=%projects_dir%{code}\\{lc}dflts\\Data\\
SET {code}DWG=%projects_dir%{code}\\{lc}dwg
SET {code}ETM=%projects_dir%{code}\\{lc}etm
SET {code}000ID={code}
"""
    with open(evars_bat, "w", encoding="gbk", errors="replace") as f:
        f.write(evars_content)

    if register_e3d:
        _register_to_project_info_xml(root, code, name, target_dir)
        _register_to_projects_ini(code, name, target_dir)

    return {
        "ok": True,
        "code": code,
        "name": name,
        "path": target_dir,
        "evars_bat": evars_bat,
        "message": f"项目 [{code}] 已成功规范化创建并完成系统注册！"
    }


def decommission_project(proj_path, archive_dir=DEFAULT_ARCHIVE_DIR, do_archive=True, do_unregister=True, do_delete=True, force=False):
    """
    优雅下线 AVEVA E3D 工程
    :param proj_path: 项目物理目录
    :param archive_dir: 冷备目标目录
    :param do_archive: 是否执行 ZIP 压缩打包冷备
    :param do_unregister: 是否从 ProjectInfo.xml、custom_evars.bat 及 projects.ini 中注销
    :param do_delete: 是否删除物理文件夹
    :param force: 若存在锁文件是否强制执行
    """
    proj_path = os.path.abspath(proj_path)
    info = inspect_project(proj_path)
    code = info["code"]

    if info["is_locked"] and not force:
        lock_names = ", ".join([os.path.basename(x) for x in info["lock_files"][:3]])
        raise RuntimeError(f"检测到项目 [{code}] 存在活跃锁文件 ({lock_names})，可能有用户正在使用或异常崩溃残留，操作已自动拦截！")

    archived_file = None
    if do_archive and info["exists"]:
        os.makedirs(archive_dir, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        archived_file = os.path.join(archive_dir, f"{code}_Backup_{ts}.zip")
        with zipfile.ZipFile(archived_file, "w", zipfile.ZIP_DEFLATED) as zf:
            for root, dirs, files in os.walk(proj_path):
                for f in files:
                    fp = os.path.join(root, f)
                    arcname = os.path.relpath(fp, proj_path)
                    zf.write(fp, arcname)

    if do_unregister:
        parent_dir = os.path.dirname(proj_path)
        _unregister_from_project_info_xml(parent_dir, code)
        _unregister_from_custom_evars(parent_dir, code)
        _unregister_from_projects_ini(code)

    if do_delete and info["exists"]:
        shutil.rmtree(proj_path, ignore_errors=True)

    return {
        "ok": True,
        "code": code,
        "archived_file": archived_file,
        "deleted": do_delete,
        "unregistered": do_unregister,
        "message": f"项目 [{code}] 已成功优雅下线处理完毕！"
    }


# ── 内部系统注册/注销辅助 ──────────────────────────────────────────────

def _register_to_project_info_xml(projects_dir, code, name, address):
    """将项目注入到 E3D 3.1 的 ProjectInfo.xml 中"""
    xml_path = os.path.join(projects_dir, "ProjectInfo.xml")
    try:
        if os.path.exists(xml_path):
            tree = ET.parse(xml_path)
            root = tree.getroot()
        else:
            root = ET.Element("ProjectList", {
                "xmlns:xsd": "http://www.w3.org/2001/XMLSchema",
                "xmlns:xsi": "http://www.w3.org/2001/XMLSchema-instance",
                "xmlns": "www.aveva.com"
            })
            tree = ET.ElementTree(root)

        # 检查是否已存在对应 Code
        for p in root.findall(".//Project") or root.findall("{www.aveva.com}Project"):
            c = p.find("Code") or p.find("{www.aveva.com}Code")
            if c is not None and c.text and c.text.strip().upper() == code:
                # 更新
                n = p.find("Name") or p.find("{www.aveva.com}Name")
                if n is not None: n.text = name
                a = p.find("Address") or p.find("{www.aveva.com}Address")
                if a is not None: a.text = address
                tree.write(xml_path, encoding="utf-8", xml_declaration=True)
                return

        # 新增
        ns = ""
        if "www.aveva.com" in root.tag:
            ns = "{www.aveva.com}"
        new_proj = ET.SubElement(root, f"{ns}Project")
        for tag, val in [("Project", code), ("Code", code), ("Address", address),
                         ("Number", code), ("Name", name), ("Description", name),
                         ("Message", ""), ("ApplicationType", "false")]:
            el = ET.SubElement(new_proj, f"{ns}{tag}")
            el.text = val

        tree.write(xml_path, encoding="utf-8", xml_declaration=True)
    except Exception:
        pass


def _unregister_from_project_info_xml(projects_dir, code):
    """从 ProjectInfo.xml 中彻底剔除项目"""
    xml_path = os.path.join(projects_dir, "ProjectInfo.xml")
    if not os.path.exists(xml_path):
        return
    try:
        tree = ET.parse(xml_path)
        root = tree.getroot()
        changed = False
        for p in list(root):
            c = p.find("Code") or p.find("{www.aveva.com}Code") or p.find("Project") or p.find("{www.aveva.com}Project")
            if c is not None and c.text and c.text.strip().upper() == code:
                root.remove(p)
                changed = True
        if changed:
            tree.write(xml_path, encoding="utf-8", xml_declaration=True)
    except Exception:
        pass


def _unregister_from_custom_evars(projects_dir, code):
    """从 custom_evars.bat 中清除指定项目的 call 调用"""
    bat_path = os.path.join(projects_dir, "custom_evars.bat")
    if not os.path.exists(bat_path):
        return
    try:
        with open(bat_path, "r", encoding="gbk", errors="ignore") as f:
            lines = f.readlines()
        pattern = re.compile(rf'evars{code}\.bat|\\{code}\\', re.IGNORECASE)
        new_lines = [l for l in lines if not pattern.search(l)]
        if len(new_lines) != len(lines):
            with open(bat_path, "w", encoding="gbk", errors="replace") as f:
                f.writelines(new_lines)
    except Exception:
        pass


def _find_projects_ini_path():
    """定位全局 projects.ini"""
    for env_var in ("AVEVA_DESIGN_PROJECT_DIRS", "E3D_PROJECT_DIRS", "PDMSWK"):
        val = os.environ.get(env_var)
        if val:
            candidate = os.path.join(val, "projects.ini")
            if os.path.exists(candidate):
                return candidate
    for cand in [r"D:\AVEVA\Projects\projects.ini", r"C:\AVEVA\Projects\projects.ini"]:
        if os.path.exists(cand):
            return cand
    return None


def _register_to_projects_ini(code, name, address):
    """向 projects.ini 注册项目"""
    ini_path = _find_projects_ini_path()
    if not ini_path:
        return
    try:
        with open(ini_path, "r", encoding="gbk", errors="ignore") as f:
            content = f.read()
        section_tag = f"[{code}]"
        if section_tag.lower() in content.lower():
            # 替换已有区块
            lines = content.splitlines(True)
            new_lines = []
            skipping = False
            for l in lines:
                if re.match(rf"^\[{code}\]", l, re.IGNORECASE):
                    skipping = True
                    new_lines.append(f"[{code}]\nPATH = {address}\nNAME = {name}\nDESCRIPTION = {name}\n")
                    continue
                if skipping and (l.strip().startswith("[") or l.strip() == ""):
                    skipping = False
                if not skipping:
                    new_lines.append(l)
            with open(ini_path, "w", encoding="gbk", errors="replace") as f:
                f.writelines(new_lines)
        else:
            append_block = f"\n\n[{code}]\nPATH = {address}\nNAME = {name}\nDESCRIPTION = {name}\n"
            with open(ini_path, "a", encoding="gbk", errors="replace") as f:
                f.write(append_block)
    except Exception:
        pass


def _unregister_from_projects_ini(code):
    """从 projects.ini 注销项目"""
    ini_path = _find_projects_ini_path()
    if not ini_path or not os.path.exists(ini_path):
        return
    try:
        with open(ini_path, "r", encoding="gbk", errors="ignore") as f:
            lines = f.readlines()
        new_lines = []
        skipping = False
        for l in lines:
            if re.match(rf"^\[{code}\]", l.strip(), re.IGNORECASE):
                skipping = True
                continue
            if skipping and (l.strip().startswith("[") or l.strip() == ""):
                skipping = False
            if not skipping:
                new_lines.append(l)
        with open(ini_path, "w", encoding="gbk", errors="replace") as f:
            f.writelines(new_lines)
    except Exception:
        pass
