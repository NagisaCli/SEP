#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 项目实时连接数与在线协同会话追踪模块单元测试
"""

import json
import os
import shutil
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_session as session
import e3d_util as util


class TestSessionTracker(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base_dir = self.tmp.name

    def test_zero_window_process_enumeration(self):
        """测试内存级 Windows 原生进程枚举，不抛异常且正确返回进程集合。"""
        procs = session.get_running_process_names()
        self.assertIsInstance(procs, set)
        running = session.is_local_e3d_running()
        self.assertIsInstance(running, bool)

    def test_resolve_project_code_and_db_dir_standard(self):
        """测试标准 evars<CODE>.bat 及 <CODE>000 目录识别。"""
        proj_dir = os.path.join(self.base_dir, "Project_Fox_Phase1")
        os.makedirs(os.path.join(proj_dir, "FOX000"), exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsFOX.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        code, db_dir, root_dir = session.resolve_project_code_and_db_dir(bat_path)
        self.assertEqual(code, "FOX")
        self.assertIsNotNone(db_dir)
        self.assertTrue(os.path.isdir(db_dir))
        self.assertTrue(db_dir.endswith("FOX000"))

    def test_resolve_project_code_and_db_dir_from_bat_content(self):
        """测试从 bat 脚本内部的 set CODE000= 语句精准提取数据库目录。"""
        proj_dir = os.path.join(self.base_dir, "CustomProject")
        custom_000 = os.path.join(proj_dir, "Custom_DB_Dir_000")
        os.makedirs(custom_000, exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsSAM.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write(f"@echo off\r\nset SAM000={custom_000}\r\n")

        code, db_dir, root_dir = session.resolve_project_code_and_db_dir(bat_path)
        self.assertEqual(code, "SAM")
        self.assertEqual(util.normalize_path(db_dir).lower(), util.normalize_path(custom_000).lower())

    def test_resolve_project_code_fuzzy_000(self):
        """测试当文件夹以 000 结尾时的自动匹配。"""
        proj_dir = os.path.join(self.base_dir, "MyPlant")
        os.makedirs(os.path.join(proj_dir, "000"), exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsFPS.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        code, db_dir, root_dir = session.resolve_project_code_and_db_dir(bat_path)
        self.assertEqual(code, "FPS")
        self.assertIsNotNone(db_dir)
        self.assertTrue(db_dir.endswith("000"))

    def test_inspect_project_connections_sep_sessions(self):
        """测试 .sep_sessions 心跳探测与超时清理。"""
        proj_dir = os.path.join(self.base_dir, "ProjA")
        os.makedirs(proj_dir, exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsAAA.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        ses_dir = os.path.join(proj_dir, ".sep_sessions")
        os.makedirs(ses_dir, exist_ok=True)

        # 写入一条有效心跳
        valid_sess = {
            "session_id": "ses_test1",
            "project_id": "p1",
            "project_name": "AAA",
            "computer_name": "ENGINEER-PC-01",
            "user_name": "ZhangSan",
            "last_heartbeat": util.now_iso(),
        }
        with open(os.path.join(ses_dir, "ses_test1.json"), "w", encoding="utf-8") as f:
            json.dump(valid_sess, f)

        # 写入一条过期心跳（1 小时前）
        stale_sess = {
            "session_id": "ses_stale",
            "computer_name": "OLD-PC",
            "user_name": "OldUser",
            "last_heartbeat": "2020-01-01T00:00:00+00:00",
        }
        stale_file = os.path.join(ses_dir, "ses_stale.json")
        with open(stale_file, "w", encoding="utf-8") as f:
            json.dump(stale_sess, f)

        res = session.inspect_project_connections(bat_path)
        self.assertEqual(res["online_count"], 1)
        self.assertEqual(res["sessions"][0]["user_name"], "ZhangSan")
        self.assertIn("sep_heartbeat", res["detection_sources"])
        # 过期文件应被自动清理
        self.assertFalse(os.path.exists(stale_file))

    def test_inspect_project_connections_dabacon_locks(self):
        """测试 DABACON 数据库文件锁 (*.lok, *.lck) 识别。"""
        proj_dir = os.path.join(self.base_dir, "ProjB")
        db_dir = os.path.join(proj_dir, "BBB000")
        os.makedirs(db_dir, exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsBBB.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        # 创建锁文件并写入用户提示
        lock_file = os.path.join(db_dir, "bbbsys.lok")
        with open(lock_file, "wb") as f:
            f.write(b"LOCKED BY LiSi@WORKSTATION-99 FOR MODULE DES")

        res = session.inspect_project_connections(bat_path)
        self.assertTrue(res["has_locks"])
        self.assertIn("bbbsys.lok", res["lock_files"])
        self.assertGreaterEqual(res["online_count"], 1)
        self.assertIn("dabacon_lock", res["detection_sources"])
        self.assertEqual(res["sessions"][0]["user_name"], "LiSi")
        self.assertEqual(res["sessions"][0]["computer_name"], "WORKSTATION-99")

    def test_inspect_project_connections_recent_writes(self):
        """测试 DABACON 数据库文件近期写入活跃度。"""
        proj_dir = os.path.join(self.base_dir, "ProjC")
        db_dir = os.path.join(proj_dir, "CCC000")
        os.makedirs(db_dir, exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsCCC.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        # 创建数据库文件
        db_file = os.path.join(db_dir, "ccc001")
        with open(db_file, "w", encoding="utf-8") as f:
            f.write("data")

        res = session.inspect_project_connections(bat_path)
        self.assertTrue(res["is_active_recently"])
        self.assertIsNotNone(res["last_db_write_time"])
        self.assertIn("db_write", res["detection_sources"])

    def test_multi_source_fusion_deduplication(self):
        """测试多源探测结果针对相同 (computer, user) 的聚合去重与优先级覆盖。"""
        proj_dir = os.path.join(self.base_dir, "ProjD")
        db_dir = os.path.join(proj_dir, "DDD000")
        ses_dir = os.path.join(proj_dir, ".sep_sessions")
        os.makedirs(db_dir, exist_ok=True)
        os.makedirs(ses_dir, exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsDDD.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        # 1. 写入锁文件
        with open(os.path.join(db_dir, "dddsys.lok"), "wb") as f:
            f.write(b"USER WangWu@CAD-PC-02")

        # 2. 同时写入 SEP 心跳（同一计算机与用户）
        sep_sess = {
            "session_id": "ses_d1",
            "computer_name": "CAD-PC-02",
            "user_name": "WangWu",
            "project_name": "DDD",
            "last_heartbeat": util.now_iso(),
        }
        with open(os.path.join(ses_dir, "ses_d1.json"), "w", encoding="utf-8") as f:
            json.dump(sep_sess, f)

        res = session.inspect_project_connections(bat_path)
        # 应去重为 1 个会话，且 source 优先采用 sep
        self.assertEqual(res["online_count"], 1)
        self.assertEqual(res["sessions"][0]["source"], "sep")
        self.assertEqual(res["sessions"][0]["user_name"], "WangWu")

    def test_register_and_unregister_project_session(self):
        """测试项目的会话注册与注销全生命周期。"""
        proj_dir = os.path.join(self.base_dir, "ProjE")
        os.makedirs(proj_dir, exist_ok=True)
        bat_path = os.path.join(proj_dir, "evarsEEE.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write("@echo off\r\n")

        pid = "proj_test_eee"
        sess = session.register_project_session(pid, "EEE", bat_path)
        self.assertIsNotNone(sess)
        self.assertEqual(sess["project_id"], pid)

        # 验证文件已生成
        ses_dir = os.path.join(proj_dir, ".sep_sessions")
        self.assertTrue(os.path.isdir(ses_dir))
        files = [f for f in os.listdir(ses_dir) if f.endswith(".json")]
        self.assertEqual(len(files), 1)

        # 注销会话
        session.unregister_project_session(pid)
        files_after = [f for f in os.listdir(ses_dir) if f.endswith(".json")]
        self.assertEqual(len(files_after), 0)

    def test_batch_inspect_sessions_with_cache(self):
        """测试多项目批量并行探测与 5 秒短时缓存机制。"""
        projects = []
        for code in ["AAA", "BBB", "CCC"]:
            p_dir = os.path.join(self.base_dir, f"Batch_{code}")
            os.makedirs(os.path.join(p_dir, f"{code}000"), exist_ok=True)
            bat = os.path.join(p_dir, f"evars{code}.bat")
            with open(bat, "w", encoding="utf-8") as f:
                f.write("@echo off\r\n")
            projects.append({"id": f"p_{code}", "name": code, "bat_path": bat})

        # 首次查询
        t0 = time.time()
        res1 = session.batch_inspect_sessions(projects, use_cache=False)
        self.assertEqual(len(res1), 3)

        # 二次查询（走缓存）
        res2 = session.batch_inspect_sessions(projects, use_cache=True)
        self.assertEqual(len(res2), 3)
        self.assertEqual(set(res1.keys()), set(res2.keys()))


if __name__ == "__main__":
    unittest.main()
