#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 用户与团队管理模块自动化测试
==================================
测试 e3d_useradmin 桥接功能及 e3d_web 对应的 7 条 API 路由。
"""

import json
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_useradmin
import e3d_web


class TestUserAdminModule(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        # 隔离凭证与缓存文件路径
        self.orig_creds_path = e3d_useradmin._creds_path
        self.orig_cache_path = e3d_useradmin._user_cache_path
        e3d_useradmin._creds_path = lambda: os.path.join(self.tmp.name, "sep_admin_creds.json")
        e3d_useradmin._user_cache_path = lambda: os.path.join(self.tmp.name, "sep_user_cache.json")

    def tearDown(self):
        e3d_useradmin._creds_path = self.orig_creds_path
        e3d_useradmin._user_cache_path = self.orig_cache_path


    def test_creds_save_and_load(self):
        """测试凭证保存与读取"""
        e3d_useradmin.save_creds("APS", "SYSTEM", "SECRET123")
        creds = e3d_useradmin.load_creds("APS")
        self.assertEqual(creds.get("admin_user"), "SYSTEM")
        self.assertEqual(creds.get("admin_password"), "SECRET123")

        # 读取已保存用户名（不应泄露密码接口）
        user = e3d_useradmin.get_saved_admin_user("APS")
        self.assertEqual(user, "SYSTEM")

        # 未配置项目返回 None
        self.assertIsNone(e3d_useradmin.get_saved_admin_user("NONEXISTENT"))

    def test_parse_bat_evars(self):
        """测试从 evars*.bat 解析标准代码与环境变量"""
        bat_file = os.path.join(self.tmp.name, "evarsSAMPLE.bat")
        with open(bat_file, "w", encoding="utf-8") as f:
            f.write(
                "set SAM000=%projects_dir%\\SAMPLE\\sam000\n"
                "set SAMDFLTS=%~dp0dflts\n"
                "set SAM000ID=SAMPLE\n"
            )
        code, evars = e3d_useradmin.parse_bat_evars(bat_file, lib_path="C:\\AVEVA\\Projects")
        self.assertEqual(code, "SAM")
        self.assertEqual(evars.get("SAM000"), "C:\\AVEVA\\Projects\\SAMPLE\\sam000")
        self.assertTrue(evars.get("SAMDFLTS", "").endswith("dflts"))

    def test_resolve_project_context_fallback(self):
        """测试未知项目回退规范代码与内置映射"""
        code, evars = e3d_useradmin.resolve_project_context("AvevaPlantSample")
        self.assertEqual(code, "APS")
        code2, _ = e3d_useradmin.resolve_project_context("XYZ")
        self.assertEqual(code2, "XYZ")

    @mock.patch("e3d_useradmin._run")
    def test_list_users(self, mock_run):
        mock_run.return_value = {
            "ok": True,
            "data": [
                {"name": "SYSTEM", "security": "Free", "description": "", "teams": ["*MASTER"]},
                {"name": "USER1", "security": "General", "description": "Test", "teams": ["*PIPING"]},
            ]
        }
        users = e3d_useradmin.list_users("APS")
        self.assertEqual(len(users), 2)
        self.assertEqual(users[0]["name"], "SYSTEM")
        mock_run.assert_called_once_with("APS", ["user", "list", "APS"], admin_user=None, admin_password=None)

    @mock.patch("e3d_useradmin._run")
    def test_list_users_cached_and_force_refresh(self, mock_run):
        """测试用户缓存生效与 force_refresh 穿透"""
        mock_run.return_value = {
            "ok": True,
            "data": [
                {"name": "SYSTEM", "security": "Free", "description": "", "teams": ["*MASTER"]},
            ]
        }
        # 第一次调用：缓存未命中，调用 _run
        u1 = e3d_useradmin.list_users("APS")
        self.assertEqual(len(u1), 1)
        self.assertEqual(mock_run.call_count, 1)

        # 第二次调用（无 force_refresh）：缓存命中，不调用 _run
        u2 = e3d_useradmin.list_users("APS", force_refresh=False)
        self.assertEqual(len(u2), 1)
        self.assertEqual(mock_run.call_count, 1)  # 仍然是 1！

        # 第三次调用（force_refresh=True）：强制穿透，再次调用 _run
        u3 = e3d_useradmin.list_users("APS", force_refresh=True)
        self.assertEqual(len(u3), 1)
        self.assertEqual(mock_run.call_count, 2)  # 变为 2！


    @mock.patch("e3d_useradmin._run")
    def test_list_teams(self, mock_run):
        mock_run.return_value = {
            "ok": True,
            "data": [
                {"name": "*MASTER", "member_count": 1, "users": ["SYSTEM"]},
                {"name": "*PIPING", "member_count": 1, "users": ["USER1"]},
            ]
        }
        teams = e3d_useradmin.list_teams("APS")
        self.assertEqual(len(teams), 2)
        self.assertEqual(teams[0]["name"], "*MASTER")

    @mock.patch("e3d_useradmin._run")
    def test_add_user(self, mock_run):
        mock_run.return_value = {"ok": True, "message": "User 'NEWUSER' created"}
        res = e3d_useradmin.add_user("APS", "NEWUSER", "*MASTER", security="General", description="Desc")
        self.assertTrue(res["ok"])
        mock_run.assert_called_once()
        args = mock_run.call_args[0][1]
        self.assertIn("NEWUSER", args)
        self.assertIn("--team", args)
        self.assertIn("*MASTER", args)

    @mock.patch("e3d_useradmin._run")
    def test_delete_user(self, mock_run):
        mock_run.return_value = {"ok": True, "message": "User 'TESTUSER' deleted"}
        res = e3d_useradmin.delete_user("APS", "TESTUSER")
        self.assertTrue(res["ok"])
        mock_run.assert_called_once_with("APS", ["user", "delete", "APS", "TESTUSER"], admin_user=None, admin_password=None)

    @mock.patch("e3d_useradmin._run")
    def test_add_user_to_team(self, mock_run):
        mock_run.return_value = {"ok": True, "message": "User added"}
        res = e3d_useradmin.add_user_to_team("APS", "*PIPING", "USER1")
        self.assertTrue(res["ok"])
        mock_run.assert_called_once_with("APS", ["team", "add-user", "APS", "*PIPING", "USER1"], admin_user=None, admin_password=None)


class TestUserAdminWebHandlers(unittest.TestCase):
    """测试 e3d_web 中的 WebHandler 路由处理"""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.orig_creds_path = e3d_useradmin._creds_path
        self.orig_cache_path = e3d_useradmin._user_cache_path
        e3d_useradmin._creds_path = lambda: os.path.join(self.tmp.name, "sep_admin_creds.json")
        e3d_useradmin._user_cache_path = lambda: os.path.join(self.tmp.name, "sep_user_cache.json")

    def tearDown(self):
        e3d_useradmin._creds_path = self.orig_creds_path
        e3d_useradmin._user_cache_path = self.orig_cache_path


    def _create_mock_handler(self):
        handler = mock.MagicMock(spec=e3d_web._WebHandler)
        sent_data = {}

        def fake_send_json(data, status=200):
            sent_data["body"] = data
            sent_data["status"] = status

        handler._send_json = fake_send_json
        return handler, sent_data

    def test_admin_config_get_empty(self):
        handler, sent = self._create_mock_handler()
        e3d_web._WebHandler._handle_admin_config_get(handler, {"project": "APS"})
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])
        self.assertEqual(sent["body"]["admin_user"], "")

    def test_admin_config_set_and_get(self):
        handler, sent = self._create_mock_handler()
        # Set
        e3d_web._WebHandler._handle_admin_config_set(handler, {
            "project": "APS",
            "admin_user": "SYSTEM",
            "admin_password": "MYPASSWORD"
        })
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])
        self.assertEqual(sent["body"]["admin_user"], "SYSTEM")
        self.assertNotIn("admin_password", sent["body"])  # 密码不外显

        # Get
        e3d_web._WebHandler._handle_admin_config_get(handler, {"project": "APS"})
        self.assertEqual(sent["body"]["admin_user"], "SYSTEM")
        self.assertNotIn("admin_password", sent["body"])

    @mock.patch("e3d_useradmin.list_users")
    def test_admin_users_list_handler(self, mock_list):
        mock_list.return_value = [{"name": "SYSTEM", "security": "Free", "description": "", "teams": []}]
        handler, sent = self._create_mock_handler()
        e3d_web._WebHandler._handle_admin_users_list(handler, {"project": "APS"})
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])
        self.assertEqual(len(sent["body"]["users"]), 1)

    @mock.patch("e3d_useradmin.add_user")
    def test_admin_users_add_handler(self, mock_add):
        mock_add.return_value = {"ok": True, "message": "created"}
        handler, sent = self._create_mock_handler()
        e3d_web._WebHandler._handle_admin_users_add(handler, {
            "project": "APS",
            "username": "NEWUSER",
            "team": "*MASTER",
            "security": "General"
        })
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])

    @mock.patch("e3d_useradmin.delete_user")
    def test_admin_users_delete_handler(self, mock_del):
        mock_del.return_value = {"ok": True, "message": "deleted"}
        handler, sent = self._create_mock_handler()
        e3d_web._WebHandler._handle_admin_users_delete(handler, {
            "project": "APS",
            "username": "TESTUSER"
        })
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])

    @mock.patch("e3d_useradmin.list_teams")
    def test_admin_teams_list_handler(self, mock_list):
        mock_list.return_value = [{"name": "*MASTER", "member_count": 1, "users": ["SYSTEM"]}]
        handler, sent = self._create_mock_handler()
        e3d_web._WebHandler._handle_admin_teams_list(handler, {"project": "APS"})
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])
        self.assertEqual(len(sent["body"]["teams"]), 1)

    @mock.patch("e3d_useradmin.add_user_to_team")
    def test_admin_teams_add_user_handler(self, mock_add):
        mock_add.return_value = {"ok": True, "message": "added"}
        handler, sent = self._create_mock_handler()
        e3d_web._WebHandler._handle_admin_teams_add_user(handler, {
            "project": "APS",
            "team": "*CABLE",
            "username": "NEWUSER"
        })
        self.assertEqual(sent["status"], 200)
        self.assertTrue(sent["body"]["ok"])


if __name__ == "__main__":
    unittest.main()
