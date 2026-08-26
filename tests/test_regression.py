#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 回归测试
============
覆盖本轮排查中确认并修复的缺陷，防止再次退化。
每个用例都对应一个真实可复现的问题，而不是为覆盖率而写。
"""

import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_launcher as launcher
import e3d_scanner as scanner
import e3d_store as store
import e3d_util as util
import e3d_web


class TestScannerManagedBlockFeedback(unittest.TestCase):
    """
    启动任意项目会把 call 写进本地 custom_evars.bat 的托管区；
    若扫描仍解析这些 call，本地项目库会凭空多出指向别处的“幽灵项目”。
    """

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.lib = self.tmp.name
        os.makedirs(os.path.join(self.lib, 'LocalProj'))
        open(os.path.join(self.lib, 'LocalProj', 'evarsLocalProj.bat'), 'w').close()

    def test_managed_block_not_rescanned_as_projects(self):
        before, _ = scanner.scan_library(self.lib)
        self.assertEqual([p['name'] for p in before], ['LocalProj'])

        remote = r'\\SomeServer\share\RemoteProj\evarsRemoteProj.bat'
        launcher.write_managed(self.lib, [remote])

        after, _ = scanner.scan_library(self.lib)
        self.assertEqual(
            [p['name'] for p in after], ['LocalProj'],
            '托管区中的 call 不应被当作该项目库的项目',
        )
        self.assertFalse(
            any('SomeServer' in p['bat_path'] for p in after),
            '不应出现指向其他路径库的幽灵项目',
        )

    def test_user_written_calls_still_discovered(self):
        """用户自己手写的 call 仍应被识别，修复不能误伤既有能力。"""
        os.makedirs(os.path.join(self.lib, 'Other'), exist_ok=True)
        user_bat = os.path.join(self.lib, 'Other', 'evarsUserProj.bat')
        open(user_bat, 'w').close()
        custom = os.path.join(self.lib, 'custom_evars.bat')
        with open(custom, 'wb') as f:
            f.write(('@echo off\r\ncall "' + user_bat + '"\r\n').encode('gbk'))

        found = scanner.parse_custom_evars(custom, self.lib)
        self.assertIn('UserProj', [p['name'] for p in found])

    def test_infrastructure_bats_excluded_after_var_expansion(self):
        """
        AVEVA 原版 custom_evars.bat 里有：
            if exist "%projects_dir%projects.bat" call "%projects_dir%projects.bat" "%projects_dir%"
        排除判断若在变量展开前做，basename 会得到整串 '%projects_dir%projects.bat'，
        projects.bat 便漏了过去，被当成一个名为 “projects” 的项目。
        """
        custom = os.path.join(self.lib, 'custom_evars.bat')
        body = (
            '@echo off\r\n'
            'if exist "%projects_dir%projects.bat" call "%projects_dir%projects.bat" "%projects_dir%"\r\n'
            'if exist "%projects_dir%LocalProj\\evarsLocalProj.bat" '
            'call "%projects_dir%LocalProj\\evarsLocalProj.bat"\r\n'
        )
        with open(custom, 'wb') as f:
            f.write(body.encode('gbk'))

        names = [p['name'] for p in scanner.parse_custom_evars(custom, self.lib)]
        self.assertNotIn('projects', names, 'projects.bat 不是项目')
        self.assertIn('LocalProj', names, '真实项目仍须被识别')

        found = [p['name'] for p in scanner.scan_library(self.lib)[0]]
        self.assertNotIn('projects', found)

    def test_strip_managed_block_keeps_user_content(self):
        text = (
            '@echo off\r\n'
            'rem keep me\r\n'
            + launcher.MANAGED_START + '\r\n'
            'call "X:\\a\\evarsA.bat"\r\n'
            + launcher.MANAGED_END + '\r\n'
            'rem also keep me\r\n'
        )
        out = scanner.strip_managed_block(text)
        self.assertIn('rem keep me', out)
        self.assertIn('rem also keep me', out)
        self.assertNotIn('evarsA.bat', out)


class TestTransactionRollback(unittest.TestCase):
    """写入失败必须完整回滚，且不残留半成品文件或备份。"""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.local = os.path.join(self.tmp.name, 'local')
        os.makedirs(self.local)
        self.custom = os.path.join(self.local, 'custom_evars.bat')

        self.orig = ('@echo off\r\nset projects_dir=' + r'C:\ORIGINAL' + '\\\r\n').encode('gbk')
        self.evars_bat = os.path.join(self.tmp.name, 'evars.bat')
        self.evars_init = os.path.join(self.tmp.name, 'evars.init')
        for p in (self.evars_bat, self.evars_init):
            with open(p, 'wb') as f:
                f.write(self.orig)

        self._saved = (launcher.EVARS_BAT, launcher.EVARS_INIT)
        launcher.EVARS_BAT, launcher.EVARS_INIT = self.evars_bat, self.evars_init
        self.addCleanup(self._restore_globals)

        self.cfg = os.path.join(self.tmp.name, 'cfg.json')
        store.save_data({
            'version': 3,
            'settings': {'local_projects_dir': self.local, 'e3d_lnk': 'x'},
            'categories': [], 'project_meta': {}, 'libraries': [],
            'my_projects': [], 'all_projects_cache': [],
        }, self.cfg)
        self._orig_cfg_fn = util.get_config_file_path
        util.get_config_file_path = lambda: self.cfg
        self.addCleanup(self._restore_cfg_fn)

    def _restore_globals(self):
        launcher.EVARS_BAT, launcher.EVARS_INIT = self._saved

    def _restore_cfg_fn(self):
        util.get_config_file_path = self._orig_cfg_fn

    def _fail_write(self):
        ghost = os.path.join(self.tmp.name, 'NOPE', 'evarsGhost.bat')
        with self.assertRaises(launcher.LauncherError):
            launcher.write_mode('single', {'bat_path': ghost, 'name': 'Ghost'})

    def test_failed_write_does_not_leave_new_custom_evars(self):
        self.assertFalse(os.path.exists(self.custom))
        self._fail_write()
        self.assertFalse(
            os.path.exists(self.custom),
            '事务失败时新建的 custom_evars.bat 必须被删除',
        )

    def test_failed_write_restores_evars_byte_exact(self):
        self._fail_write()
        for p in (self.evars_bat, self.evars_init):
            with open(p, 'rb') as f:
                self.assertEqual(f.read(), self.orig, f'{p} 未按字节还原')

    def test_failed_write_preserves_existing_custom_evars(self):
        user = ('@echo off\r\nrem USER LINE\r\ncall "' + r'C:\u\own.bat' + '"\r\n').encode('gbk')
        with open(self.custom, 'wb') as f:
            f.write(user)
        self._fail_write()
        with open(self.custom, 'rb') as f:
            self.assertEqual(f.read(), user, '用户原有内容必须原样保留')

    def test_no_backup_files_left_behind(self):
        self._fail_write()
        leftovers = [n for n in os.listdir(self.tmp.name) if n.endswith('.sep.bak')]
        leftovers += [n for n in os.listdir(self.local) if n.endswith('.sep.bak')]
        self.assertEqual(leftovers, [], '回滚后不应残留 .sep.bak')

    def test_repeated_set_projects_dir_keeps_pristine_backup(self):
        """
        set_projects_dir 不得覆盖外层事务已保存的备份，
        否则重试写入后再回滚只会回到「改过一次」的中间状态。
        """
        launcher._backup(self.evars_bat)
        launcher.set_projects_dir(self.evars_bat, r'C:\FIRST')
        launcher.set_projects_dir(self.evars_bat, r'C:\SECOND')
        launcher._restore(self.evars_bat, True)
        with open(self.evars_bat, 'rb') as f:
            self.assertEqual(f.read(), self.orig)


class TestBackupHelpers(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def test_restore_removes_file_created_during_failed_txn(self):
        p = os.path.join(self.tmp.name, 'new.bat')
        existed = launcher._backup(p)
        self.assertFalse(existed)
        with open(p, 'w') as f:
            f.write('half written')
        launcher._restore(p, existed)
        self.assertFalse(os.path.exists(p))

    def test_backup_clears_stale_bak_when_file_absent(self):
        """上次崩溃留下的陈旧 .sep.bak 不能污染下一次回滚。"""
        p = os.path.join(self.tmp.name, 'x.bat')
        with open(p + '.sep.bak', 'w') as f:
            f.write('ANCIENT')
        existed = launcher._backup(p)
        self.assertFalse(existed)
        self.assertFalse(os.path.exists(p + '.sep.bak'))


class TestNormalizeEdgeCases(unittest.TestCase):
    def test_unc_prefix_not_degraded_to_local(self):
        self.assertEqual(util.normalize_path('\\\\'), '\\\\')
        self.assertEqual(util.path_protocol('\\\\'), 'unc')

    def test_unc_trailing_slash_stripped(self):
        self.assertEqual(util.normalize_path('\\\\srv\\share\\'), '\\\\srv\\share')
        self.assertEqual(util.normalize_path('\\\\srv\\share\\sub\\'), '\\\\srv\\share\\sub')

    def test_smb_trailing_slash_stripped(self):
        self.assertEqual(util.normalize_path('smb://host/share/'), '\\\\host\\share')

    def test_drive_root_keeps_slash(self):
        for s in ('C:', 'C:/', 'C:\\'):
            self.assertEqual(util.normalize_path(s), 'C:\\')

    def test_two_char_relative_path(self):
        self.assertEqual(util.normalize_path('ab\\'), 'ab')


class TestCategoryUniqueness(unittest.TestCase):
    def test_rename_to_existing_name_rejected(self):
        d = store.default_data()
        store.add_category(d, 'Alpha')
        beta = store.add_category(d, 'Beta')
        with self.assertRaises(ValueError):
            store.update_category(d, beta['id'], name='Alpha')
        names = sorted(c['name'] for c in d['categories'])
        self.assertEqual(names, ['Alpha', 'Beta'])

    def test_rename_to_own_name_allowed(self):
        d = store.default_data()
        a = store.add_category(d, 'Alpha')
        store.update_category(d, a['id'], name='Alpha', color='#fff')
        self.assertEqual(a['color'], '#fff')


class TestWebHandlerHardening(unittest.TestCase):
    """通过真实 HTTP 请求验证鉴权、异常兜底与请求体上限。"""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.cfg = os.path.join(cls.tmp.name, 'cfg.json')
        store.save_data(store.default_data(), cls.cfg)
        cls._orig_cfg_fn = util.get_config_file_path
        # 隔离配置：绝不能碰用户真实的 e3d_projects.json
        util.get_config_file_path = lambda: cls.cfg

        cls.port = e3d_web._get_free_port(8950)
        cls.srv = e3d_web.ThreadedHTTPServer(('127.0.0.1', cls.port), e3d_web._WebHandler)
        cls.thread = threading.Thread(target=cls.srv.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f'http://127.0.0.1:{cls.port}'

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()
        util.get_config_file_path = cls._orig_cfg_fn
        cls.tmp.cleanup()

    def _post(self, path, body=None, token=e3d_web.API_TOKEN, headers=None):
        data = json.dumps(body or {}).encode()
        req = urllib.request.Request(self.base + path, data=data, method='POST')
        if token:
            req.add_header('X-SEP-Token', token)
        for k, v in (headers or {}).items():
            req.add_header(k, v)
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status, json.loads(r.read().decode('utf-8'))
        except urllib.error.HTTPError as e:
            try:
                return e.code, json.loads(e.read().decode('utf-8'))
            except Exception:
                return e.code, {}

    def test_open_config_dir_does_not_crash(self):
        """曾因 e3d_web 未 import sys 触发 NameError，直接掐断连接。"""
        status, body = self._post('/api/settings/open-config-dir', {})
        self.assertEqual(status, 200)
        self.assertTrue(body.get('ok'))

    def test_requires_token(self):
        self.assertEqual(self._post('/api/detect', {}, token=None)[0], 403)
        self.assertEqual(self._post('/api/detect', {}, token='0' * 32)[0], 403)

    def test_rejects_foreign_origin(self):
        status, _ = self._post('/api/detect', {}, headers={'Origin': 'http://evil.example'})
        self.assertEqual(status, 403)

    def test_oversized_body_rejected_not_crash(self):
        status, body = self._post(
            '/api/category/add', {'name': 'x'},
            headers={'Content-Length': str(64 * 1024 * 1024)},
        )
        self.assertIn(status, (400, 500))

    def test_duplicate_category_rename_returns_400(self):
        self._post('/api/category/add', {'name': 'CatA'})
        r = self._post('/api/category/add', {'name': 'CatB'})
        cat_b_id = r[1]['category']['id']
        status, body = self._post('/api/category/update', {'id': cat_b_id, 'name': 'CatA'})
        self.assertEqual(status, 400)
        self.assertIn('同名', body.get('error', ''))

    def test_unknown_route_404(self):
        self.assertEqual(self._post('/api/nope', {})[0], 404)


class TestWebUiEscaping(unittest.TestCase):
    """
    诊断面板曾把路径/命令拼进内联 onclick 的单引号字符串里；
    esc() 产生的 &#39; 在属性中会被浏览器还原成单引号，从而逃逸字符串。
    """

    @classmethod
    def setUpClass(cls):
        with open(util.resource_path('web_ui.html'), encoding='utf-8') as f:
            cls.html = f.read()

    def test_no_inline_onclick_string_interpolation_of_paths(self):
        self.assertNotIn("applyFix('guest_auth','${esc(", self.html)
        self.assertNotIn("navigator.clipboard.writeText('${esc(", self.html)

    def test_uses_data_attributes_instead(self):
        self.assertIn('data-copy-cmd=', self.html)
        self.assertIn('data-fix-id=', self.html)
        self.assertIn('data-fix-path=', self.html)


class TestNoUndefinedNames(unittest.TestCase):
    """静态编译每个模块，捕捉语法错误。"""

    def test_modules_compile(self):
        import py_compile
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        for name in ('e3d_util.py', 'e3d_store.py', 'e3d_scanner.py',
                     'e3d_launcher.py', 'e3d_web.py', 'e3d_diag.py',
                     'e3d_config.py', 'switch_e3d_project.py'):
            py_compile.compile(os.path.join(root, name), doraise=True)


if __name__ == '__main__':
    unittest.main(verbosity=2)
