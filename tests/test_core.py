#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SEP 核心模块单元测试：路径规范化 / 扫描 / 托管区写入 / 迁移。"""

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_diag
import e3d_launcher as launcher
import e3d_scanner as scanner
import e3d_store as store
import e3d_util as util
import e3d_web


class TestNormalize(unittest.TestCase):
    def test_smb_to_unc(self):
        self.assertEqual(util.normalize_path('smb://server/share/path'), '\\\\server\\share\\path')
        self.assertEqual(util.normalize_path('smb://server/share'), '\\\\server\\share')

    def test_file_uri(self):
        self.assertEqual(util.normalize_path('file:///C:/Projects/x'), 'C:\\Projects\\x')
        self.assertEqual(util.normalize_path('file://server/share'), '\\\\server\\share')

    def test_slashes_and_trailing(self):
        self.assertEqual(util.normalize_path('D:/AVEVA/Projects/E3D3.1/'), 'D:\\AVEVA\\Projects\\E3D3.1')
        self.assertEqual(util.normalize_path('"C:\\x\\y"'), 'C:\\x\\y')

    def test_url_untouched(self):
        self.assertEqual(util.normalize_path('https://x/y/custom_evars.bat'), 'https://x/y/custom_evars.bat')

    def test_protocol(self):
        self.assertEqual(util.path_protocol('\\\\srv\\share'), 'unc')
        self.assertEqual(util.path_protocol('C:\\x'), 'local')
        self.assertEqual(util.path_protocol('http://x'), 'url')


