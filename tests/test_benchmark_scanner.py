#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 极速扫描引擎与多库并发重扫单元测试
"""

import os
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_scanner as scanner
import e3d_util as util


class TestFastScannerEngine(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base_dir = self.tmp.name

    def test_fast_scan_collection_with_many_subdirs(self):
        """测试包含大量子文件夹的项目库极速扫描与准确性。"""
        lib_dir = os.path.join(self.base_dir, "LargeLibrary")
        os.makedirs(lib_dir, exist_ok=True)

        # 创建 30 个项目子文件夹和 10 个非项目干扰文件夹
        for i in range(30):
            p_dir = os.path.join(lib_dir, f"Project_{i:03d}")
            os.makedirs(p_dir, exist_ok=True)
            bat_file = os.path.join(p_dir, f"evarsP{i:02d}.bat")
            with open(bat_file, "w", encoding="utf-8") as f:
                f.write("@echo off\r\n")

        for i in range(10):
            noise_dir = os.path.join(lib_dir, f"Noise_Folder_{i}")
            os.makedirs(noise_dir, exist_ok=True)
            with open(os.path.join(noise_dir, "readme.txt"), "w", encoding="utf-8") as f:
                f.write("not a project")

        t0 = time.perf_counter()
        projects, info = scanner.scan_library(lib_dir)
        elapsed = time.perf_counter() - t0

        self.assertEqual(len(projects), 30)
        self.assertEqual(info["kind"], "collection")
        # 验证极速性能（30 个项目本地扫描时间在毫秒级完成）
        self.assertLess(elapsed, 1.0)

    def test_rescan_libraries_parallel(self):
        """测试多路径库并发重扫函数。"""
        libs = []
        for lib_idx in range(5):
            lib_dir = os.path.join(self.base_dir, f"Lib_{lib_idx}")
            os.makedirs(lib_dir, exist_ok=True)
            for p_idx in range(5):
                p_dir = os.path.join(lib_dir, f"Proj_{p_idx}")
                os.makedirs(p_dir, exist_ok=True)
                with open(os.path.join(p_dir, f"evarsL{lib_idx}P{p_idx}.bat"), "w", encoding="utf-8") as f:
                    f.write("@echo off\r\n")

            lib_obj = {
                "id": f"lib_{lib_idx}",
                "name": f"Library {lib_idx}",
                "path": lib_dir,
                "type": "collection",
            }
            libs.append(lib_obj)

        t0 = time.perf_counter()
        results = scanner.rescan_libraries_parallel(libs, timeout=5)
        elapsed = time.perf_counter() - t0

        self.assertEqual(len(results), 5)
        for projs, lib in results:
            self.assertEqual(len(projs), 5)
            self.assertIsNone(lib.get("last_error"))
            self.assertIsNotNone(lib.get("last_scan"))
        self.assertLess(elapsed, 1.0)


if __name__ == "__main__":
    unittest.main()
