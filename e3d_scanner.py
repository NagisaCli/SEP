#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 扫描层
==========
路径类型判定与项目发现（下一层项目文件夹规则）：

1. 路径是 evarsXXX.bat 文件                -> 单项目
2. 目录直接含 evarsXXX.bat                 -> 项目文件夹（单项目）
3. 目录下一层子文件夹内含 evarsXXX.bat      -> 项目库（collection）
   - 只检查项目库的下一层：每个子文件夹里只要有 evars*.bat 就识别为一个项目
   - 下一层没有该文件的文件夹不视为项目文件夹
   - 不再解析 custom_evars.bat 的 call 行来获取项目
4. smb:// 自动转 UNC；URL 预留扩展窗口
"""

import os
import re

import e3d_util as util


EVARS_FILE_RE = re.compile(r'^evars(.+)\.bat$', re.IGNORECASE)


def classify(path, timeout=6):
    """
    判定路径类型。
    返回 {kind: collection|project|invalid|unsupported, reason, ...}
    """
    norm = util.normalize_path(path)
    if not norm:
        return {'kind': 'invalid', 'reason': '路径为空'}

    if util.is_url(norm):
        return {
            'kind': 'unsupported',
            'reason': 'URL 项目库将在后续版本支持（扩展窗口已预留）',
            'path': norm,
        }

    if util.path_protocol(norm) == 'unc':
        return util.run_with_timeout(
            lambda: _classify_impl(norm),
            timeout,
            {'kind': 'invalid', 'reason': f'网络路径访问超时: {norm}', 'path': norm},
        )
    return _classify_impl(norm)


def _classify_impl(norm):
    try:
        exists = os.path.exists(norm)
    except OSError:
        exists = False
    if not exists:
        return {'kind': 'invalid', 'reason': f'路径不存在或不可访问: {norm}', 'path': norm}

    if os.path.isfile(norm):
        base = os.path.basename(norm)
        if base.lower() != 'evars.bat' and EVARS_FILE_RE.match(base):
            return {'kind': 'project', 'reason': '单项目文件', 'path': norm}
        return {'kind': 'invalid', 'reason': '文件不是 evarsXXX.bat 项目文件', 'path': norm}

    project_dirs = _find_project_dirs(norm)
    direct = _find_direct_evars(norm)

    if project_dirs:
        return {
            'kind': 'collection',
            'reason': f'项目库（发现 {len(project_dirs)} 个项目文件夹）',
            'path': norm,
            'project_dirs': project_dirs,
            'direct': direct,
        }
    if direct:
        return {
            'kind': 'project',
            'reason': f'项目文件夹（直接发现 {len(direct)} 个 evars*.bat）',
            'path': norm,
            'direct': direct,
        }

    return {
        'kind': 'invalid',
        'reason': '目录中未找到 evarsXXX.bat，且下一层子文件夹也没有项目文件',
        'path': norm,
    }


def scan_library(path, timeout=8):
    """
    扫描路径库，返回 (projects, info)。
    projects: [{id, name, bat_path, lib_path, project_dir}]
    """
    norm = util.normalize_path(path)
    if util.path_protocol(norm) == 'unc':
        return util.run_with_timeout(
            lambda: _scan_impl(norm),
            timeout,
            ([], {'kind': 'invalid', 'reason': f'网络路径扫描超时: {norm}', 'path': norm}),
        )
    return _scan_impl(norm)


def _scan_impl(path):
    info = classify(path)
    if info['kind'] in ('invalid', 'unsupported'):
        return [], info

    norm = util.normalize_path(path)
    if info['kind'] == 'project':
        bats = info.get('direct') or ([info.get('path')] if info.get('path') else [])
        return _dedupe([_make_project(p, norm) for p in bats]), info

    projects = []
    for d in (info.get('project_dirs') or []):
        for p in _find_direct_evars(d):
            projects.append(_make_project(p, norm, project_dir=d))
    for p in (info.get('direct') or []):
        projects.append(_make_project(p, norm))
    return _dedupe(projects), info


def project_name(bat_path):
    """项目名取 evarsXXX.bat 的 XXX；不符合格式时取文件名主干。"""
    base = os.path.basename(util.normalize_path(bat_path))
    m = EVARS_FILE_RE.match(base)
    if m:
        return m.group(1)
    return os.path.splitext(base)[0]


def add_library(path, timeout=8):
    """
    添加路径库：判定类型并扫描。
    返回 (lib, info, projects)；失败时 lib 为 None，info 含 reason。
    """
    info = classify(path, timeout)
    if info['kind'] in ('invalid', 'unsupported'):
        return None, info, []

    norm = util.normalize_path(path)
    projects, info = scan_library(path, timeout)
    if not projects and info['kind'] == 'collection':
        return None, {**info, 'kind': 'invalid', 'reason': '该路径没有解析出任何项目'}, []

    lib = {
        'id': util.gen_id('lib', norm),
        'name': _library_name(norm, info),
        'path': norm,
        'type': 'project' if info['kind'] == 'project' else 'collection',
        'protocol': util.path_protocol(norm),
        'source': 'user',
        'last_scan': util.now_iso(),
        'last_error': None,
    }
    return lib, info, projects


def rescan_library(lib, timeout=8):
    """
    重新扫描已有路径库，原地更新 lib 字段。
    返回 (projects, lib)。
    """
    projects, info = scan_library(lib.get('path', ''), timeout)
    lib['last_scan'] = util.now_iso()
    if info['kind'] in ('invalid', 'unsupported'):
        lib['last_error'] = info.get('reason', '路径不可访问')
        return [], lib
    lib['type'] = 'project' if info['kind'] == 'project' else 'collection'
    lib['last_error'] = None
    return projects, lib


# ---- 内部工具 ----

def _find_project_dirs(dirpath):
    """
    返回项目库下一层中的项目文件夹：
    子文件夹里直接存在 evars*.bat（不含 evars.bat）才算是项目文件夹。
    """
    out = []
    try:
        entries = os.listdir(dirpath)
    except OSError:
        return []
    for name in sorted(entries):
        p = os.path.join(dirpath, name)
        try:
            if os.path.isdir(p) and _find_direct_evars(p):
                out.append(p)
        except OSError:
            continue
    return out


def _find_direct_evars(dirpath):
    """返回目录直接包含的 evars*.bat（排除 evars.bat 自身）。"""
    out = []
    try:
        for name in os.listdir(dirpath):
            low = name.lower()
            if low.startswith('evars') and low.endswith('.bat') and low != 'evars.bat':
                p = os.path.join(dirpath, name)
                if os.path.isfile(p):
                    out.append(p)
    except OSError:
        return []
    return sorted(out)


def _make_project(bat_path, lib_path, project_dir=None):
    bat = util.normalize_path(bat_path)
    return {
        'id': util.gen_id('proj', bat),
        'name': project_name(bat),
        'bat_path': bat,
        'lib_path': util.normalize_path(lib_path),
        'project_dir': util.normalize_path(project_dir) if project_dir else None,
    }


def _dedupe(projects):
    seen = set()
    out = []
    for p in projects:
        key = p['bat_path'].lower()
        if key in seen:
            continue
        seen.add(key)
        out.append(p)
    return out


def _library_name(norm, info):
    if info.get('kind') == 'project':
        if os.path.isfile(norm):
            return project_name(norm)
        return os.path.basename(norm.rstrip('\\'))
    base = os.path.basename(norm.rstrip('\\'))
    return base or norm


# ---- URL 扩展窗口 ----

class UrlLibraryAdapter:
    """
    URL 项目库适配器占位（后续版本实现）。
    预期接口：
      fetch_custom_evars(base_url) -> text
      list_evars(base_url) -> [url]
      resolve(bat_url) -> 本地缓存路径或可启动路径
    """
    supported = False

    @staticmethod
    def classify(url):
        return {'kind': 'unsupported', 'reason': 'URL 项目库将在后续版本支持'}
