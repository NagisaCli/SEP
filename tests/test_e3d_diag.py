#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""E3D 配置文件与环境诊断/修复模块单元测试。"""

import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_diag
import e3d_util as util


class TestE3DConfigDiagAndFix(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

        self.install_dir = os.path.join(self.tmp.name, 'Everything3D3.1')
        self.projects_dir = os.path.join(self.tmp.name, 'Projects', 'E3D3.1')
        os.makedirs(self.install_dir, exist_ok=True)
        os.makedirs(self.projects_dir, exist_ok=True)

        self.evars_bat = os.path.join(self.install_dir, 'evars.bat')
        self.evars_init = os.path.join(self.install_dir, 'evars.init')
        self.custom_evars = os.path.join(self.projects_dir, 'custom_evars.bat')

        with open(self.evars_bat, 'w', encoding='utf-8') as f:
            f.write('@echo off\r\nset projects_dir=' + self.projects_dir + '\r\n')

        with open(self.evars_init, 'w', encoding='utf-8') as f:
            f.write(
                '@echo off\r\n'
                'set pmllib=%aveva_design_exe%pmllib\\\r\n'
                'set pmlui=d:\\nonexistent\\pmlui\\;%pmlui%\r\n'
                'set PMLNET=%PMLNET%;C:\\nonexistent\\bin\r\n'
            )

        with open(self.custom_evars, 'w', encoding='utf-8') as f:
            f.write(
                '@echo off\r\n'
                'set PMLLIB=%PMLLIB%;C:\\nonexistent\\pmllib\r\n'
                'net use Z: \\\\192.0.2.1\\fake_share\r\n'
                ':: >>> SEP MANAGED PROJECTS (do not edit) >>>\r\n'
                ':: <<< SEP MANAGED PROJECTS <<<\r\n'
            )

    def test_diagnose_e3d_config_detects_dead_paths(self):
        report = e3d_diag.diagnose_e3d_config(
            e3d_install_dir=self.install_dir,
            projects_dir=self.projects_dir,
            timeout=1,
        )
        self.assertFalse(report['ok'])
        checks = {c['id']: c for c in report['checks']}

        self.assertEqual(checks['e3d_core_files']['status'], 'ok')
        self.assertEqual(checks['evars_init_paths']['status'], 'warn')
        self.assertGreaterEqual(len(checks['evars_init_paths']['invalid_lines']), 2)

        self.assertEqual(checks['custom_evars_health']['status'], 'warn')
        self.assertGreaterEqual(len(checks['custom_evars_health']['invalid_lines']), 1)

    def test_fix_e3d_config_comments_invalid_lines_and_creates_backup(self):
        res = e3d_diag.fix_e3d_config(
            e3d_install_dir=self.install_dir,
            projects_dir=self.projects_dir,
            timeout=1,
        )
        self.assertTrue(res['ok'])
        self.assertGreater(len(res['changes']), 0)

        # Check backup created
        self.assertTrue(os.path.exists(self.evars_init + '.sep.bak'))
        self.assertTrue(os.path.exists(self.custom_evars + '.sep.bak'))

        # Check content in evars.init has disabled lines
        with open(self.evars_init, 'r', encoding='utf-8') as f:
            content = f.read()
        self.assertIn('rem [SEP DISABLED - PATH NOT FOUND]', content)

        # Check content in custom_evars.bat has disabled lines
        with open(self.custom_evars, 'r', encoding='utf-8') as f:
            content = f.read()
        self.assertIn('rem [SEP DISABLED', content)


if __name__ == '__main__':
    unittest.main()
