#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SEP 插件管理模块单元测试。"""

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_plugin
import e3d_util as util


class TestE3DPluginManager(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

        self.plugins_dir = os.path.join(self.tmp.name, 'Plugins')
        self.projects_dir = os.path.join(self.tmp.name, 'Projects', 'E3D3.1')
        os.makedirs(self.plugins_dir, exist_ok=True)
        os.makedirs(self.projects_dir, exist_ok=True)

        self.custom_evars = os.path.join(self.projects_dir, 'custom_evars.bat')
        with open(self.custom_evars, 'w', encoding='utf-8') as f:
            f.write(
                '@echo off\r\n'
                'rem User custom lines\r\n'
                ':: >>> SEP MANAGED PROJECTS (do not edit) >>>\r\n'
                ':: <<< SEP MANAGED PROJECTS <<<\r\n'
            )

    def test_create_and_scan_plugin(self):
        created = e3d_plugin.create_plugin_skeleton('TestTool', plugins_dir=self.plugins_dir, has_pmllib=True, has_pmlnet=True)
        self.assertTrue(os.path.isdir(created))
        self.assertTrue(os.path.isdir(os.path.join(created, 'pmllib')))
        self.assertTrue(os.path.isdir(os.path.join(created, 'bin')))

        plugins = e3d_plugin.scan_plugins(plugins_dir=self.plugins_dir, local_projects_dir=self.projects_dir)
        self.assertEqual(len(plugins), 1)
        p = plugins[0]
        self.assertEqual(p['name'], 'TestTool')
        self.assertTrue(p['has_pmllib'])
        self.assertTrue(p['has_pmlnet'])
        self.assertTrue(p['has_pml_index'])
        self.assertFalse(p['enabled'])

    def test_toggle_plugin_and_write_block(self):
        e3d_plugin.create_plugin_skeleton('PluginA', plugins_dir=self.plugins_dir)
        e3d_plugin.create_plugin_skeleton('PluginB', plugins_dir=self.plugins_dir)

        # Enable PluginA
        e3d_plugin.set_plugin_enabled('PluginA', True, local_projects_dir=self.projects_dir, plugins_dir=self.plugins_dir)

        enabled = e3d_plugin.read_enabled_plugins(self.projects_dir)
        self.assertIn('PluginA', enabled)
        self.assertNotIn('PluginB', enabled)

        with open(self.custom_evars, 'r', encoding='utf-8') as f:
            content = f.read()
        self.assertIn(e3d_plugin.PLUGINS_BLOCK_START, content)
        self.assertIn('PluginA', content)

        # Disable PluginA
        e3d_plugin.set_plugin_enabled('PluginA', False, local_projects_dir=self.projects_dir, plugins_dir=self.plugins_dir)
        enabled_after = e3d_plugin.read_enabled_plugins(self.projects_dir)
        self.assertNotIn('PluginA', enabled_after)

    def test_rebuild_pml_index(self):
        pml_dir = os.path.join(self.plugins_dir, 'CustomPml', 'pmllib')
        sub_forms = os.path.join(pml_dir, 'forms')
        os.makedirs(sub_forms, exist_ok=True)

        with open(os.path.join(sub_forms, 'myform.pmlfrm'), 'w') as f:
            f.write('setup form !!myform\r\nexit\r\n')
        with open(os.path.join(pml_dir, 'root_obj.pmlobj'), 'w') as f:
            f.write('define object root_obj\r\nendobject\r\n')

        ok, msg = e3d_plugin.rebuild_pml_index(pml_dir)
        self.assertTrue(ok)

        idx_path = os.path.join(pml_dir, 'pml.index')
        self.assertTrue(os.path.isfile(idx_path))
        with open(idx_path, 'r') as f:
            idx_content = f.read()

        self.assertIn('/forms/', idx_content)
        self.assertIn('myform.pmlfrm', idx_content)
        self.assertIn('root_obj.pmlobj', idx_content)

    def test_batch_toggle_and_hotload_macro(self):
        e3d_plugin.create_plugin_skeleton('Plugin1', plugins_dir=self.plugins_dir)
        e3d_plugin.create_plugin_skeleton('Plugin2', plugins_dir=self.plugins_dir)

        # Batch Enable
        target = e3d_plugin.set_all_plugins_enabled(True, local_projects_dir=self.projects_dir, plugins_dir=self.plugins_dir)
        self.assertEqual(len(target), 2)
        self.assertEqual(len(e3d_plugin.read_enabled_plugins(self.projects_dir)), 2)

        # Generate Hotload Macro
        macro_path = os.path.join(self.plugins_dir, 'load_all.mac')
        p, content = e3d_plugin.generate_hotload_macro(
            plugins_dir=self.plugins_dir, local_projects_dir=self.projects_dir, output_path=macro_path
        )
        self.assertTrue(os.path.isfile(macro_path))
        self.assertIn("pml index '", content)
        self.assertIn("pml rehash all", content)

        # Batch Disable
        e3d_plugin.set_all_plugins_enabled(False, local_projects_dir=self.projects_dir, plugins_dir=self.plugins_dir)
        self.assertEqual(len(e3d_plugin.read_enabled_plugins(self.projects_dir)), 0)


if __name__ == '__main__':
    unittest.main()