class TestScanner(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def _write(self, rel, content, encoding='utf-8'):
        p = os.path.join(self.tmp.name, rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, 'w', encoding=encoding) as f:
            f.write(content)
        return p

    def test_classify_single_bat(self):
        p = self._write('evarsDPH.bat', '@echo off\r\n')
        info = scanner.classify(p)
        self.assertEqual(info['kind'], 'project')

    def test_classify_collection_subfolders(self):
        self._write(os.path.join('ProjA', 'evarsAAA.bat'), '')
        self._write(os.path.join('ProjB', 'evarsBB.bat'), '')
        self._write(os.path.join('NoProject', 'readme.txt'), 'x')
        info = scanner.classify(self.tmp.name)
        self.assertEqual(info['kind'], 'collection')
        self.assertEqual(len(info.get('project_dirs', [])), 2)

    def test_classify_project_folder_direct(self):
        self._write('evarsAAA.bat', '')
        self._write('evarsBB.bat', '')
        self._write('evars.bat', '')
        self._write('readme.txt', 'x')
        info = scanner.classify(self.tmp.name)
        self.assertEqual(info['kind'], 'project')
        self.assertEqual(len(info.get('direct', [])), 2)

    def test_classify_custom_evars_alone(self):
        # 包含 custom_evars.bat 识别为项目库，并解析项目
        self._write('custom_evars.bat', 'call "D:\\Projects\\evarsAAA.bat"\r\n')
        info = scanner.classify(self.tmp.name)
        self.assertEqual(info['kind'], 'collection')
        self.assertEqual(len(info.get('custom_projects', [])), 1)

    def test_classify_invalid(self):
        d = os.path.join(self.tmp.name, 'nope')
        os.makedirs(d)
        info = scanner.classify(d)
        self.assertEqual(info['kind'], 'invalid')

    def test_classify_url_unsupported(self):
        info = scanner.classify('https://x/custom_evars.bat')
        self.assertEqual(info['kind'], 'unsupported')

    def test_scan_project_folder_direct(self):
        self._write('evarsDPH.bat', '')
        self._write('evarsAB.bat', '')
        projects, info = scanner.scan_library(self.tmp.name)
        self.assertEqual(info['kind'], 'project')
        self.assertEqual(sorted(p['name'] for p in projects), ['AB', 'DPH'])

    def test_scan_single_bat_file(self):
        p = self._write('evarsDPH.bat', '@echo off\r\n')
        projects, info = scanner.scan_library(p)
        self.assertEqual(info['kind'], 'project')
        self.assertEqual(len(projects), 1)
        self.assertEqual(projects[0]['name'], 'DPH')
        self.assertEqual(projects[0]['bat_path'], p)

    def test_scan_library_subfolders(self):
        self._write(os.path.join('ProjA', 'evarsAAA.bat'), '')
        self._write(os.path.join('ProjB', 'evarsBB.bat'), '')
        self._write(os.path.join('NoProject', 'readme.txt'), 'x')
        self._write(os.path.join('Deep', 'Inner', 'evarsCC.bat'), '')
        projects, info = scanner.scan_library(self.tmp.name)
        self.assertEqual(info['kind'], 'collection')
        self.assertEqual(sorted(p['name'] for p in projects), ['AAA', 'BB'])
        pa = [p for p in projects if p['name'] == 'AAA'][0]
        self.assertEqual(pa['project_dir'], os.path.join(self.tmp.name, 'ProjA'))
        self.assertFalse(any(p['name'] == 'CC' for p in projects))

    def test_scan_custom_evars(self):
        self._write('custom_evars.bat', 'if exist "%projects_dir%ProjA\\evarsAAA.bat" call "%projects_dir%ProjA\\evarsAAA.bat"\r\ncall "D:\\Projects\\ProjB\\evarsBB.bat"\r\n')
        projects, info = scanner.scan_library(self.tmp.name)
        self.assertEqual(info['kind'], 'collection')
        self.assertEqual(sorted(p['name'] for p in projects), ['AAA', 'BB'])

    def test_project_name(self):
        self.assertEqual(scanner.project_name('evarsDPH.bat'), 'DPH')
        self.assertEqual(scanner.project_name(r'D:\x\evarsAB.bat'), 'AB')
        self.assertEqual(scanner.project_name('other.bat'), 'other')


class TestLauncher(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.local = self.tmp.name

    def test_write_managed_idempotent(self):
        path = launcher.write_managed(self.local, [r'C:\x\evarsA.bat'])
        self.assertTrue(os.path.exists(path))
        self.assertEqual(launcher.read_managed(self.local), [r'C:\x\evarsA.bat'])

        # 用户内容要保留
        text, _ = util.read_text_smart(path)
        text = 'rem 用户原有内容\r\n' + text
        util.write_text_preserve(path, text, 'gbk')

        launcher.write_managed(self.local, [r'C:\x\evarsB.bat', r'C:\x\evarsC.bat'])
        self.assertEqual(
            launcher.read_managed(self.local),
            [r'C:\x\evarsB.bat', r'C:\x\evarsC.bat'],
        )
        after, enc = util.read_text_smart(path)
        self.assertIn('rem 用户原有内容', after)
        self.assertEqual(enc, 'gbk')
        self.assertEqual(after.count(launcher.MANAGED_START), 1)
        self.assertTrue(after.rstrip().endswith(launcher.MANAGED_END))

    def test_write_managed_empty(self):
        launcher.write_managed(self.local, [])
        self.assertEqual(launcher.read_managed(self.local), [])

    def test_set_projects_dir(self):
        f = os.path.join(self.local, 'evars.bat')
        with open(f, 'w', encoding='gbk') as fh:
            fh.write('@echo off\r\nset projects_dir=D:\\old\\projects\\\r\n')
        launcher.set_projects_dir(f, 'D:\\new\\projects')
        self.assertEqual(launcher.read_projects_dir(f), 'D:\\new\\projects\\')

    def test_set_projects_dir_gbk_chinese(self):
        f = os.path.join(self.local, 'evars.bat')
        with open(f, 'wb') as fh:
            fh.write('@echo off\r\nset projects_dir=D:\\旧\\项目\\\r\n'.encode('gbk'))
        launcher.set_projects_dir(f, 'D:\\新\\项目')
        self.assertEqual(launcher.read_projects_dir(f), 'D:\\新\\项目\\')

    def test_set_projects_dir_utf8_bom(self):
        f = os.path.join(self.local, 'evars.bat')
        with open(f, 'wb') as fh:
            fh.write('@echo off\r\nset projects_dir=D:\\old\\\r\n'.encode('utf-8-sig'))
        launcher.set_projects_dir(f, 'D:\\new')
        with open(f, 'rb') as fh:
            raw = fh.read()
        self.assertTrue(raw.startswith(b'\xef\xbb\xbf'))
        self.assertEqual(raw.count(b'\xef\xbb\xbf'), 1)
        self.assertEqual(launcher.read_projects_dir(f), 'D:\\new\\')

    def test_managed_block_quotes(self):
        block = launcher.managed_block([r'C:\x\evarsA.bat'])
        self.assertIn('call "C:\\x\\evarsA.bat"', block)
        self.assertIn(launcher.MANAGED_START, block)

    def test_write_managed_multiple(self):
        p = launcher.write_managed(self.local, [r'C:\x\evarsA.bat', r'C:\y\evarsB.bat'])
        self.assertTrue(os.path.exists(p))
        text, _ = util.read_text_smart(p)
        self.assertIn('call "C:\\x\\evarsA.bat"', text)
        self.assertIn('call "C:\\y\\evarsB.bat"', text)
        self.assertIn(launcher.MANAGED_START, text)
        self.assertIn(launcher.MANAGED_END, text)

    def test_read_projects_dir_variants(self):
        f1 = os.path.join(self.local, 'evars1.bat')
        with open(f1, 'w', encoding='utf-8') as fh:
            fh.write('projects_dir=D:\\test\\\r\n')
        self.assertEqual(launcher.read_projects_dir(f1), 'D:\\test\\')

        f2 = os.path.join(self.local, 'evars2.bat')
        with open(f2, 'w', encoding='utf-8') as fh:
            fh.write('set projects_dir="D:\\test2\\"\r\n')
        self.assertEqual(launcher.read_projects_dir(f2), 'D:\\test2\\')



class TestStore(unittest.TestCase):
    def test_migration_v1(self):
        with tempfile.TemporaryDirectory() as tmp:
            cfg = os.path.join(tmp, 'e3d_projects.json')
            with open(cfg, 'w', encoding='utf-8') as f:
                json.dump({
                    'projects': {
                        'DPH': '\\\\srv\\share\\DPH',
                        'AAA': '\\\\srv\\share\\evarsAAA.bat',
                    }
                }, f, ensure_ascii=False)
            data = store.load_data(cfg)
            self.assertEqual(data['version'], 3)
            libs = [x for x in data['libraries'] if x['name'] == 'DPH']
            self.assertEqual(len(libs), 1)
            self.assertEqual(libs[0]['path'], '\\\\srv\\share\\DPH')
            my = [x for x in data['my_projects'] if x['name'] == 'AAA']
            self.assertEqual(len(my), 1)
            self.assertEqual(my[0]['bat_path'], '\\\\srv\\share\\evarsAAA.bat')

    def test_migration_v2_to_v3(self):
        with tempfile.TemporaryDirectory() as tmp:
            cfg = os.path.join(tmp, 'e3d_projects.json')
            with open(cfg, 'w', encoding='utf-8') as f:
                json.dump({
                    'version': 2,
                    'settings': {'e3d_lnk': ''},
                    'libraries': [],
                    'my_projects': [{
                        'id': 'proj_x', 'name': 'X',
                        'bat_path': r'D:\x\evarsX.bat',
                        'lib_id': None, 'source': 'user',
                        'added_at': util.now_iso(),
                    }],
                    'all_projects_cache': [],
                }, f, ensure_ascii=False)
            data = store.load_data(cfg)
            self.assertEqual(data['version'], 3)
            self.assertEqual(data['categories'], [])
            self.assertEqual(data['project_meta'], {})
            self.assertEqual(data['my_projects'][0]['name'], 'X')

    def test_v2_roundtrip(self):
        with tempfile.TemporaryDirectory() as tmp:
            cfg = os.path.join(tmp, 'e3d_projects.json')
            data = store.load_data(cfg)
            data['my_projects'].append({
                'id': 'proj_x',
                'name': 'X',
                'bat_path': r'D:\x\evarsX.bat',
                'lib_id': None,
                'source': 'user',
                'added_at': util.now_iso(),
            })
            store.save_data(data, cfg)
            again = store.load_data(cfg)
            self.assertEqual(again['my_projects'][0]['name'], 'X')

    def test_category_crud(self):
        data = store.default_data()
        cat = store.add_category(data, '  DPH  ', '#ff0000')
        self.assertEqual(cat['name'], 'DPH')
        self.assertEqual(cat['color'], '#ff0000')
        dup = store.add_category(data, 'dph', '#00ff00')
        self.assertEqual(dup['id'], cat['id'])
        self.assertEqual(len(data['categories']), 1)

        updated = store.update_category(data, cat['id'], 'Piping', '#00ff00')
        self.assertEqual(updated['name'], 'Piping')
        self.assertEqual(updated['color'], '#00ff00')

        meta = store.update_project_meta(data, 'proj_x', {'category_id': cat['id']})
        self.assertEqual(meta['category_id'], cat['id'])
        self.assertTrue(store.remove_category(data, cat['id']))
        self.assertEqual(store.get_project_meta(data, 'proj_x')['category_id'], '')
        self.assertFalse(store.remove_category(data, cat['id']))

    def test_project_meta_persist(self):
        with tempfile.TemporaryDirectory() as tmp:
            cfg = os.path.join(tmp, 'e3d_projects.json')
            data = store.load_data(cfg)
            store.update_project_meta(data, 'proj_1', {
                'name': '一号项目',
                'tags': ['P&ID', ' piping ', 'P&ID'],
                'description': '描述',
                'notes': '备注',
                'status': '进行中',
                'owner': '张三',
            })
            store.save_data(data, cfg)
            again = store.load_data(cfg)
            meta = store.get_project_meta(again, 'proj_1')
            self.assertEqual(meta['name'], '一号项目')
            self.assertEqual(meta['tags'], ['P&ID', 'piping'])
            self.assertEqual(meta['status'], '进行中')
            self.assertEqual(meta['owner'], '张三')
            self.assertTrue(meta['updated_at'])

    def test_merge_project_meta(self):
        data = store.default_data()
        proj = {'id': 'proj_1', 'name': 'AAA', 'bat_path': r'D:\x\evarsAAA.bat'}
        merged = store.merge_project_meta(proj, None)
        self.assertEqual(merged['name'], 'AAA')
        self.assertEqual(merged['display_name'], 'AAA')
        store.update_project_meta(data, 'proj_1', {'name': '我的项目', 'tags': ['a']})
        merged = store.merge_project_meta(proj, store.get_project_meta(data, 'proj_1'))
        self.assertEqual(merged['name'], '我的项目')
        self.assertEqual(merged['tags'], ['a'])


class TestDiagnostics(unittest.TestCase):
    def test_diag_local_ok(self):
        with tempfile.TemporaryDirectory() as tmp:
            lib = os.path.join(tmp, 'lib')
            proj = os.path.join(lib, 'ProjA')
            os.makedirs(proj, exist_ok=True)
            with open(os.path.join(proj, 'evarsAAA.bat'), 'w', encoding='utf-8') as f:
                f.write('@echo off\r\n')
            report = e3d_diag.diagnose(lib, timeout=6)
            self.assertTrue(report['ok'])
            ids = [c['id'] for c in report['checks']]
            self.assertIn('exists', ids)
            self.assertIn('project_files', ids)
            self.assertEqual([c['status'] for c in report['checks'] if c['id'] == 'project_files'], ['ok'])

    def test_diag_missing_path_fail(self):
        with tempfile.TemporaryDirectory() as tmp:
            report = e3d_diag.diagnose(os.path.join(tmp, 'nope'), timeout=6)
            self.assertFalse(report['ok'])
            self.assertEqual(report['checks'][0]['status'], 'fail')

    def test_diag_no_projects_fail(self):
        with tempfile.TemporaryDirectory() as tmp:
            os.makedirs(os.path.join(tmp, 'empty'), exist_ok=True)
            report = e3d_diag.diagnose(tmp, timeout=6)
            self.assertFalse(report['ok'])
            by_id = {c['id']: c for c in report['checks']}
            self.assertEqual(by_id['project_files']['status'], 'fail')

    def test_apply_fix_unknown(self):
        result = e3d_diag.apply_fix('not_a_fix')
        self.assertFalse(result['ok'])
        self.assertIn('未知修复项', result['message'])


class TestSecurity(unittest.TestCase):
    def test_validate_safe_bat_path_valid(self):
        self.assertEqual(util.validate_safe_bat_path(r'C:\Program Files\AVEVA\evarsDPH.bat'), r'C:\Program Files\AVEVA\evarsDPH.bat')
        self.assertEqual(util.validate_safe_bat_path(r'D:\项目\evarsAAA.bat'), r'D:\项目\evarsAAA.bat')
        self.assertEqual(util.validate_safe_bat_path(''), '')

    def test_validate_safe_bat_path_dangerous(self):
        dangerous = [
            r'C:\x\evars.bat" & calc.exe & "',
            'C:\\x\\evars.bat\r\ncalc.exe',
            'C:\\x\\evars.bat\ncalc.exe',
            'C:\\x\\evars.bat | whoami',
            'C:\\x\\evars.bat < in.txt',
            'C:\\x\\evars.bat > out.txt',
            'C:\\x\\evars.bat ^& echo 1',
        ]
        for p in dangerous:
            with self.assertRaises(ValueError):
                util.validate_safe_bat_path(p)

    def test_managed_block_injection_blocked(self):
        with self.assertRaises(ValueError):
            launcher.managed_block([r'C:\x\test.bat" & notepad.exe & "'])

    def test_set_projects_dir_injection_blocked(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = os.path.join(tmp, 'evars.bat')
            with open(f, 'w', encoding='utf-8') as fh:
                fh.write('@echo off\r\nset projects_dir=D:\\old\\\r\n')
            with self.assertRaises(launcher.LauncherError):
                launcher.set_projects_dir(f, r'D:\old\" & calc.exe & "')


class TestWebSecurity(unittest.TestCase):
    def test_token_injected_in_html(self):
        html = e3d_web._load_html()
        self.assertIn(e3d_web.API_TOKEN, html)
        self.assertIn('window.__SEP_TOKEN__', html)


class TestLaunchModes(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.local = os.path.join(self.tmp.name, 'LocalProjects')
        os.makedirs(self.local, exist_ok=True)
        self.e3d_dir = os.path.join(self.tmp.name, 'E3D')
        os.makedirs(self.e3d_dir, exist_ok=True)

        self.evars_bat = os.path.join(self.e3d_dir, 'evars.bat')
        with open(self.evars_bat, 'w', encoding='utf-8') as f:
            f.write('@echo off\r\nset projects_dir=D:\\dummy\\\r\nfor %%x in ("%projects_dir%\\") do set projects_dir=%%~dpx\r\n')
        self.evars_init = os.path.join(self.e3d_dir, 'evars.init')
        with open(self.evars_init, 'w', encoding='utf-8') as f:
            f.write('set projects_dir=D:\\dummy\\\r\n')

        launcher.EVARS_BAT = self.evars_bat
        launcher.EVARS_INIT = self.evars_init

        self.cfg_file = os.path.join(self.tmp.name, 'e3d_projects.json')
        self.proj_a = os.path.join(self.local, 'evarsA.bat')
        with open(self.proj_a, 'w', encoding='utf-8') as f:
            f.write('@echo off\r\n')

        data = {
            'version': 3,
            'settings': {'local_projects_dir': self.local, 'e3d_lnk': ''},
            'categories': [],
            'project_meta': {},
            'libraries': [],
            'my_projects': [{'id': 'proj_a', 'name': 'A', 'bat_path': self.proj_a, 'lib_id': None, 'source': 'user', 'added_at': ''}],
            'all_projects_cache': [{'id': 'proj_a', 'name': 'A', 'bat_path': self.proj_a, 'lib_id': None, 'discovered_at': ''}],
        }
        store.save_data(data, self.cfg_file)

    def test_write_mode_single(self):
        # 覆写 store.get_config_path
        orig_fn = util.get_config_file_path
        util.get_config_file_path = lambda: self.cfg_file
        try:
            summary = launcher.write_mode('single', {'bat_path': self.proj_a, 'name': 'A'})
            self.assertEqual(summary['projects_dir'], self.local)
            self.assertEqual(launcher.read_projects_dir(self.evars_bat), self.local + '\\')
            self.assertEqual(launcher.read_projects_dir(self.evars_init), self.local + '\\')
            managed = launcher.read_managed(self.local)
            self.assertEqual(managed, [self.proj_a])
        finally:
            util.get_config_file_path = orig_fn

    def test_write_mode_library(self):
        orig_fn = util.get_config_file_path
        util.get_config_file_path = lambda: self.cfg_file
        lib_dir = os.path.join(self.tmp.name, 'RemoteLib')
        os.makedirs(lib_dir, exist_ok=True)
        try:
            summary = launcher.write_mode('library', {'path': lib_dir, 'name': 'Remote'})
            self.assertEqual(summary['projects_dir'], lib_dir)
            self.assertEqual(launcher.read_projects_dir(self.evars_bat), lib_dir + '\\')
            self.assertEqual(launcher.read_projects_dir(self.evars_init), lib_dir + '\\')
        finally:
            util.get_config_file_path = orig_fn


class TestGlobalStorage(unittest.TestCase):
    def test_get_user_data_dir_default(self):
        d = util.get_user_data_dir()
        self.assertTrue(bool(d))
        self.assertTrue(os.path.isabs(d))

    def test_get_config_file_path(self):
        p = util.get_config_file_path()
        self.assertTrue(p.endswith('e3d_projects.json'))

    def test_portable_mode(self):
        old_env = os.environ.get('SEP_PORTABLE')
        try:
            os.environ['SEP_PORTABLE'] = '1'
            self.assertEqual(util.get_user_data_dir(), util.SCRIPT_DIR)
        finally:
            if old_env is None:
                os.environ.pop('SEP_PORTABLE', None)
            else:
                os.environ['SEP_PORTABLE'] = old_env

    def test_sanitize_unc_paths(self):
        text = r'call \\NonExistentHost999\000E3D31\DPH\evarsDPH.bat'
        fixed = util.sanitize_unc_paths(text, r'\\192.168.2.10\000E3D31')
        self.assertIn(r'\\192.168.2.10\000E3D31\DPH\evarsDPH.bat', fixed)

    def test_read_managed_with_if_exist(self):
        with tempfile.TemporaryDirectory() as tmp:
            custom_bat = os.path.join(tmp, 'custom_evars.bat')
            with open(custom_bat, 'w', encoding='utf-8') as f:
                f.write(launcher.MANAGED_START + '\n')
                f.write('if exist "C:\\x\\evarsA.bat" call "C:\\x\\evarsA.bat"\n')
                f.write('call "C:\\y\\evarsB.bat"\n')
                f.write(launcher.MANAGED_END + '\n')
            res = launcher.read_managed(tmp)
            self.assertEqual(len(res), 2)
            self.assertEqual(res[0], 'C:\\x\\evarsA.bat')
            self.assertEqual(res[1], 'C:\\y\\evarsB.bat')


if __name__ == '__main__':
    unittest.main(verbosity=2)


