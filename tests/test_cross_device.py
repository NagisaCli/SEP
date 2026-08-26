#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
跨设备能力与数据持久性单元测试
"""

import json
import os
import shutil
import sys
import tempfile
import uuid
import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import e3d_util as util
import e3d_store as store
import e3d_plugin as plugin


@pytest.fixture
def temp_env():
    td = tempfile.mkdtemp(prefix="sep_test_cross_dev_")
    old_appdata = os.environ.get("APPDATA")
    os.environ["APPDATA"] = td
    yield td
    if old_appdata is not None:
        os.environ["APPDATA"] = old_appdata
    else:
        os.environ.pop("APPDATA", None)
    shutil.rmtree(td, ignore_errors=True)


class TestCrossDeviceResilience:

    def test_get_available_drives(self):
        drives = util.get_available_drives()
        assert isinstance(drives, list)
        assert len(drives) > 0
        assert "C:" in drives or "/" in drives

    def test_resolve_cross_device_path_existing(self, temp_env):
        real_dir = os.path.join(temp_env, "existing_folder")
        os.makedirs(real_dir, exist_ok=True)
        res, created, notice = util.resolve_cross_device_path([r"Z:\NonExistent", real_dir])
        assert res.lower() == util.normalize_path(real_dir).lower()
        assert not created

    def test_resolve_cross_device_path_auto_creates(self, temp_env):
        sub = os.path.join(f"TestAutoCreate_{uuid.uuid4().hex[:8]}", "SubFolder")
        res, created, notice = util.resolve_cross_device_path([r"X:\Fake1", r"Y:\Fake2"], sub_path=sub)
        try:
            assert os.path.isdir(res)
            assert created
            assert "自动" in (notice or "")
        finally:
            top_dir = os.path.dirname(res)
            shutil.rmtree(top_dir, ignore_errors=True)

    def test_rotate_file_backups(self, temp_env):
        cfg_file = os.path.join(temp_env, "test_config.json")
        with open(cfg_file, "w", encoding="utf-8") as f:
            f.write("v1")
        util.rotate_file_backups(cfg_file, max_backups=3)
        assert os.path.isfile(cfg_file + ".bak")

        with open(cfg_file, "w", encoding="utf-8") as f:
            f.write("v2")
        util.rotate_file_backups(cfg_file, max_backups=3)
        assert os.path.isfile(cfg_file + ".bak.1")

    def test_device_notifications(self, temp_env):
        notif = store.add_device_notification("info", "测试通知", "测试详情消息", action_label="查看", action_url="/test")
        assert notif["title"] == "测试通知"
        all_notifs = store.get_device_notifications()
        assert any(n["id"] == notif["id"] for n in all_notifs)

        store.dismiss_device_notification(notif["id"])
        active_notifs = store.get_device_notifications(only_active=True)
        assert not any(n["id"] == notif["id"] for n in active_notifs)

    def test_export_and_import_config_bundle(self, temp_env):
        data = store.load_data()
        data["my_projects"].append({
            "id": "proj_demo",
            "name": "Demo Project",
            "bat_path": r"D:\AVEVA\Projects\E3D3.1\demo.bat",
            "lib_id": None,
            "source": "manual",
            "added_at": util.now_iso()
        })
        store.save_data(data)

        # 导出
        bundle = store.export_config_bundle()
        assert bundle["sep_bundle_version"] == 1
        assert len(bundle["data"]["my_projects"]) == 1

        # 模拟导入到另一环境
        bundle_str = json.dumps(bundle)
        import_res = store.import_config_bundle(bundle_str, remap_drives=True)
        assert import_res["ok"] is True
        assert import_res["projects_count"] == 1
