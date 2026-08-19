#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 数据层
==========
e3d_projects.json schema v3 的读写、旧版本自动迁移、默认值补齐。

Schema v3:
{
  "version": 3,
  "settings": {
    "e3d_lnk": "",
    "local_projects_dir": "",
    "last_mode": "",
    "last_launched": ""
  },
  "categories": [ {id, name, color} ],
  "project_meta": {
    "<project_id>": {
      "name": "",            # 显示名称（覆盖文件名默认名）
      "category_id": "",
      "tags": [],
      "description": "",
      "notes": "",
      "status": "",
      "owner": "",
      "updated_at": ""
    }
  },
  "libraries": [ {id, name, path, type, protocol, source, last_scan, last_error} ],
  "my_projects": [ {id, name, bat_path, lib_id, source, added_at} ],
  "all_projects_cache": [ {id, name, bat_path, lib_id, lib_path, project_dir, discovered_at} ]
}

项目元信息按项目 ID 单独存储：重新扫描路径库会重建 all_projects_cache，
但分类 / 标签 / 描述等管理信息不会丢失。
"""

import json
import os
import re

import e3d_util as util


CONFIG_FILE = util.get_config_file_path()

CATEGORY_COLORS = [
    '#4f8cff', '#2dd4a7', '#f5b85c', '#ff5d6c', '#b07bff',
    '#36b6e8', '#ff8f6b', '#8bd66b', '#e86b9a', '#9aa8ff',
]

STATUS_OPTIONS = ['进行中', '已完成', '暂停', '归档']


def get_config_path():
    return util.get_config_file_path()


def default_data():
    return {
        "version": 3,
        "settings": {
            "e3d_lnk": "",
            "local_projects_dir": "",
            "last_mode": "",
            "last_launched": "",
        },
        "categories": [],
        "project_meta": {},
        "libraries": [],
        "my_projects": [],
        "all_projects_cache": [],
    }


def load_data(config_file=None):
    """加载配置；不存在或损坏时返回默认值；旧 schema 自动迁移。"""
    path = config_file or util.get_config_file_path()
    data = default_data()
    if os.path.exists(path):
        try:
            with open(path, 'r', encoding='utf-8') as f:
                raw = json.load(f)
            data = _migrate(raw)
        except (json.JSONDecodeError, IOError, TypeError):
            data = default_data()
    _fill_defaults(data)
    return data


def save_data(data, config_file=None):
    """原子写入配置。自动确保父目录存在。"""
    path = config_file or util.get_config_file_path()
    parent = os.path.dirname(path)
    if parent:
        try:
            os.makedirs(parent, exist_ok=True)
        except OSError:
            pass
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


def read_paths_cache():
    """读取 e3d_paths.json（E3D 路径检测缓存）。"""
    p = util.get_paths_cache_path()
    try:
        with open(p, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return {}



def _migrate(raw):
    """v1 / v2 -> v3；v3 直接补默认字段。"""
    if not isinstance(raw, dict):
        return default_data()

    d = default_data()
    d['settings'].update(raw.get('settings') or {})

    if raw.get('version') == 3:
        d['categories'] = list(raw.get('categories') or [])
        d['project_meta'] = dict(raw.get('project_meta') or {})
        d['libraries'] = list(raw.get('libraries') or [])
        d['my_projects'] = list(raw.get('my_projects') or [])
        d['all_projects_cache'] = list(raw.get('all_projects_cache') or [])
        return d

    if raw.get('version') == 2:
        d['libraries'] = list(raw.get('libraries') or [])
        d['my_projects'] = list(raw.get('my_projects') or [])
        d['all_projects_cache'] = list(raw.get('all_projects_cache') or [])
        return d

    # v1（旧版 projects dict）
    projects = raw.get('projects') or {}
    for name, path in projects.items():
        norm = util.normalize_path(str(path))
        if not norm:
            continue
        if norm.lower().endswith('.bat'):
            # 旧版本里直接指向 bat 的条目 -> 我的项目
            d['my_projects'].append({
                'id': util.gen_id('proj', norm),
                'name': _name_from_bat(norm) or str(name),
                'bat_path': norm,
                'lib_id': None,
                'source': 'legacy',
                'added_at': util.now_iso(),
            })
        else:
            # 旧版本里指向文件夹的条目 -> 路径库（保留整库载入能力）
            d['libraries'].append({
                'id': util.gen_id('lib', norm),
                'name': str(name),
                'path': norm,
                'type': 'unknown',
                'protocol': util.path_protocol(norm),
                'source': 'legacy',
                'last_scan': None,
                'last_error': '由旧版本迁移，请重新扫描',
            })
    return d


def _fill_defaults(data):
    d = default_data()
    data.setdefault('version', 3)
    settings = data.setdefault('settings', {})
    for k, v in d['settings'].items():
        settings.setdefault(k, v)
    for k in ('categories', 'project_meta', 'libraries', 'my_projects', 'all_projects_cache'):
        data.setdefault(k, [] if k != 'project_meta' else {})

    # 清理非法分类 / 元信息
    data['categories'] = [c for c in data['categories'] if isinstance(c, dict) and c.get('id')]
    for c in data['categories']:
        c.setdefault('name', '未命名分类')
        c.setdefault('color', _category_color(len(data['categories'])))
    data['project_meta'] = {
        pid: _clean_meta(m)
        for pid, m in (data.get('project_meta') or {}).items()
        if isinstance(m, dict)
    }

    # 本地项目库路径默认取 E3D 检测缓存
    if not settings.get('local_projects_dir'):
        cache = read_paths_cache()
        pd = cache.get('projects_dir') or r'D:\AVEVA\Projects\E3D3.1'
        settings['local_projects_dir'] = util.normalize_path(pd)

    # 启动快捷方式默认自动查找
    lnk = settings.get('e3d_lnk') or ''
    if not lnk or not os.path.exists(lnk):
        found = util.find_e3d_lnk(lnk)
        if found:
            settings['e3d_lnk'] = found


def _clean_meta(m):
    out = {
        'name': '',
        'category_id': '',
        'tags': [],
        'description': '',
        'notes': '',
        'status': '',
        'owner': '',
        'updated_at': '',
    }
    out.update({k: v for k, v in m.items() if k in out})
    out['tags'] = normalize_tags(out.get('tags') or [])
    if out['status'] not in STATUS_OPTIONS:
        out['status'] = ''
    return out


def _category_color(index):
    return CATEGORY_COLORS[index % len(CATEGORY_COLORS)]


def _name_from_bat(bat_path):
    base = os.path.basename(util.normalize_path(bat_path))
    m = re.match(r'^evars(.+)\.bat$', base, re.IGNORECASE)
    if m:
        return m.group(1)
    return os.path.splitext(base)[0]


# ============================================================
# 标签
# ============================================================

def normalize_tags(tags):
    """清洗标签：去空白、去重、限制数量。"""
    seen = set()
    out = []
    for t in (tags or []):
        if not isinstance(t, str):
            continue
        s = ' '.join(t.split())
        if not s or len(s) > 20:
            continue
        key = s.lower()
        if key in seen:
            continue
        seen.add(key)
        out.append(s)
        if len(out) >= 20:
            break
    return out


def all_tags(data):
    """汇总所有项目元信息里用到的标签（去重，按使用次数排序）。"""
    counter = {}
    for meta in (data.get('project_meta') or {}).values():
        for t in meta.get('tags') or []:
            counter[t] = counter.get(t, 0) + 1
    return [t for t, _ in sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]))]


# ============================================================
# 分类
# ============================================================

def find_category(data, category_id):
    for c in data.get('categories') or []:
        if c.get('id') == category_id:
            return c
    return None


def add_category(data, name, color=''):
    name = ' '.join(str(name or '').split())
    if not name:
        return None
    if len(name) > 30:
        name = name[:30]
    cat = {
        'id': util.gen_id('cat', name.lower()),
        'name': name,
        'color': color or _category_color(len(data.get('categories') or [])),
    }
    existing = find_category(data, cat['id'])
    if existing:
        return existing
    data.setdefault('categories', []).append(cat)
    return cat


def update_category(data, category_id, name=None, color=None):
    cat = find_category(data, category_id)
    if not cat:
        return None
    if name is not None:
        name = ' '.join(str(name).split())
        if name:
            cat['name'] = name[:30]
    if color:
        cat['color'] = color
    return cat


def remove_category(data, category_id):
    before = len(data.get('categories') or [])
    data['categories'] = [c for c in data['categories'] if c.get('id') != category_id]
    if len(data['categories']) == before:
        return False
    # 清除引用该分类的项目元信息
    for meta in (data.get('project_meta') or {}).values():
        if meta.get('category_id') == category_id:
            meta['category_id'] = ''
            meta['updated_at'] = util.now_iso()
    return True


# ============================================================
# 项目元信息
# ============================================================

def get_project_meta(data, project_id):
    meta = (data.get('project_meta') or {}).get(project_id)
    return _clean_meta(meta) if isinstance(meta, dict) else _clean_meta({})


def update_project_meta(data, project_id, fields):
    """
    更新项目元信息（分类 / 标签 / 描述 / 备注 / 状态 / 负责人 / 显示名称）。
    返回更新后的 meta。
    """
    if not project_id:
        return None
    meta = get_project_meta(data, project_id)
    if 'name' in fields and fields['name'] is not None:
        name = ' '.join(str(fields['name']).split())
        meta['name'] = name[:60]
    if 'category_id' in fields and fields['category_id'] is not None:
        cid = str(fields['category_id'] or '')
        meta['category_id'] = cid if (not cid or find_category(data, cid)) else ''
    if 'tags' in fields:
        meta['tags'] = normalize_tags(fields['tags'])
    for key in ('description', 'notes', 'status', 'owner'):
        if key in fields and fields[key] is not None:
            v = str(fields[key]).strip()
            if key == 'status' and v and v not in STATUS_OPTIONS:
                v = ''
            meta[key] = v[:2000] if key in ('description', 'notes') else v[:100]
    meta['updated_at'] = util.now_iso()
    data.setdefault('project_meta', {})[project_id] = meta
    return meta


def merge_project_meta(project, meta):
    """把元信息合并进项目条目（显示名称覆盖默认名）。"""
    item = dict(project)
    default_name = item.get('name') or ''
    meta = meta or {}
    item.update({
        'display_name': meta.get('name') or default_name,
        'category_id': meta.get('category_id') or '',
        'tags': meta.get('tags') or [],
        'description': meta.get('description') or '',
        'notes': meta.get('notes') or '',
        'status': meta.get('status') or '',
        'owner': meta.get('owner') or '',
        'meta_updated_at': meta.get('updated_at') or '',
    })
    item['name'] = item['display_name']
    return item


def project_meta_map(data):
    """返回 {project_id: clean_meta} 便于批量合并。"""
    return {pid: _clean_meta(m) for pid, m in (data.get('project_meta') or {}).items() if isinstance(m, dict)}


# ============================================================
# 查找
# ============================================================

def find_library(data, lib_id=None, path=None):
    """按 ID 或规范化路径查找路径库。"""
    if lib_id:
        for lib in data['libraries']:
            if lib.get('id') == lib_id:
                return lib
    if path:
        norm = util.normalize_path(path).lower()
        for lib in data['libraries']:
            if util.normalize_path(lib.get('path', '')).lower() == norm:
                return lib
    return None


def find_my_project(data, project_id=None, name=None, bat_path=None):
    for p in data['my_projects']:
        if project_id and p.get('id') == project_id:
            return p
        if name and p.get('name', '').lower() == name.lower():
            return p
        if bat_path and util.normalize_path(p.get('bat_path', '')).lower() == util.normalize_path(bat_path).lower():
            return p
    return None


def find_cached_project(data, project_id=None, name=None):
    for p in data['all_projects_cache']:
        if project_id and p.get('id') == project_id:
            return p
        if name and p.get('name', '').lower() == name.lower():
            return p
    return None
