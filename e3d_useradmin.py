#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
e3d_useradmin.py — SEP AVEVA E3D 用户/团队管理桥接模块
======================================================
通过调用 e3d-admin.exe (--format json) 子进程，以机器可读方式
操作 AVEVA Administration 中的项目用户与团队，供 SEP Web API 调用。

不存储或记录密码明文到日志。
凭证保存在 sep_admin_creds.json（本地明文，仅本机访问）。
"""

import json
import logging
import os
import re
import subprocess
import sys
import time

import e3d_util as util

# ── 路径 ──────────────────────────────────────────────────────────────────────

def _script_dir() -> str:
    """返回本脚本所在目录（SEP 工作目录）。"""
    return os.path.dirname(os.path.abspath(__file__))


def get_exe_path() -> str:
    """
    定位 e3d-admin.exe。
    查找顺序:
      1. 相对 util.SCRIPT_DIR / _script_dir(): ADMIN/E3dAdmin/bin/publish/e3d-admin.exe
      2. 环境变量 E3D_ADMIN_EXE
    """
    bases = [util.SCRIPT_DIR, _script_dir()]
    for b in bases:
        if not b:
            continue
        candidate = os.path.join(b, "ADMIN", "E3dAdmin", "bin", "publish", "e3d-admin.exe")
        if os.path.isfile(candidate):
            return candidate

    env_path = os.environ.get("E3D_ADMIN_EXE", "")
    if env_path and os.path.isfile(env_path):
        return env_path

    raise FileNotFoundError(
        "e3d-admin.exe not found. Searched in:\n"
        + "\n".join(f"  - {os.path.join(b, 'ADMIN', 'E3dAdmin', 'bin', 'publish', 'e3d-admin.exe')}" for b in bases)
        + "\nPlease publish the E3dAdmin project first, or set E3D_ADMIN_EXE."
    )



def _creds_path() -> str:
    """凭证 JSON 文件路径（与 SEP 数据目录同级）。"""
    return os.path.join(util.get_user_data_dir(), "sep_admin_creds.json")


def _user_cache_path() -> str:
    """用户与团队持久化缓存 JSON 文件路径。"""
    return os.path.join(util.get_user_data_dir(), "sep_user_cache.json")


# ── 工程代码与环境变量解析 ────────────────────────────────────────────────────

def parse_bat_evars(bat_path: str, lib_path: str = "") -> tuple[str | None, dict[str, str]]:
    """从项目的 evars*.bat 文件中解析出环境变量与标准工程代码。"""
    if not bat_path or not os.path.exists(bat_path):
        return None, {}
    evars = {}
    code = None
    proj_dir = os.path.dirname(bat_path)
    base_dir = lib_path or os.path.dirname(proj_dir)
    try:
        with open(bat_path, "r", errors="ignore") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("rem") or line.startswith("::"):
                    continue
                m = re.match(r"^set\s+([A-Za-z0-9_]+)\s*=\s*(.*)$", line, re.IGNORECASE)
                if m:
                    k, v = m.group(1).strip(), m.group(2).strip()
                    base_clean = base_dir.rstrip("\\/")
                    proj_clean = proj_dir.rstrip("\\/")
                    for token in ["%projects_dir%\\", "%projects_dir%/", "%projects_dir%"]:
                        if token.lower() in v.lower():
                            idx = v.lower().find(token.lower())
                            while idx != -1:
                                repl = (base_clean + "\\") if token.endswith(("\\", "/")) else base_clean
                                v = v[:idx] + repl + v[idx + len(token):]
                                idx = v.lower().find(token.lower(), idx + len(repl))
                    for token in ["%~dp0\\", "%~dp0/", "%~dp0"]:
                        if token.lower() in v.lower():
                            idx = v.lower().find(token.lower())
                            while idx != -1:
                                repl = (proj_clean + "\\") if token.endswith(("\\", "/")) else proj_clean
                                v = v[:idx] + repl + v[idx + len(token):]
                                idx = v.lower().find(token.lower(), idx + len(repl))
                    evars[k] = v
                    m_code = re.match(r"^([A-Za-z0-9]{2,5})000$", k, re.IGNORECASE)
                    if m_code:
                        code = m_code.group(1).upper()
    except Exception:
        pass
    return code, evars


def resolve_project_context(project_identifier: str) -> tuple[str, dict[str, str]]:
    """
    根据传入的项目代号、名称或路径，解析出：
    1. 权威的 AVEVA 2-5 字符工程代码（如 APS, FHG, SST, FOX, DPH）
    2. 项目专属的环境变量字典（从 evars*.bat 读取）
    """
    raw_code = (project_identifier or "").strip()
    if not raw_code:
        return "", {}

    try:
        import e3d_store
        data = e3d_store.load_data()
        all_projs = data.get("all_projects_cache", []) + data.get("my_projects", [])
    except Exception:
        all_projs = []

    matched_proj = None
    # 1. 优先按 id 或名称精准匹配
    for p in all_projs:
        if p.get("id") == raw_code or p.get("name", "").upper() == raw_code.upper():
            matched_proj = p
            break

    # 2. 尝试按 bat_path 文件名匹配
    if not matched_proj:
        for p in all_projs:
            bat = p.get("bat_path", "")
            base = os.path.splitext(os.path.basename(bat))[0].upper()
            if base.replace("EVARS", "") == raw_code.upper() or base == raw_code.upper():
                matched_proj = p
                break

    evars = {}
    canonical_code = None

    if matched_proj:
        bat_path = matched_proj.get("bat_path", "")
        lib_path = matched_proj.get("lib_path", "")
        if bat_path and os.path.exists(bat_path):
            code_found, parsed_evars = parse_bat_evars(bat_path, lib_path)
            if code_found:
                canonical_code = code_found
            evars.update(parsed_evars)

    # 3. 若仍未匹配，遍历项目中是否已有某工程的标准代码与 raw_code 匹配
    if not canonical_code:
        for p in all_projs:
            bat = p.get("bat_path", "")
            if bat and os.path.exists(bat):
                code_found, parsed_evars = parse_bat_evars(bat, p.get("lib_path", ""))
                if code_found and code_found.upper() == raw_code.upper():
                    canonical_code = code_found
                    evars.update(parsed_evars)
                    break

    # 4. 兜底内置映射
    if not canonical_code:
        known = {
            "AVEVAPLANTSAMPLE": "APS",
            "AVEVACATALOGUE": "ACP",
            "AVEVAMARINESAMPLE": "AMS",
        }
        if raw_code.upper() in known:
            canonical_code = known[raw_code.upper()]

    final_code = (canonical_code or raw_code).upper()
    return final_code, evars


# ── 用户缓存管理 ──────────────────────────────────────────────────────────────

def get_all_cached_users() -> dict:
    """读取全部项目的已缓存用户数据字典 {PROJECT: {"users": [...], "updated_at": "..."}}"""
    path = _user_cache_path()
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def get_cached_users(project_code: str) -> list[dict] | None:
    """读取指定项目的已缓存用户列表（若无缓存返回 None）。"""
    data = get_all_cached_users()
    code = project_code.upper()
    item = data.get(code)
    if item and isinstance(item, dict) and "users" in item:
        return item["users"]
    canon, _ = resolve_project_context(code)
    if canon and canon != code:
        item = data.get(canon)
        if item and isinstance(item, dict) and "users" in item:
            return item["users"]
    return None


def set_cached_users(project_code: str, users: list[dict]) -> None:
    """更新指定项目的用户缓存。"""
    path = _user_cache_path()
    data = get_all_cached_users()
    code = project_code.upper()
    payload = {
        "users": users,
        "count": len(users),
        "updated_at": util.now_iso(),
    }
    data[code] = payload
    canon, _ = resolve_project_context(code)
    if canon and canon != code:
        data[canon] = payload
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def invalidate_cached_users(project_code: str) -> None:
    """清除指定项目的用户缓存。"""
    path = _user_cache_path()
    data = get_all_cached_users()
    code = project_code.upper()
    changed = False
    if code in data:
        del data[code]
        changed = True
    canon, _ = resolve_project_context(code)
    if canon and canon in data:
        del data[canon]
        changed = True
    if changed:
        try:
            with open(path, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception:
            pass


# ── 凭证管理 ──────────────────────────────────────────────────────────────────


def load_creds(project_code: str) -> dict:
    """
    加载指定项目的管理凭证。
    返回 {"admin_user": str, "admin_password": str} 或 {}（未配置）。
    """
    path = _creds_path()
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        code = project_code.upper()
        if code in data:
            return data[code]
        canon, _ = resolve_project_context(code)
        if canon and canon != code and canon in data:
            return data[canon]
        return {}
    except Exception:
        return {}


def save_creds(project_code: str, admin_user: str, admin_password: str) -> None:
    """
    保存项目管理凭证到 sep_admin_creds.json。
    密码明文存储（本机仅限，不记录日志）。
    """
    path = _creds_path()
    data: dict = {}
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            data = {}

    code = project_code.upper()
    data[code] = {
        "admin_user": admin_user,
        "admin_password": admin_password,
    }
    canon, _ = resolve_project_context(code)
    if canon and canon != code:
        data[canon] = {
            "admin_user": admin_user,
            "admin_password": admin_password,
        }

    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def get_saved_admin_user(project_code: str) -> str | None:
    """返回已保存的管理员用户名（不返回密码）。"""
    creds = load_creds(project_code)
    return creds.get("admin_user")


# ── 日志 ──────────────────────────────────────────────────────────────────────

def _get_logger() -> logging.Logger:
    log_dir = util.get_user_data_dir()
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.join(log_dir, "sep_useradmin.log")

    logger = logging.getLogger("sep_useradmin")
    if not logger.handlers:
        handler = logging.FileHandler(log_path, encoding="utf-8")
        handler.setFormatter(logging.Formatter(
            "%(asctime)s [%(levelname)s] %(message)s",
            datefmt="%Y-%m-%d %H:%M:%S"
        ))
        logger.addHandler(handler)
        logger.setLevel(logging.INFO)
    return logger


# ── 核心子进程调用 ────────────────────────────────────────────────────────────

def _run(project_code: str, cmd_args: list[str],
         admin_user: str | None = None, admin_password: str | None = None,
         timeout: int = 60) -> dict:
    """
    调用 e3d-admin.exe ... --format json，返回解析后的 dict。
    cmd_args 示例: ["user", "list", "APS"]
    如果 admin_user/password 未提供，从 sep_admin_creds.json 读取。
    不记录 admin_password 到日志。
    """
    logger = _get_logger()
    exe = get_exe_path()

    canonical_code, proj_evars = resolve_project_context(project_code)
    actual_code = canonical_code or project_code.upper()

    # 解析凭证（优先查询规范代码，回退原始代码）
    if not admin_user or not admin_password:
        creds = load_creds(actual_code)
        if not creds and actual_code != project_code.upper():
            creds = load_creds(project_code)
        admin_user = admin_user or creds.get("admin_user", "SYSTEM")
        admin_password = admin_password or creds.get("admin_password", "XXXXXX")

    # 替换 cmd_args 中的工程名称为规范代码
    actual_cmd_args = []
    for arg in cmd_args:
        if arg.upper() == project_code.upper() or (actual_code and arg.upper() == actual_code):
            actual_cmd_args.append(actual_code)
        else:
            actual_cmd_args.append(arg)

    full_args = [
        exe,
        "--admin-user", admin_user,
        "--admin-pass", admin_password,
        "--format", "json",
    ] + actual_cmd_args

    # 日志记录（隐藏密码）
    safe_args = [
        exe,
        "--admin-user", admin_user,
        "--admin-pass", "***",
        "--format", "json",
    ] + actual_cmd_args
    logger.info("RUN (code=%s, evars=%d): %s", actual_code, len(proj_evars), " ".join(safe_args))

    env = os.environ.copy()
    if proj_evars:
        env.update(proj_evars)

    t0 = time.monotonic()
    try:
        result = subprocess.run(
            full_args,
            env=env,
            capture_output=True,
            text=True,
            timeout=timeout,
            creationflags=0x08000000 if sys.platform == "win32" else 0,
        )
    except subprocess.TimeoutExpired:
        logger.error("TIMEOUT after %ds for project=%s (actual=%s)", timeout, project_code, actual_code)
        return {"ok": False, "error": f"e3d-admin timed out after {timeout}s"}
    except FileNotFoundError as e:
        logger.error("EXE NOT FOUND: %s", e)
        return {"ok": False, "error": str(e)}

    elapsed = round(time.monotonic() - t0, 2)

    stdout = (result.stdout or "").strip()
    stderr = (result.stderr or "").strip()

    logger.info("EXIT=%d elapsed=%.2fs stdout_len=%d", result.returncode, elapsed, len(stdout))
    if stderr:
        logger.warning("STDERR: %s", stderr[:500])

    # Parse JSON from stdout
    if stdout:
        # Find last line that looks like JSON (handles extra AVEVA noise before/after)
        for line in reversed(stdout.splitlines()):
            line = line.strip()
            if line.startswith("{"):
                try:
                    parsed = json.loads(line)
                    if not parsed.get("ok", True) and result.returncode != 0:
                        logger.error("AVEVA error: %s", parsed.get("error", ""))
                    return parsed
                except json.JSONDecodeError:
                    continue

    # No parseable JSON found — build error from stderr or exit code
    err_msg = stderr or f"e3d-admin exited with code {result.returncode}"
    logger.error("No JSON in output. err=%s", err_msg[:300])
    return {"ok": False, "error": err_msg}


# ── 公开 API ──────────────────────────────────────────────────────────────────

def list_users(project_code: str,
               admin_user: str | None = None,
               admin_password: str | None = None,
               force_refresh: bool = False) -> list[dict]:
    """
    列出项目用户。
    :param force_refresh: 为 False 时优先从本地缓存秒级读取；为 True 时强制调用 AVEVA 重新扫描。
    返回: [{"name": str, "security": str, "description": str, "teams": [str]}]
    """
    code = project_code.upper()
    if not force_refresh:
        cached = get_cached_users(code)
        if cached is not None:
            return cached

    result = _run(code, ["user", "list", code],
                  admin_user=admin_user, admin_password=admin_password)
    if not result.get("ok"):
        raise RuntimeError(result.get("error", "Failed to list users"))

    users = result.get("data", [])
    set_cached_users(code, users)
    return users


def list_teams(project_code: str,
               admin_user: str | None = None,
               admin_password: str | None = None) -> list[dict]:
    """
    列出项目团队。
    返回: [{"name": str, "member_count": int, "users": [str]}]
    """
    code = project_code.upper()
    result = _run(code, ["team", "list", code],
                  admin_user=admin_user, admin_password=admin_password)
    if not result.get("ok"):
        raise RuntimeError(result.get("error", "Failed to list teams"))
    return result.get("data", [])


def add_user(project_code: str,
             username: str,
             team: str,
             security: str = "General",
             description: str | None = None,
             password: str | None = None,
             admin_user: str | None = None,
             admin_password: str | None = None) -> dict:
    """
    新建用户并加入初始团队。
    返回: {"ok": bool, "message": str}
    """
    code = project_code.upper()
    u_name = username.upper()
    cmd = ["user", "add", code, u_name,
           "--team", team, "--security", security]
    if description:
        cmd += ["--desc", description]
    if password:
        cmd += ["--password", password]

    result = _run(code, cmd, admin_user=admin_user, admin_password=admin_password)
    if not result.get("ok"):
        raise RuntimeError(result.get("error", "Failed to add user"))

    # 同步增量更新本地缓存
    cached = get_cached_users(code)
    if cached is not None:
        clean_team = team.lstrip('*')
        full_team = f"*{clean_team}"
        # 避免重复
        updated = [u for u in cached if u.get("name") != u_name]
        updated.append({
            "name": u_name,
            "security": security,
            "description": description or "",
            "teams": [full_team]
        })
        updated.sort(key=lambda x: x.get("name", ""))
        set_cached_users(code, updated)

    return {"ok": True, "message": result.get("message", f"User '{username}' added.")}


def delete_user(project_code: str,
                username: str,
                admin_user: str | None = None,
                admin_password: str | None = None) -> dict:
    """
    删除用户（内置保护：不能删 SYSTEM，不能删最后一个 FREE 用户）。
    返回: {"ok": bool, "message": str}
    """
    code = project_code.upper()
    u_name = username.upper()
    result = _run(code,
                  ["user", "delete", code, u_name],
                  admin_user=admin_user, admin_password=admin_password)
    if not result.get("ok"):
        raise RuntimeError(result.get("error", "Failed to delete user"))

    # 同步增量从缓存中剔除
    cached = get_cached_users(code)
    if cached is not None:
        updated = [u for u in cached if u.get("name") != u_name]
        set_cached_users(code, updated)

    return {"ok": True, "message": result.get("message", f"User '{username}' deleted.")}


def add_user_to_team(project_code: str,
                     team: str,
                     username: str,
                     admin_user: str | None = None,
                     admin_password: str | None = None) -> dict:
    """
    将用户加入团队。
    返回: {"ok": bool, "message": str}
    """
    code = project_code.upper()
    u_name = username.upper()
    result = _run(code,
                  ["team", "add-user", code, team, u_name],
                  admin_user=admin_user, admin_password=admin_password)
    if not result.get("ok"):
        raise RuntimeError(result.get("error", "Failed to add user to team"))

    # 同步增量将团队加入缓存
    cached = get_cached_users(code)
    if cached is not None:
        clean_team = team.lstrip('*')
        full_team = f"*{clean_team}"
        for u in cached:
            if u.get("name") == u_name:
                teams = list(u.get("teams") or [])
                if full_team not in teams and clean_team not in teams:
                    teams.append(full_team)
                    u["teams"] = teams
                break
        set_cached_users(code, cached)

    return {"ok": True, "message": result.get("message", f"User '{username}' added to team '{team}'.")}

