#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP Web 启动面板
================
内嵌 HTTP 服务 + 浏览器 UI。零第三方依赖。
"""

import http.server
import json
import os
import socket
import threading
import time
import webbrowser

import e3d_diag
import e3d_launcher as launcher
import e3d_scanner as scanner
import e3d_store as store
import e3d_util as util


FALLBACK_HTML = """<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>SEP</title></head>
<body style="font-family:sans-serif;background:#0b0e14;color:#e8ecf4;display:grid;place-items:center;height:100vh">
<div style="text-align:center"><h2>SEP 启动器</h2><p style="color:#8b95ab">未找到 web_ui.html 资源，请检查程序文件完整性。</p></div>
</body></html>"""

_LOCK = threading.Lock()


def _load_html():
    try:
        with open(util.resource_path('web_ui.html'), 'r', encoding='utf-8') as f:
            return f.read()
    except OSError:
        return FALLBACK_HTML


class _WebHandler(http.server.BaseHTTPRequestHandler):
    server_version = 'SEP/2.0'

    def log_message(self, fmt, *args):
        pass

    def _send_json(self, data, status=200):
        body = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self):
        length = int(self.headers.get('Content-Length', 0))
        if length <= 0:
            return {}
        try:
            return json.loads(self.rfile.read(length).decode('utf-8'))
        except Exception:
            return {}

    def do_GET(self):
        if self.path in ('/', '/index.html'):
            body = _load_html().encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path == '/api/status':
            self._handle_status()
        elif self.path == '/favicon.ico':
            self.send_response(204)
            self.end_headers()
        else:
            self._send_json({'error': 'not found'}, 404)

    def do_POST(self):
        routes = {
            '/api/library/add': self._handle_library_add,
            '/api/library/remove': self._handle_library_remove,
            '/api/library/rescan': self._handle_library_rescan,
            '/api/library/rescan-all': self._handle_library_rescan_all,
            '/api/my/add': self._handle_my_add,
            '/api/my/remove': self._handle_my_remove,
            '/api/my/rename': self._handle_my_rename,
            '/api/my/clear': self._handle_my_clear,
            '/api/my/batch-add': self._handle_my_batch_add,
            '/api/my/batch-remove': self._handle_my_batch_remove,
            '/api/category/add': self._handle_category_add,
            '/api/category/update': self._handle_category_update,
            '/api/category/remove': self._handle_category_remove,
            '/api/project/update': self._handle_project_update,
            '/api/projects/batch-update': self._handle_projects_batch_update,
            '/api/diagnose/library': self._handle_diagnose_library,
            '/api/diagnose/all': self._handle_diagnose_all,
            '/api/diagnose/fix': self._handle_diagnose_fix,
            '/api/settings/update': self._handle_settings_update,
            '/api/launch': self._handle_launch,
            '/api/detect': self._handle_detect,
            '/api/quit': self._handle_quit,
        }
        fn = routes.get(self.path)
        if fn:
            with _LOCK:
                fn(self._read_body())
        else:
            self._send_json({'error': 'not found'}, 404)

    # ---------- 状态 ----------

    def _handle_status(self, body=None):
        launcher.resolve_e3d(force=False, verbose=False)
        data = store.load_data()

        current = None
        if launcher.EVARS_BAT:
            current = launcher.read_projects_dir(launcher.EVARS_BAT)

        local_dir = launcher.get_local_projects_dir(data)
        managed = launcher.read_managed(local_dir)

        lnk = util.find_e3d_lnk((data.get('settings') or {}).get('e3d_lnk', ''))
        if lnk and (data['settings'].get('e3d_lnk') != lnk):
            data['settings']['e3d_lnk'] = lnk
            store.save_data(data)

        lib_map = {lib['id']: lib.get('name', '') for lib in data['libraries']}
        count_map = {}
        for p in data['all_projects_cache']:
            lid = p.get('lib_id')
            count_map[lid] = count_map.get(lid, 0) + 1

        libraries = []
        for lib in data['libraries']:
            item = dict(lib)
            item['project_count'] = count_map.get(lib.get('id'), 0)
            libraries.append(item)

        all_projects = []
        meta_map = store.project_meta_map(data)
        for p in data['all_projects_cache']:
            item = store.merge_project_meta(p, meta_map.get(p.get('id')))
            item['lib_name'] = lib_map.get(p.get('lib_id'), '')
            all_projects.append(item)

        my_projects = [
            store.merge_project_meta(p, meta_map.get(p.get('id')))
            for p in data['my_projects']
        ]

        e3d = {
            'detected': bool(launcher.EVARS_BAT and launcher.EVARS_INIT),
            'install_dir': os.path.dirname(launcher.EVARS_BAT) if launcher.EVARS_BAT else None,
            'current_projects_dir': current,
            'lnk': lnk,
        }
        self._send_json({
            'e3d': e3d,
            'managed_paths': managed,
            'local_projects_dir': local_dir,
            'my_projects': my_projects,
            'libraries': libraries,
            'all_projects': all_projects,
            'categories': data['categories'],
            'all_tags': store.all_tags(data),
            'status_options': store.STATUS_OPTIONS,
            'settings': data['settings'],
        })

    # ---------- 路径库 ----------

    def _handle_library_add(self, body):
        path = (body.get('path') or '').strip()
        if not path:
            return self._send_json({'error': '路径不能为空'}, 400)
        try:
            lib, info, projects = scanner.add_library(path, timeout=10)
        except Exception as e:
            return self._send_json({'error': f'扫描失败: {e}'}, 500)
        if not lib:
            return self._send_json({'error': info.get('reason', '无法识别该路径')}, 400)

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
        self._send_json({'ok': True, 'library': lib, 'projects': projects})

    def _handle_library_remove(self, body):
        data = store.load_data()
        lib_id = body.get('id', '')
        libs = [x for x in data['libraries'] if x.get('id') != lib_id]
        if len(libs) == len(data['libraries']):
            return self._send_json({'error': '未找到该路径库'}, 404)
        data['libraries'] = libs
        data['all_projects_cache'] = [p for p in data['all_projects_cache'] if p.get('lib_id') != lib_id]
        for p in data['my_projects']:
            if p.get('lib_id') == lib_id:
                p['lib_id'] = None
        store.save_data(data)
        self._send_json({'ok': True})

    def _handle_library_rescan(self, body):
        data = store.load_data()
        lib = store.find_library(data, lib_id=body.get('id', ''))
        if not lib:
            return self._send_json({'error': '未找到该路径库'}, 404)
        try:
            projects, lib = scanner.rescan_library(lib, timeout=10)
        except Exception as e:
            return self._send_json({'error': f'扫描失败: {e}'}, 500)
        _replace_cache(data, lib['id'], projects)
        store.save_data(data)
        self._send_json({'ok': True, 'projects': projects})

    def _handle_library_rescan_all(self, body):
        data = store.load_data()
        results = []
        for lib in data['libraries']:
            try:
                projects, lib = scanner.rescan_library(lib, timeout=10)
            except Exception as e:
                lib['last_error'] = str(e)
                projects = []
            _replace_cache(data, lib['id'], projects)
            results.append({'id': lib['id'], 'count': len(projects), 'error': lib.get('last_error')})
        store.save_data(data)
        self._send_json({'ok': True, 'results': results})

    # ---------- 我的项目 ----------

    def _handle_my_add(self, body):
        data = store.load_data()
        pid = body.get('project_id', '')
        proj = store.find_cached_project(data, project_id=pid)
        if not proj:
            return self._send_json({'error': '未找到该项目，请先扫描路径库'}, 404)
        if store.find_my_project(data, bat_path=proj['bat_path']):
            return self._send_json({'error': '该项目已在“我的项目”中'}, 400)
        data['my_projects'].append({
            'id': proj['id'],
            'name': proj['name'],
            'bat_path': proj['bat_path'],
            'lib_id': proj.get('lib_id'),
            'source': 'user',
            'added_at': util.now_iso(),
        })
        store.save_data(data)
        self._send_json({'ok': True})

    def _handle_my_remove(self, body):
        data = store.load_data()
        pid = body.get('id', '')
        before = len(data['my_projects'])
        data['my_projects'] = [p for p in data['my_projects'] if p.get('id') != pid]
        if len(data['my_projects']) == before:
            return self._send_json({'error': '未找到该项目'}, 404)
        store.save_data(data)
        self._send_json({'ok': True})

    def _handle_my_rename(self, body):
        data = store.load_data()
        pid = body.get('id', '')
        name = (body.get('name') or '').strip()
        if not name:
            return self._send_json({'error': '名称不能为空'}, 400)
        proj = store.find_my_project(data, project_id=pid)
        if not proj:
            return self._send_json({'error': '未找到该项目'}, 404)
        meta = store.update_project_meta(data, pid, {'name': name})
        if meta and meta.get('name'):
            proj['name'] = meta['name']
            cached = store.find_cached_project(data, project_id=pid)
            if cached:
                cached['name'] = meta['name']
        store.save_data(data)
        self._send_json({'ok': True})

    def _handle_my_clear(self, body):
        data = store.load_data()
        data['my_projects'] = []
        store.save_data(data)
        self._send_json({'ok': True})

    def _handle_my_batch_add(self, body):
        data = store.load_data()
        added = []
        for pid in (body.get('project_ids') or []):
            proj = store.find_cached_project(data, project_id=pid)
            if not proj or store.find_my_project(data, bat_path=proj['bat_path']):
                continue
            data['my_projects'].append({
                'id': proj['id'],
                'name': proj['name'],
                'bat_path': proj['bat_path'],
                'lib_id': proj.get('lib_id'),
                'source': 'user',
                'added_at': util.now_iso(),
            })
            added.append(pid)
        store.save_data(data)
        self._send_json({'ok': True, 'added': added})

    def _handle_my_batch_remove(self, body):
        data = store.load_data()
        ids = set(body.get('project_ids') or [])
        before = len(data['my_projects'])
        data['my_projects'] = [p for p in data['my_projects'] if p.get('id') not in ids]
        store.save_data(data)
        self._send_json({'ok': True, 'removed': before - len(data['my_projects'])})

    # ---------- 启动 ----------

    def _handle_launch(self, body):
        mode = body.get('mode', '')
        data = store.load_data()
        lnk = (data.get('settings') or {}).get('e3d_lnk', '')

        try:
            if mode in ('single', 'temp'):
                pid = body.get('project_id', '')
                if mode == 'single':
                    proj = store.find_my_project(data, project_id=pid)
                    if not proj:
                        return self._send_json({'error': '未在“我的项目”中找到该项目'}, 404)
                else:
                    proj = store.find_cached_project(data, project_id=pid)
                    if not proj:
                        return self._send_json({'error': '未找到该项目，请先扫描路径库'}, 404)
                meta = store.get_project_meta(data, proj['id'])
                payload = {'bat_path': proj['bat_path'], 'name': meta.get('name') or proj['name']}
            elif mode == 'all':
                payload = {'name': '全部'}
            elif mode == 'library':
                lib = store.find_library(data, lib_id=body.get('library_id', ''))
                if not lib:
                    return self._send_json({'error': '未找到该路径库'}, 404)
                payload = {'path': lib['path'], 'name': lib['name']}
            else:
                return self._send_json({'error': f'未知启动模式: {mode}'}, 400)

            summary = launcher.launch(mode, payload, lnk=lnk)
            self._send_json({'ok': True, 'summary': summary})
        except launcher.LauncherError as e:
            self._send_json({'error': str(e)}, 500)
        except Exception as e:
            self._send_json({'error': f'启动失败: {e}'}, 500)

    # ---------- 其他 ----------

    def _handle_detect(self, body):
        ok = launcher.resolve_e3d(force=True, verbose=False)
        if not ok:
            return self._send_json({'error': '未检测到 E3D 安装'}, 500)
        data = store.load_data()
        lnk = util.find_e3d_lnk(data['settings'].get('e3d_lnk', ''))
        if lnk:
            data['settings']['e3d_lnk'] = lnk
        store.save_data(data)
        self._send_json({'ok': True, 'path': os.path.dirname(launcher.EVARS_BAT)})

    # ---------- 分类 ----------

    def _handle_category_add(self, body):
        data = store.load_data()
        cat = store.add_category(data, body.get('name', ''), body.get('color', ''))
        if not cat:
            return self._send_json({'error': '分类名称不能为空'}, 400)
        store.save_data(data)
        self._send_json({'ok': True, 'category': cat})

    def _handle_category_update(self, body):
        data = store.load_data()
        cat = store.update_category(
            data,
            body.get('id', ''),
            body.get('name'),
            body.get('color'),
        )
        if not cat:
            return self._send_json({'error': '未找到该分类'}, 404)
        store.save_data(data)
        self._send_json({'ok': True, 'category': cat})

    def _handle_category_remove(self, body):
        data = store.load_data()
        if not store.remove_category(data, body.get('id', '')):
            return self._send_json({'error': '未找到该分类'}, 404)
        store.save_data(data)
        self._send_json({'ok': True})

    # ---------- 项目信息 ----------

    def _handle_project_update(self, body):
        data = store.load_data()
        pid = body.get('project_id', '')
        cached = store.find_cached_project(data, project_id=pid)
        my = store.find_my_project(data, project_id=pid)
        if not cached and not my:
            return self._send_json({'error': '未找到该项目'}, 404)

        fields = {
            k: body.get(k)
            for k in ('name', 'category_id', 'tags', 'description', 'notes', 'status', 'owner')
            if k in body
        }
        meta = store.update_project_meta(data, pid, fields)
        if meta is None:
            return self._send_json({'error': '项目 ID 无效'}, 400)
        if my and fields.get('name'):
            my['name'] = meta['name']

        # 同步缓存条目名称，避免下次扫描前显示不一致
        if cached and fields.get('name') and meta.get('name'):
            cached['name'] = meta['name']

        store.save_data(data)
        base = cached or my or {'id': pid, 'name': meta.get('name') or ''}
        self._send_json({'ok': True, 'project': store.merge_project_meta(base, meta)})

    def _handle_projects_batch_update(self, body):
        data = store.load_data()
        ids = []
        for pid in (body.get('project_ids') or []):
            if store.find_cached_project(data, project_id=pid) or store.find_my_project(data, project_id=pid):
                ids.append(pid)
        if not ids:
            return self._send_json({'error': '未找到任何可编辑的项目'}, 404)

        fields = {
            k: body.get(k)
            for k in ('name', 'category_id', 'tags', 'description', 'notes', 'status', 'owner')
            if k in body
        }
        updated = []
        for pid in ids:
            meta = store.update_project_meta(data, pid, fields)
            if meta is None:
                continue
            if fields.get('name') and meta.get('name'):
                my = store.find_my_project(data, project_id=pid)
                cached = store.find_cached_project(data, project_id=pid)
                if my:
                    my['name'] = meta['name']
                if cached:
                    cached['name'] = meta['name']
            updated.append(pid)
        store.save_data(data)
        self._send_json({'ok': True, 'updated': updated})

    # ---------- 设置 ----------

    def _handle_settings_update(self, body):
        data = store.load_data()
        s = data['settings']
        if 'local_projects_dir' in body and body.get('local_projects_dir') is not None:
            s['local_projects_dir'] = util.normalize_path(body['local_projects_dir'])
        if 'e3d_lnk' in body and body.get('e3d_lnk') is not None:
            s['e3d_lnk'] = (body['e3d_lnk'] or '').strip()
        store.save_data(data)
        self._send_json({'ok': True, 'settings': s})

    # ---------- 诊断与修复 ----------

    def _handle_diagnose_library(self, body):
        data = store.load_data()
        path = (body.get('path') or '').strip()
        lib_id = body.get('id', '')
        if lib_id:
            lib = store.find_library(data, lib_id=lib_id)
            if not lib:
                return self._send_json({'error': '未找到该路径库'}, 404)
            path = lib.get('path', '')
        if not path:
            return self._send_json({'error': '缺少路径'}, 400)
        try:
            report = e3d_diag.diagnose(path, timeout=12)
        except Exception as e:
            return self._send_json({'error': f'诊断失败: {e}'}, 500)
        self._send_json({'ok': True, 'report': report})

    def _handle_diagnose_all(self, body):
        data = store.load_data()
        results = []
        for lib in data['libraries']:
            try:
                report = e3d_diag.diagnose(lib.get('path', ''), timeout=12)
            except Exception as e:
                report = {
                    'ok': False,
                    'path': lib.get('path', ''),
                    'protocol': 'local',
                    'checks': [{'id': 'error', 'name': '诊断异常', 'status': 'fail', 'detail': str(e), 'fix': None}],
                    'fixes': [],
                }
            results.append({'library': lib, 'report': report})
        self._send_json({'ok': True, 'results': results})

    def _handle_diagnose_fix(self, body):
        fix_id = (body.get('fix_id') or '').strip()
        path = (body.get('path') or '').strip()
        data = store.load_data()
        if body.get('library_id'):
            lib = store.find_library(data, lib_id=body.get('library_id'))
            if lib:
                path = lib.get('path', '')
        if not fix_id:
            return self._send_json({'error': '缺少修复项'}, 400)
        result = e3d_diag.apply_fix(fix_id, path)
        if result.get('ok'):
            self._send_json({'ok': True, 'result': result})
        else:
            self._send_json({'ok': False, 'error': result.get('message', '修复失败'), 'result': result})

    def _handle_quit(self, body):
        self._send_json({'ok': True})
        threading.Timer(0.4, os._exit, (0,)).start()


def _replace_cache(data, lib_id, projects):
    now = util.now_iso()
    data['all_projects_cache'] = [p for p in data['all_projects_cache'] if p.get('lib_id') != lib_id]
    for p in projects:
        item = dict(p)
        item['lib_id'] = lib_id
        item['discovered_at'] = now
        data['all_projects_cache'].append(item)


def _get_free_port(start=8800):
    for port in range(start, start + 100):
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.bind(('127.0.0.1', port))
                return port
        except OSError:
            continue
    return 8800


def start_web_ui():
    """启动本地 Web 服务并打开浏览器。"""
    launcher.resolve_e3d(force=False, verbose=False)
    port = _get_free_port()
    server = http.server.HTTPServer(('127.0.0.1', port), _WebHandler)
    url = f'http://127.0.0.1:{port}'

    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    print(f'\n  SEP 启动面板: {url}')
    print('  关闭面板请点击页面右上角「退出」，或按 Ctrl+C 结束。\n')
    try:
        webbrowser.open(url)
    except Exception:
        pass

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        server.shutdown()
