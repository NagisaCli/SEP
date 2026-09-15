#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
v3 回归测试：本轮修复的真实缺陷与性能约束。
每个用例对应一个可复现的问题，防止再次退化。
"""

import json
import os
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_diag
import e3d_plugin
import e3d_session
import e3d_store as store
import e3d_util as util
import e3d_web


class TestRunWithTimeoutActuallyTimesOut(unittest.TestCase):
    """旧实现 `with ThreadPoolExecutor()` 退出时 join 卡住的线程，timeout 形同虚设。"""

    def test_returns_default_within_timeout(self):
        gate = threading.Event()
        t0 = time.monotonic()
        res = util.run_with_timeout(lambda: gate.wait(30) or 'late', timeout=0.3, default='TIMEOUT')
        elapsed = time.monotonic() - t0
        gate.set()
        self.assertEqual(res, 'TIMEOUT')
        self.assertLess(elapsed, 2.0, f'timeout 未生效，耗时 {elapsed:.1f}s')

    def test_exception_returns_default(self):
        def boom():
            raise RuntimeError('x')
        self.assertEqual(util.run_with_timeout(boom, timeout=1, default='D'), 'D')


class TestHostResolveCache(unittest.TestCase):
    def test_ipv4_passthrough_and_cache(self):
        self.assertTrue(util.is_host_resolvable('192.168.1.1'))
        util._HOST_CACHE.set('nonexistent-host-zzz', False)
        self.assertFalse(util.is_host_resolvable('NonExistent-Host-ZZZ'))


class TestCrossDevicePathResolution(unittest.TestCase):
    """
    resolve_cross_device_path 曾把 sub_path 追加到每个候选路径之后，
    用户配置的 plugins_dir=C:\\X\\Plugins 被当成 C:\\X\\Plugins\\AVEVA\\Plugins 探测，从未生效；
    盘符兜底还会生成 'D:AVEVA\\Plugins' 这种盘符相对路径。
    """

    def test_configured_candidate_is_used_as_is(self):
        with tempfile.TemporaryDirectory() as tmp:
            mine = os.path.join(tmp, 'MyPlugins')
            os.makedirs(mine)
            res, created, _ = util.resolve_cross_device_path([mine, r'Z:\nope'], sub_path=r'AVEVA\Plugins')
            self.assertEqual(res.lower(), util.normalize_path(mine).lower())
            self.assertFalse(created)

    def test_drive_fallback_path_is_absolute(self):
        drives = util.get_available_drives()
        sub = 'SEP_TEST_NEVER_EXISTS_' + util.random_id(6)
        res, created, _ = util.resolve_cross_device_path([], sub_path=sub, default_drive_pref=tuple(drives))
        try:
            self.assertRegex(res, r'^[A-Za-z]:\\', res)
            self.assertTrue(os.path.isabs(res))
        finally:
            try:
                os.rmdir(res)
            except OSError:
                pass


class TestSaveDataSkipsUnchanged(unittest.TestCase):
    def test_unchanged_write_is_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            cfg = os.path.join(tmp, 'e3d_projects.json')
            data = store.default_data()
            self.assertTrue(store.save_data(data, cfg))
            self.assertFalse(os.path.exists(cfg + '.bak'))
            self.assertFalse(store.save_data(data, cfg), '内容未变化不应重写')
            self.assertFalse(os.path.exists(cfg + '.bak'), '内容未变化不应轮转备份')
            data['categories'].append({'id': 'cat_x', 'name': 'X', 'color': '#fff'})
            self.assertTrue(store.save_data(data, cfg))
            self.assertTrue(os.path.exists(cfg + '.bak'))


class TestDiagUndefinedVarLine(unittest.TestCase):
    """diagnose_e3d_config 曾在遇到 if not exist "%var%" 时因 NameError(lines) 中断整段检查。"""

    def test_undefined_var_reported_not_crashed(self):
        with tempfile.TemporaryDirectory() as tmp:
            inst = os.path.join(tmp, 'E3D')
            proj = os.path.join(tmp, 'P')
            os.makedirs(inst)
            os.makedirs(proj)
            with open(os.path.join(inst, 'evars.bat'), 'w', encoding='utf-8') as f:
                f.write('@echo off\r\nset projects_dir=' + proj + '\r\n')
            with open(os.path.join(inst, 'evars.init'), 'w', encoding='utf-8') as f:
                f.write('@echo off\r\n')
            with open(os.path.join(proj, 'custom_evars.bat'), 'w', encoding='utf-8') as f:
                f.write(
                    '@echo off\r\n'
                    'if not exist "%sep_undefined_var_zzz%" goto :eof\r\n'
                    'set PMLLIB=%PMLLIB%;C:\\nonexistent_sep_test\\pmllib\r\n'
                )
            rep = e3d_diag.diagnose_e3d_config(e3d_install_dir=inst, projects_dir=proj, timeout=1)
            self.assertFalse(any('异常' in i for i in rep.get('issues', [])), rep.get('issues'))
            checks = {c['id']: c for c in rep['checks']}
            reasons = [l['reason'] for l in checks['custom_evars_health']['invalid_lines']]
            self.assertTrue(any('未定义' in r for r in reasons), reasons)
            self.assertTrue(any('nonexistent_sep_test' in (l.get('path') or '') for l in checks['custom_evars_health']['invalid_lines']),
                            '未定义变量行之后的死路径也必须继续被检查')


class TestCadFontsToolDoesNotCrash(unittest.TestCase):
    """fix_cad_fonts_tool 曾缺少 glob/shutil 导入且未初始化 changes，调用即 NameError。"""

    def test_returns_result_dict(self):
        res = e3d_diag.fix_cad_fonts_tool()
        self.assertIn('ok', res)
        self.assertIn('changes', res)
        self.assertIsInstance(res['changes'], list)


class TestUserdataDir(unittest.TestCase):
    def test_get_userdata_dir_returns_path(self):
        d = e3d_diag.get_userdata_dir()
        self.assertTrue(d.lower().endswith('userdata'))


class TestSessionProbeBounded(unittest.TestCase):
    def test_batch_inspect_within_budget(self):
        projects = [
            {'id': 'p1', 'bat_path': r'\\192.0.2.1\nope\evarsA.bat'},
            {'id': 'p2', 'bat_path': r'\\192.0.2.2\nope\evarsB.bat'},
        ]
        t0 = time.monotonic()
        res = e3d_session.batch_inspect_sessions(projects, budget=1.5)
        self.assertLess(time.monotonic() - t0, 4.0)
        self.assertEqual(set(res.keys()), {'p1', 'p2'})
        for v in res.values():
            self.assertIn('online_count', v)

    def test_local_project_dir_probe(self):
        with tempfile.TemporaryDirectory() as tmp:
            bat = os.path.join(tmp, 'evarsX.bat')
            open(bat, 'w').close()
            ses_dir = os.path.join(tmp, '.sep_sessions')
            os.makedirs(ses_dir)
            with open(os.path.join(ses_dir, 'ses_1.json'), 'w', encoding='utf-8') as f:
                json.dump({'session_id': 'ses_1', 'computer_name': 'OTHER-PC', 'user_name': 'u',
                           'last_heartbeat': util.now_iso()}, f)
            with open(os.path.join(ses_dir, 'ses_old.json'), 'w', encoding='utf-8') as f:
                json.dump({'session_id': 'ses_old', 'computer_name': 'OLD-PC', 'user_name': 'u',
                           'last_heartbeat': '2000-01-01T00:00:00'}, f)
            res = e3d_session.inspect_project_connections(bat)
            self.assertEqual(res['online_count'], 1)
            self.assertFalse(os.path.exists(os.path.join(ses_dir, 'ses_old.json')), '过期心跳应被清理')

    def test_process_enumeration_no_subprocess(self):
        if sys.platform != 'win32':
            self.skipTest('windows only')
        names = e3d_session._running_process_names_win()
        self.assertTrue(any(n.startswith('python') for n in names), names)


class TestPluginInspectCache(unittest.TestCase):
    def test_cache_invalidates_on_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            pdir = os.path.join(tmp, 'Plugins')
            os.makedirs(pdir)
            target = e3d_plugin.create_plugin_skeleton('CacheTool', plugins_dir=pdir)
            a = e3d_plugin.inspect_plugin_deep(target)
            b = e3d_plugin.inspect_plugin_deep(target)
            self.assertEqual(a['functions'][0]['file'], b['functions'][0]['file'])
            self.assertIsNot(a, b, '缓存必须返回副本，调用方修改不能污染缓存')
            time.sleep(0.05)
            with open(os.path.join(target, 'pmllib', 'functions', 'extra.pmlfnc'), 'w') as f:
                f.write('define function !extra()\r\nendfunction\r\n')
            c = e3d_plugin.inspect_plugin_deep(target)
            self.assertEqual(len(c['functions']), 2)

    def test_random_id_exists(self):
        self.assertEqual(len(util.random_id(6)), 6)


class TestWebEndpointsFixed(unittest.TestCase):
    """通过真实 HTTP 验证曾经 500 / 静默失效的接口。"""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.cfg = os.path.join(cls.tmp.name, 'cfg.json')
        cls.proj_dir = os.path.join(cls.tmp.name, 'Proj')
        os.makedirs(cls.proj_dir)
        cls.bat = os.path.join(cls.proj_dir, 'evarsDEMO.bat')
        open(cls.bat, 'w').close()
        cls.plugins_dir = os.path.join(cls.tmp.name, 'Plugins')
        os.makedirs(cls.plugins_dir)
        e3d_plugin.create_plugin_skeleton('WebTool', plugins_dir=cls.plugins_dir)

        data = store.default_data()
        data['settings']['auto_scanned'] = True
        data['settings']['local_projects_dir'] = cls.proj_dir
        data['settings']['plugins_dir'] = cls.plugins_dir
        data['all_projects_cache'] = [{'id': 'proj_demo', 'name': 'DEMO', 'bat_path': cls.bat, 'lib_id': None, 'discovered_at': ''}]
        store.save_data(data, cls.cfg)
        cls._orig_cfg_fn = util.get_config_file_path
        util.get_config_file_path = lambda: cls.cfg
        e3d_plugin.invalidate_caches()

        cls.port = e3d_web._get_free_port(8960)
        cls.srv = e3d_web.ThreadedHTTPServer(('127.0.0.1', cls.port), e3d_web._WebHandler)
        cls.thread = threading.Thread(target=cls.srv.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f'http://127.0.0.1:{cls.port}'

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()
        util.get_config_file_path = cls._orig_cfg_fn
        e3d_plugin.invalidate_caches()
        cls.tmp.cleanup()

    def _call(self, path, body=None, method='POST'):
        data = json.dumps(body or {}).encode() if method == 'POST' else None
        req = urllib.request.Request(self.base + path, data=data, method=method)
        req.add_header('X-SEP-Token', e3d_web.API_TOKEN)
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.status, json.loads(r.read().decode('utf-8'))
        except urllib.error.HTTPError as e:
            try:
                return e.code, json.loads(e.read().decode('utf-8'))
            except Exception:
                return e.code, {}

    def test_sessions_all_uses_project_cache(self):
        """曾读取不存在的 data['all_projects']，导致在线人数永远为空。"""
        status, body = self._call('/api/sessions/all', {'force': True})
        self.assertEqual(status, 200)
        self.assertIn('proj_demo', body.get('sessions', {}))

    def test_device_paths_endpoint_works(self):
        """曾调用不存在的 launcher.detect_e3d / e3d_diag.get_userdata_dir，恒为 500。"""
        status, body = self._call('/api/settings/device-paths', {})
        self.assertEqual(status, 200, body)
        ids = {p['id'] for p in body['paths']}
        self.assertTrue({'e3d_install', 'local_projects', 'plugins_dir', 'userdata_dir'} <= ids)

    def test_plugins_reindex_single_uses_existing_function(self):
        """曾调用不存在的 e3d_plugin.scan_plugin_folder。"""
        status, body = self._call('/api/plugins/reindex', {'name': 'WebTool'})
        self.assertEqual(status, 200, body)
        self.assertTrue(body.get('ok'))

    def test_settings_update_persists_plugins_dir(self):
        new_dir = os.path.join(self.tmp.name, 'Plugins2')
        os.makedirs(new_dir, exist_ok=True)
        status, body = self._call('/api/settings/update', {'plugins_dir': new_dir})
        self.assertEqual(status, 200)
        self.assertEqual(util.normalize_path(body['settings']['plugins_dir']), util.normalize_path(new_dir))
        status, st = self._call('/api/status', method='GET')
        self.assertEqual(util.normalize_path(st['plugins_dir']), util.normalize_path(new_dir))
        # 还原
        self._call('/api/settings/update', {'plugins_dir': self.plugins_dir})

    def test_status_includes_version_and_plugins_dir(self):
        status, st = self._call('/api/status', method='GET')
        self.assertEqual(status, 200)
        self.assertIn('version', st)
        self.assertIn('plugins_dir', st)
        self.assertEqual(len(st['all_projects']), 1)

    def test_keep_alive_multiple_requests_one_connection(self):
        """HTTP/1.1 keep-alive：403 之后连接必须仍可用（请求体已被消费）。"""
        import http.client
        conn = http.client.HTTPConnection('127.0.0.1', self.port, timeout=10)
        try:
            body = json.dumps({'x': 1})
            conn.request('POST', '/api/detect', body=body, headers={'Content-Type': 'application/json'})
            r1 = conn.getresponse()
            r1.read()
            self.assertEqual(r1.status, 403)
            conn.request('GET', '/api/status', headers={'X-SEP-Token': e3d_web.API_TOKEN})
            r2 = conn.getresponse()
            data = json.loads(r2.read().decode('utf-8'))
            self.assertEqual(r2.status, 200)
            self.assertIn('all_projects', data)
        finally:
            conn.close()

    def test_my_reorder(self):
        self._call('/api/my/add', {'project_id': 'proj_demo'})
        status, body = self._call('/api/my/reorder', {'project_ids': ['proj_demo']})
        self.assertEqual(status, 200)
        self.assertTrue(body.get('ok'))
        self._call('/api/my/remove', {'id': 'proj_demo'})


class TestWebUiContract(unittest.TestCase):
    """前端与后端字段约定。"""

    @classmethod
    def setUpClass(cls):
        with open(util.resource_path('web_ui.html'), encoding='utf-8') as f:
            cls.html = f.read()

    def test_diag_lines_use_raw_line_field(self):
        """诊断明细曾渲染不存在的 l.text，页面显示 undefined。"""
        self.assertNotIn('${esc(l.text)}', self.html)
        self.assertIn('raw_line', self.html)

    def test_no_mousemove_repaint_loop(self):
        self.assertNotIn("addEventListener('mousemove'", self.html)

    def test_no_backdrop_filter(self):
        self.assertNotIn('backdrop-filter', self.html)

    def test_size_budget(self):
        self.assertLess(len(self.html.encode('utf-8')), 130 * 1024, '页面体积应保持精简')


if __name__ == '__main__':
    unittest.main(verbosity=2)
