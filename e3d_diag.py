#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 诊断与修复模块
==================
对路径库 / 项目路径做基础健康检查：

- 本地路径：存在性、可读性、可写性、evars 项目文件；
- 网络（SMB/UNC）路径：主机解析、SMB 445 端口、共享可访问性、
  来宾登录策略（AllowInsecureGuestAuth）、SMB 客户端服务状态。

每个检查项返回 {id, name, status, detail, fix}；status 为 ok / warn / fail / skip。
fix 为 None 或 {id, title, steps, commands, requires_admin}，UI 可展示修复步骤，
也可通过 apply_fix() 尝试自动执行安全修复。
"""

import os
import re
import socket
import subprocess
import sys

import e3d_util as util


CREATE_NO_WINDOW = 0x08000000 if sys.platform == 'win32' else 0


# Windows 常见网络错误码 -> 可读说明
NET_ERRORS = {
    5: '拒绝访问：没有权限，或服务器禁用了来宾访问。',
    53: '找不到网络路径：主机名 / 共享名不正确，或主机未开机。',
    67: '找不到网络名称：共享名可能不存在。',
    85: '本地设备名已在使用。',
    86: '指定的网络密码不正确：登录凭据有问题。',
    1219: '同一用户已建立多个连接：请先断开旧连接。',
    1231: '无法联系网络位置：网络不可达。',
    1326: '用户名或密码不正确：登录失败。',
    1331: '帐户当前已禁用：常见于来宾（Guest）帐户被禁用。',
    1396: '目标帐户名称不正确：可能需要域前缀或不同凭据。',
}


def _run(cmd, timeout=8):
    """静默执行命令（不弹窗），返回 (returncode, output)。"""
    try:
        r = subprocess.run(
            cmd, capture_output=True, text=True,
            timeout=timeout, creationflags=CREATE_NO_WINDOW,
        )
        return r.returncode, (r.stdout or '') + (r.stderr or '')
    except subprocess.TimeoutExpired:
        return -1, '命令执行超时'
    except Exception as e:
        return -2, str(e)


def _guest_auth_setting():
    """读取本机 SMB 客户端 AllowInsecureGuestAuth 策略。返回 True/False/None。"""
    if sys.platform != 'win32':
        return None
    try:
        import winreg
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            r'SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters',
        )
        try:
            value, _ = winreg.QueryValueEx(key, 'AllowInsecureGuestAuth')
            return int(value) == 1
        finally:
            winreg.CloseKey(key)
    except OSError:
        return None


def _smb_service_status():
    """检查 SMB 客户端服务 LanmanWorkstation 是否在运行。"""
    if sys.platform != 'win32':
        return None
    code, out = _run(['sc', 'query', 'lanmanworkstation'], timeout=6)
    if code == 0 and re.search(r'STATE\s*:\s*\d+\s+RUNNING', out, re.IGNORECASE):
        return 'running'
    if code == 0:
        return 'stopped'
    return 'unknown'


def _net_use_test(share, timeout=8):
    """
    用空凭据尝试连接共享（不会弹出交互式输入框）。
    返回 (returncode, output)。0 表示可匿名/当前凭据访问。
    """
    return _run(['net', 'use', share, '/user:""', '""'], timeout=timeout)


def _net_error_detail(code):
    return NET_ERRORS.get(code, f'系统错误 {code}，请根据提示进一步排查。')


def _share_root_of(norm):
    """从 UNC 路径中取出 \\\\host\\share 根。"""
    parts = norm.lstrip('\\').split('\\')
    if len(parts) >= 2:
        return '\\\\' + '\\'.join(parts[:2])
    return norm


def diagnose(path, timeout=8):
    """对路径执行基础诊断，返回报告 dict。"""
    norm = util.normalize_path(path)
    if not norm:
        return {
            'ok': False,
            'path': '',
            'protocol': 'local',
            'checks': [{
                'id': 'empty',
                'name': '路径',
                'status': 'fail',
                'detail': '路径为空',
                'fix': None,
            }],
            'fixes': [],
        }

    protocol = util.path_protocol(norm)
    if protocol == 'unc':
        return _diagnose_unc(norm, timeout)
    return _diagnose_local(norm, timeout)


def _diagnose_local(norm, timeout=8):
    checks = []

    exists = util.run_with_timeout(lambda: os.path.exists(norm), timeout, False)
    checks.append({
        'id': 'exists',
        'name': '路径存在',
        'status': 'ok' if exists else 'fail',
        'detail': norm if exists else '路径不存在或不可访问',
        'fix': None,
    })

    if not exists:
        return _finish(checks)

    readable = util.run_with_timeout(lambda: os.access(norm, os.R_OK), timeout, False)
    checks.append({
        'id': 'readable',
        'name': '可读取',
        'status': 'ok' if readable else 'fail',
        'detail': '可以读取目录内容' if readable else '没有读取权限',
        'fix': {
            'id': 'local_permission',
            'title': '检查本地目录权限',
            'steps': [
                '右键目录 → 属性 → 安全，确认当前用户拥有“读取和执行”权限。',
                '若为网络磁盘映射，请先确认映射是否过期。',
            ],
            'commands': [f'icacls "{norm}"'],
            'requires_admin': False,
        } if not readable else None,
    })

    writable = util.run_with_timeout(lambda: os.access(norm, os.W_OK), timeout, False)
    checks.append({
        'id': 'writable',
        'name': '可写入',
        'status': 'ok' if writable else 'warn',
        'detail': '目录允许写入（可用于托管项目配置）' if writable else '目录不可写（整库载入可能不受影响，单项目启动需可写）',
        'fix': {
            'id': 'local_write',
            'title': '授予本地目录写入权限',
            'steps': [
                '右键目录 → 属性 → 安全 → 编辑，给当前用户添加“写入”权限。',
            ],
            'commands': [f'icacls "{norm}" /grant "%USERNAME%:(OI)(CI)M"'],
            'requires_admin': False,
        } if not writable else None,
    })

    if os.path.isfile(norm):
        is_evars = os.path.basename(norm).lower().startswith('evars') and norm.lower().endswith('.bat')
        checks.append({
            'id': 'project_file',
            'name': '项目文件',
            'status': 'ok' if is_evars else 'fail',
            'detail': '是 evarsXXX.bat 项目文件' if is_evars else '不是 evarsXXX.bat 项目文件',
            'fix': {
                'id': 'not_evars',
                'title': '选择正确的项目文件',
                'steps': ['项目文件应为 evars项目名.bat，例如 evarsDPH.bat。'],
                'commands': [],
                'requires_admin': False,
            } if not is_evars else None,
        })
    else:
        try:
            import e3d_scanner as scanner
            projects, info = scanner.scan_library(norm, timeout=timeout)
            direct = len(scanner._find_direct_evars(norm))
            subdirs = len(scanner._find_project_dirs(norm))
        except Exception:
            projects, info, direct, subdirs = [], {}, 0, 0
        if projects:
            detail = f'识别到 {len(projects)} 个项目（本层 {direct} 个，项目子文件夹 {subdirs} 个）'
        else:
            detail = '未发现 evarsXXX.bat，下一层子文件夹也没有项目文件'
        checks.append({
            'id': 'project_files',
            'name': '项目文件',
            'status': 'ok' if projects else 'fail',
            'detail': detail,
            'fix': {
                'id': 'no_projects',
                'title': '确认项目库结构',
                'steps': [
                    '项目库根目录的下一层，每个项目文件夹内应存在 evars项目名.bat。',
                    '没有 evars*.bat 的文件夹不会被识别为项目。',
                ],
                'commands': [f'dir /b /s "{norm}\\evars*.bat"'],
                'requires_admin': False,
            } if not projects else None,
        })

    return _finish(checks, path=norm)


def _diagnose_unc(norm, timeout=8):
    checks = []
    host = norm.lstrip('\\').split('\\')[0] if norm.startswith('\\\\') else ''
    share = _share_root_of(norm)

    # 1. 路径可达
    exists = util.run_with_timeout(lambda: os.path.exists(norm), timeout, False)
    checks.append({
        'id': 'exists',
        'name': '共享路径可达',
        'status': 'ok' if exists else 'fail',
        'detail': norm if exists else '无法访问该共享路径',
        'fix': None,
    })

    # 2. 主机解析
    host_ok = False
    host_detail = ''
    if host:
        try:
            socket.gethostbyname(host)
            host_ok = True
            host_detail = f'主机 {host} 可解析'
        except OSError as e:
            host_detail = f'主机 {host} 解析失败：{e}'
    else:
        host_detail = '无法从路径中解析主机名'
    checks.append({
        'id': 'host',
        'name': '主机解析',
        'status': 'ok' if host_ok else 'fail',
        'detail': host_detail,
        'fix': {
            'id': 'host_dns',
            'title': '检查主机名 / DNS',
            'steps': [
                f'确认主机名 {host} 拼写正确，且与服务器实际名称一致。',
                '可在资源管理器地址栏输入该路径测试是否可访问。',
            ],
            'commands': [f'ping -n 1 {host}', f'net view \\\\{host}'],
            'requires_admin': False,
        } if not host_ok else None,
    })

    # 3. SMB 端口
    port_ok = False
    port_detail = ''
    if host_ok:
        try:
            with socket.create_connection((host, 445), timeout=3):
                port_ok = True
            port_detail = f'{host}:445 可连接（SMB 服务在线）'
        except OSError as e:
            port_detail = f'{host}:445 连接失败：{e}'
    else:
        port_detail = '主机不可解析，跳过端口检查'
    checks.append({
        'id': 'port445',
        'name': 'SMB 端口 445',
        'status': 'ok' if port_ok else ('skip' if not host_ok else 'fail'),
        'detail': port_detail,
        'fix': {
            'id': 'smb_port',
            'title': '确认 SMB 服务与防火墙',
            'steps': [
                '确认服务器已开启文件共享（445 端口）。',
                '确认本机或服务器防火墙未拦截 445 端口。',
                '管理员可在服务器上执行：Get-SmbServerConfiguration | Select EnableSMB2Protocol',
            ],
            'commands': ['netstat -an | findstr :445'],
            'requires_admin': False,
        } if (host_ok and not port_ok) else None,
    })

    # 4. 服务状态
    svc = _smb_service_status()
    checks.append({
        'id': 'smb_service',
        'name': 'SMB 客户端服务',
        'status': 'ok' if svc == 'running' else ('warn' if svc == 'unknown' else 'fail'),
        'detail': {
            'running': 'LanmanWorkstation 服务运行中',
            'stopped': 'LanmanWorkstation 服务未运行',
            'unknown': '无法确认服务状态',
        }[svc or 'unknown'],
        'fix': {
            'id': 'start_smb_service',
            'title': '启动 LanmanWorkstation 服务',
            'steps': ['以管理员身份打开命令提示符，执行右侧命令。'],
            'commands': ['sc start lanmanworkstation'],
            'requires_admin': True,
        } if svc == 'stopped' else None,
    })

    # 5. 共享列表访问（带超时）
    share_ok = False
    share_detail = ''
    net_code = None
    net_out = ''
    if host_ok and port_ok:
        share_ok = util.run_with_timeout(lambda: os.path.isdir(share), timeout, False)
        if share_ok:
            share_detail = f'共享 {share} 可访问'
        else:
            net_code, net_out = _net_use_test(share, timeout=timeout)
            if net_code == 0:
                share_ok = util.run_with_timeout(lambda: os.path.isdir(share), timeout, False)
                share_detail = '共享可访问（连接已建立）' if share_ok else '共享存在但内容读取失败'
            else:
                share_detail = _net_error_detail(net_code) + (f'（命令输出：{net_out.strip()[:120]}）' if net_out.strip() else '')
    else:
        share_detail = '主机或端口检查未通过，跳过共享测试'

    auth_like = net_code in (5, 86, 1326, 1331, 1396)
    guest_setting = _guest_auth_setting()
    guest_fix = None
    if auth_like or (not share_ok and host_ok and port_ok and guest_setting is not True):
        guest_fix = {
            'id': 'guest_auth',
            'title': '启用本机 SMB 来宾登录（AllowInsecureGuestAuth）',
            'steps': [
                '该问题常见于本机不支持“来宾访问”远程共享。',
                '以管理员身份运行命令提示符，执行右侧命令后重启电脑（或重新连接）。',
                '如果服务器要求账号密码，请使用 net use 指定有效用户名。',
            ],
            'commands': [
                'reg add "HKLM\\SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters" /v AllowInsecureGuestAuth /t REG_DWORD /d 1 /f',
            ],
            'requires_admin': True,
        }

    checks.append({
        'id': 'share',
        'name': '共享访问',
        'status': 'ok' if share_ok else ('skip' if not (host_ok and port_ok) else 'fail'),
        'detail': share_detail,
        'fix': guest_fix if (auth_like or (not share_ok and host_ok and port_ok and guest_setting is not True)) else None,
    })

    # 6. 来宾策略状态
    if guest_setting is None:
        guest_detail = '无法读取注册表策略（可能不是 Windows 或权限不足）'
        guest_status = 'skip'
    elif guest_setting:
        guest_detail = 'AllowInsecureGuestAuth = 1，来宾登录已允许'
        guest_status = 'ok'
    else:
        guest_detail = 'AllowInsecureGuestAuth = 0 / 未设置，本机可能拒绝来宾访问远程共享'
        guest_status = 'warn' if share_ok else 'fail'
    checks.append({
        'id': 'guest_policy',
        'name': '来宾登录策略',
        'status': guest_status,
        'detail': guest_detail,
        'fix': guest_fix if guest_status == 'fail' else None,
    })

    # 7. 共享中的项目文件
    projects = []
    if share_ok:
        try:
            import e3d_scanner as scanner
            projects, _ = scanner.scan_library(norm, timeout=timeout)
        except Exception:
            projects = []
    checks.append({
        'id': 'projects',
        'name': '项目发现',
        'status': 'ok' if projects else ('skip' if not share_ok else 'fail'),
        'detail': f'识别到 {len(projects)} 个项目' if projects else ('共享不可访问，跳过' if not share_ok else '未发现 evars*.bat 项目文件'),
        'fix': {
            'id': 'no_remote_projects',
            'title': '确认远端项目库结构',
            'steps': [
                '项目库根目录的下一层，每个项目文件夹内应存在 evars项目名.bat。',
                '确认添加路径是项目库根目录，而不是项目文件夹。',
            ],
            'commands': [f'dir /b /s "{norm}\\evars*.bat"'],
            'requires_admin': False,
        } if share_ok and not projects else None,
    })

    return _finish(checks, path=norm, protocol='unc')


def _finish(checks, path='', protocol='local'):
    fixes = []
    seen = set()
    for c in checks:
        f = c.get('fix')
        if f and f.get('id') not in seen:
            seen.add(f['id'])
            fixes.append(f)
    ok = all(c['status'] in ('ok', 'skip') for c in checks)
    return {
        'ok': ok,
        'path': path,
        'protocol': protocol,
        'checks': checks,
        'fixes': fixes,
    }


def apply_fix(fix_id, path='', timeout=10):
    """
    尝试执行内置修复。
    目前支持：
      guest_auth      启用 AllowInsecureGuestAuth（需管理员）
      smb_reconnect   断开并重建指定共享连接
    返回 {ok, message, output, needs_admin?}
    """
    norm = util.normalize_path(path)
    share = _share_root_of(norm) if norm.startswith('\\\\') else ''

    if fix_id == 'guest_auth':
        if _guest_auth_setting() is True:
            return {'ok': True, 'message': 'AllowInsecureGuestAuth 已是启用状态', 'output': ''}
        code, out = _run([
            'reg', 'add',
            r'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters',
            '/v', 'AllowInsecureGuestAuth', '/t', 'REG_DWORD', '/d', '1', '/f',
        ], timeout=timeout)
        if code == 0:
            return {'ok': True, 'message': '已启用 AllowInsecureGuestAuth，建议重启电脑或重新连接共享', 'output': out}
        if code == 5 or 'denied' in out.lower() or '拒绝访问' in out:
            return {
                'ok': False,
                'message': '没有管理员权限，无法修改注册表',
                'needs_admin': True,
                'output': out,
            }
        return {'ok': False, 'message': f'修复失败（错误码 {code}）', 'output': out}

    if fix_id == 'smb_reconnect':
        if not share:
            return {'ok': False, 'message': '仅支持网络（UNC）路径', 'output': ''}
        code, out = _run(['net', 'use', share, '/delete', '/y'], timeout=timeout)
        if code != 0 and code != 2:  # 2 = 找不到连接，视为已断开
            return {'ok': False, 'message': '断开旧连接失败', 'output': out}
        return {
            'ok': True,
            'message': f'已断开 {share} 的旧连接，请重新添加路径库或重试扫描',
            'output': out,
        }

    return {'ok': False, 'message': f'未知修复项: {fix_id}', 'output': ''}
