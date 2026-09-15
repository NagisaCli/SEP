#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
测试 E3D 项目优雅创建与下线归档生命周期模块 (e3d_proj_admin)
"""

import os
import shutil
import tempfile
import unittest
import e3d_proj_admin

class TestProjectAdmin(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = self.tmp.name

    def test_create_and_inspect_and_decommission(self):
        # 1. 测试规范化创建新项目
        res = e3d_proj_admin.create_project(
            code="TST",
            name="单元测试自动化工程",
            root_dir=self.root,
            register_e3d=False
        )
        self.assertTrue(res["ok"])
        proj_dir = res["path"]
        self.assertTrue(os.path.isdir(proj_dir))
        self.assertTrue(os.path.isdir(os.path.join(proj_dir, "tst000")))
        self.assertTrue(os.path.isdir(os.path.join(proj_dir, "tstiso")))
        self.assertTrue(os.path.isfile(os.path.join(proj_dir, "evarsTST.bat")))

        # 2. 测试项目体检 (无锁状态)
        info = e3d_proj_admin.inspect_project(proj_dir)
        self.assertEqual(info["code"], "TST")
        self.assertFalse(info["is_locked"])

        # 3. 模拟放置锁文件测试拦截
        lock_file = os.path.join(proj_dir, "tst000", "sys001.lck")
        with open(lock_file, "w") as f:
            f.write("locked")

        info_locked = e3d_proj_admin.inspect_project(proj_dir)
        self.assertTrue(info_locked["is_locked"])
        self.assertEqual(len(info_locked["lock_files"]), 1)

        # 验证默认拦截下线
        archive_dir = os.path.join(self.root, "Archive")
        with self.assertRaises(RuntimeError):
            e3d_proj_admin.decommission_project(proj_dir, archive_dir=archive_dir, force=False)

        # 4. 移除锁文件并正常执行优雅下线
        os.remove(lock_file)
        decomm_res = e3d_proj_admin.decommission_project(
            proj_dir,
            archive_dir=archive_dir,
            do_archive=True,
            do_unregister=False,
            do_delete=True
        )
        self.assertTrue(decomm_res["ok"])
        self.assertFalse(os.path.exists(proj_dir))
        self.assertTrue(os.path.isfile(decomm_res["archived_file"]))

    def test_flexible_code_validation(self):
        # 验证 2~5 位字母数字有效性
        valid_codes = ["P1", "PRJ", "DEMO", "PROJ1"]
        for code in valid_codes:
            res = e3d_proj_admin.create_project(
                code=code,
                name=f"测试项目-{code}",
                root_dir=self.root,
                register_e3d=False
            )
            self.assertTrue(res["ok"])
            self.assertEqual(res["code"], code)
            self.assertTrue(os.path.isdir(res["path"]))

        # 验证非法代码
        invalid_codes = ["", "A", "TOOLONG", "PR J", "AB@"]
        for code in invalid_codes:
            with self.assertRaises(ValueError):
                e3d_proj_admin.create_project(
                    code=code,
                    name="测试",
                    root_dir=self.root,
                    register_e3d=False
                )

if __name__ == "__main__":
    unittest.main()
