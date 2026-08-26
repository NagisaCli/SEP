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
    71: '已到达远程计算机的连接数上限：目标主机为 Windows 桌面版系统（最大限制 20 个并发连接），当前连接数已满。请在共享主机上清理连接（net session /delete /y）或重启计算机。',
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
            projects, _info = scanner.scan_library(norm, timeout=timeout)
            direct = len(scanner._find_direct_evars(norm))
            subdirs = len(scanner._find_project_dirs(norm))
        except Exception:
            projects, direct, subdirs = [], 0, 0
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
                m_err = re.search(r'(?:System error|发生系统错误|系统错误)\s*(\d+)', net_out, re.IGNORECASE)
                real_code = int(m_err.group(1)) if m_err else net_code
                share_detail = _net_error_detail(real_code) + (f'（命令输出：{net_out.strip()[:120]}）' if net_out.strip() else '')
    else:
        share_detail = '主机或端口检查未通过，跳过共享测试'

    m_err = re.search(r'(?:System error|发生系统错误|系统错误)\s*(\d+)', net_out, re.IGNORECASE)
    err_num = int(m_err.group(1)) if m_err else net_code
    auth_like = err_num in (5, 86, 1326, 1331, 1396)
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

    share_fix = None
    if err_num == 71:
        share_fix = {
            'id': 'session_limit',
            'title': '清理远程共享连接数（连接数已达上限）',
            'steps': [
                f'目标主机 {host} 为 Windows 桌面版系统，并发连接数已达 20 个上限。',
                f'请在共享主机 {host} 上以管理员身份打开 CMD，执行：net session /delete /y 清理挂起连接。',
                '或在共享主机上重启计算机 / 重启 LanmanServer 服务。',
            ],
            'commands': ['net session /delete /y'],
            'requires_admin': True,
        }
    elif err_num == 1219:
        share_fix = {
            'id': 'smb_reconnect',
            'title': '断开冲突的旧连接并重试',
            'steps': [
                f'本机与 {host} 之间存在凭据冲突的旧连接。',
                '请点击修复断开旧连接，或执行 net use * /delete /y。',
            ],
            'commands': [f'net use {share} /delete /y', 'net use * /delete /y'],
            'requires_admin': False,
        }
    elif guest_fix:
        share_fix = guest_fix

    checks.append({
        'id': 'share',
        'name': '共享访问',
        'status': 'ok' if share_ok else ('skip' if not (host_ok and port_ok) else 'fail'),
        'detail': share_detail,
        'fix': share_fix,
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


def _extract_paths_from_set_line(line, pml_only=False):
    """
    从 set VAR=path1;path2;%VAR% 等语句中提取具体路径。
    过滤掉环境变量占位符 %...% 与非路径值。
    """
    m = re.match(r'^\s*set\s+([a-zA-Z0-9_]+)\s*=\s*(.*)$', line, re.IGNORECASE)
    if not m:
        return []
    var_name = m.group(1).lower()
    if pml_only and var_name not in ('pmlui', 'pmllib', 'pmlnet', 'pml_ui', 'pml_lib'):
        return []
    val = m.group(2).strip()
    raw_parts = [p.strip().strip('"').strip("'") for p in val.split(';') if p.strip()]
    paths = []
    for p in raw_parts:
        if re.match(r'^%[a-zA-Z0-9_()]+%$', p, re.IGNORECASE):
            continue
        if re.match(r'^[a-zA-Z]:[\\/]', p) or p.startswith('\\\\'):
            paths.append(util.normalize_path(p))
    return paths


def _is_unc_path_reachable(unc_path, timeout=1.5):
    """快速测试 UNC 路径是否在线可达（先探测 445 端口防阻塞）。"""
    host = unc_path.lstrip('\\').split('\\')[0] if unc_path.startswith('\\\\') else ''
    if not host:
        return False
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(timeout)
        try:
            s.connect((host, 445))
        finally:
            s.close()
        return util.run_with_timeout(lambda: os.path.exists(unc_path), timeout, False)
    except Exception:
        return False


def _check_path_validity(p, timeout=1.5):
    """检查单个路径有效性。返回 (is_ok, reason)。"""
    if not p:
        return False, '路径为空'
    if p.startswith('\\\\'):
        if _is_unc_path_reachable(p, timeout=timeout):
            return True, 'UNC 路径在线且可访问'
        return False, '网络路径离线或不可达（可能导致启动严重卡顿）'
    exists = util.run_with_timeout(lambda: os.path.exists(p), timeout=timeout, default=False)
    if exists:
        return True, '本地路径存在'
    return False, '本地路径不存在（死链）'


def diagnose_e3d_config(e3d_install_dir=None, projects_dir=None, timeout=1.5, check_my_projects=None):
    """
    全面诊断 E3D 本地配置文件与运行环境：
    1. evars.bat / evars.init 文件存在性与基础配置；
    2. evars.init 中的 PMLUI / PMLLIB / PMLNET 路径有效性；
    3. custom_evars.bat 中的插件路径、死链、网络挂载与离线 UNC 阻塞风险；
    4. 托管区与已选项目健康度。
    """
    is_custom_env = bool(e3d_install_dir or projects_dir)
    if check_my_projects is None:
        check_my_projects = not is_custom_env
    if not e3d_install_dir:
        try:
            import e3d_config
            res = e3d_config.detect_from_cache() or e3d_config.detect_e3d(verbose=False)
            if res:
                e3d_install_dir = res.get('install_dir')
                if not projects_dir:
                    projects_dir = res.get('projects_dir')
        except Exception:
            pass

    if not projects_dir:
        try:
            import e3d_store as store
            import e3d_launcher as launcher
            data = store.load_data()
            projects_dir = launcher.get_local_projects_dir(data)
        except Exception:
            projects_dir = r'D:\AVEVA\Projects\E3D3.1'

    e3d_install_dir = util.normalize_path(e3d_install_dir or r'D:\AVEVA\Everything3D3.1')
    projects_dir = util.normalize_path(projects_dir or r'D:\AVEVA\Projects\E3D3.1')

    evars_bat = os.path.join(e3d_install_dir, 'evars.bat') if e3d_install_dir else ''
    evars_init = os.path.join(e3d_install_dir, 'evars.init') if e3d_install_dir else ''
    custom_evars = os.path.join(projects_dir, 'custom_evars.bat') if projects_dir else ''

    checks = []
    issues = []

    # 1. E3D 基础安装目录与启动文件
    install_ok = bool(e3d_install_dir and os.path.isdir(e3d_install_dir))
    evars_ok = bool(evars_bat and os.path.isfile(evars_bat) and evars_init and os.path.isfile(evars_init))
    checks.append({
        'id': 'e3d_core_files',
        'name': 'E3D 核心启动文件',
        'status': 'ok' if (install_ok and evars_ok) else 'fail',
        'detail': f'安装目录: {e3d_install_dir}' if (install_ok and evars_ok) else f'未在 {e3d_install_dir} 找到完整的 evars.bat/evars.init',
        'fix': None,
    })
    if not (install_ok and evars_ok):
        issues.append('未找到完整的 E3D 安装路径或 evars 启动脚本')

    # 2. evars.init 插件与环境路径检查
    init_invalid_lines = []
    if evars_init and os.path.isfile(evars_init):
        try:
            text, _enc = util.read_text_smart(evars_init)
            for idx, line in enumerate(text.splitlines(), start=1):
                raw = line.strip()
                if not raw or raw.lower().startswith('rem') or raw.startswith('::'):
                    continue
                paths = _extract_paths_from_set_line(raw, pml_only=True)
                for p in paths:
                    ok, reason = _check_path_validity(p, timeout=timeout)
                    if not ok:
                        init_invalid_lines.append({
                            'line_num': idx,
                            'raw_line': raw,
                            'path': p,
                            'reason': reason,
                        })
        except Exception as e:
            issues.append(f'读取 evars.init 异常: {e}')

    if init_invalid_lines:
        checks.append({
            'id': 'evars_init_paths',
            'name': 'evars.init 插件与环境路径',
            'status': 'warn',
            'detail': f'发现 {len(init_invalid_lines)} 处不存在的死路径设置（可能导致 PML / UI 初始化报错）',
            'invalid_lines': init_invalid_lines,
            'fix': {
                'id': 'e3d_config_clean',
                'title': '清理 evars.init 无效路径',
                'steps': ['自动注释 evars.init 中的无效插件路径'],
                'commands': [],
                'requires_admin': False,
            }
        })
        issues.append(f'evars.init 中包含 {len(init_invalid_lines)} 处失效路径')
    else:
        checks.append({
            'id': 'evars_init_paths',
            'name': 'evars.init 插件与环境路径',
            'status': 'ok',
            'detail': '未发现失效的 PML / 环境路径',
            'invalid_lines': [],
            'fix': None,
        })

    # 3. custom_evars.bat 检查
    custom_invalid_lines = []
    custom_timeout_lines = []
    if custom_evars and os.path.isfile(custom_evars):
        try:
            text, _enc = util.read_text_smart(custom_evars)
            in_managed = False
            for idx, line in enumerate(text.splitlines(), start=1):
                raw = line.strip()
                if '>>> SEP MANAGED PROJECTS' in raw:
                    in_managed = True
                    continue
                if '<<< SEP MANAGED PROJECTS' in raw:
                    in_managed = False
                    continue
                if not raw or raw.lower().startswith('rem') or raw.startswith('::'):
                    continue

                # 检查括号语法（AVEVA evars 解析器不支持括号）
                if raw in ('(', ')') or raw.endswith('(') or raw.startswith(')'):
                    custom_invalid_lines.append({
                        'line_num': idx,
                        'raw_line': raw,
                        'path': '',
                        'reason': 'AVEVA evars 启动引擎不支持括号语法 ( / )',
                        'in_managed': in_managed,
                    })
                    continue

                # 检查未定义变量的 if not exist / if exist (如 if not exist "%mms_installed_dir%")
                m_if_var = re.search(r'if\s+(?:not\s+)?exist\s+"%([^%]+)%"', raw, re.IGNORECASE)
                if m_if_var:
                    var_name = m_if_var.group(1).lower()
                    # 检查此变量是否在此 bat 之前被定义过且未被注释
                    var_defined = any(
                        re.search(r'^\s*set\s+' + re.escape(var_name) + r'=', l, re.IGNORECASE)
                        for l in lines[:idx-1]
                    )
                    if not var_defined and not os.environ.get(var_name):
                        custom_invalid_lines.append({
                            'line_num': idx,
                            'raw_line': raw,
                            'path': '',
                            'reason': f'变量 %{var_name}% 未定义，E3D 解析器会报 Invalid syntax 错误',
                            'in_managed': in_managed,
                        })
                        continue

                # 检查 set 语句
                paths = _extract_paths_from_set_line(raw)
                for p in paths:
                    ok, reason = _check_path_validity(p, timeout=timeout)
                    if not ok:
                        custom_invalid_lines.append({
                            'line_num': idx,
                            'raw_line': raw,
                            'path': p,
                            'reason': reason,
                            'in_managed': in_managed,
                        })

                # 检查 net use 语句
                m_net = re.search(r'net\s+use\s+(?:[a-zA-Z]:|\*)\s+(\\\\[^\s]+)', raw, re.IGNORECASE)
                if m_net:
                    unc_target = m_net.group(1)
                    if not _is_unc_path_reachable(unc_target, timeout=timeout):
                        custom_timeout_lines.append({
                            'line_num': idx,
                            'raw_line': raw,
                            'path': unc_target,
                            'reason': '离线网络映射（每次启动都会尝试连接并阻塞数秒）',
                            'in_managed': in_managed,
                        })

                # 检查 call / if exist 语句 (非托管区手写)
                if not in_managed:
                    m_call = re.search(r'(?:if\s+exist\s+["\']?([^"\'\r\n]+)["\']?\s+)?call\s+["\']?([^"\'\r\n]+)["\']?', raw, re.IGNORECASE)
                    if m_call:
                        target = m_call.group(1) or m_call.group(2)
                        target = util.normalize_path(target.strip())
                        if target.lower().endswith('.bat') and not any(k in target.lower() for k in ('projects.bat', 'custom_evars.bat')):
                            ok, reason = _check_path_validity(target, timeout=timeout)
                            if not ok:
                                if target.startswith('\\\\'):
                                    custom_timeout_lines.append({
                                        'line_num': idx,
                                        'raw_line': raw,
                                        'path': target,
                                        'reason': reason,
                                        'in_managed': False,
                                    })
                                else:
                                    custom_invalid_lines.append({
                                        'line_num': idx,
                                        'raw_line': raw,
                                        'path': target,
                                        'reason': reason,
                                        'in_managed': False,
                                    })
        except Exception as e:
            issues.append(f'读取 custom_evars.bat 异常: {e}')

    custom_status = 'ok'
    custom_details = []
    if custom_invalid_lines:
        custom_details.append(f'失效死路径 {len(custom_invalid_lines)} 个')
        custom_status = 'warn'
    if custom_timeout_lines:
        custom_details.append(f'超时网络阻塞项 {len(custom_timeout_lines)} 个')
        custom_status = 'warn'

    checks.append({
        'id': 'custom_evars_health',
        'name': 'custom_evars.bat 脚本健康度',
        'status': custom_status,
        'detail': '；'.join(custom_details) if custom_details else 'custom_evars.bat 配置正常',
        'invalid_lines': custom_invalid_lines,
        'timeout_lines': custom_timeout_lines,
        'fix': {
            'id': 'e3d_config_clean',
            'title': '清理 custom_evars.bat 失效/超时项',
            'steps': ['安全注释不存在的本地路径与离线网络映射，消除 E3D 启动卡顿'],
            'commands': [],
            'requires_admin': False,
        } if (custom_invalid_lines or custom_timeout_lines) else None,
    })

    # 4. 托管区与“我的项目”健康度
    if check_my_projects:
        try:
            import e3d_store as store
            data = store.load_data()
            my_projects = data.get('my_projects') or []
            offline_my_projects = []
            for p in my_projects:
                bp = p.get('bat_path', '')
                ok, reason = _check_path_validity(bp, timeout=timeout)
                if not ok:
                    offline_my_projects.append({
                        'id': p.get('id'),
                        'name': p.get('name'),
                        'bat_path': bp,
                        'reason': reason,
                    })
            if offline_my_projects:
                checks.append({
                    'id': 'my_projects_health',
                    'name': '我的项目 / 托管区项目可达性',
                    'status': 'warn',
                    'detail': f'“我的项目”中有 {len(offline_my_projects)} 个项目离线或不可达（载入全部启动时会导致 if exist 严重网络超时卡顿）',
                    'offline_projects': offline_my_projects,
                    'fix': {
                        'id': 'e3d_config_clean',
                        'title': '刷新托管区',
                        'steps': ['自动从当前托管区中剔除离线项目'],
                        'commands': [],
                        'requires_admin': False,
                    }
                })
                issues.append(f'“我的项目”中存在 {len(offline_my_projects)} 个离线项目')
            else:
                checks.append({
                    'id': 'my_projects_health',
                    'name': '我的项目 / 托管区项目可达性',
                    'status': 'ok',
                    'detail': f'全部 {len(my_projects)} 个项目均在线可达',
                    'offline_projects': [],
                    'fix': None,
                })
        except Exception:
            pass

    overall_ok = all(c['status'] in ('ok', 'skip') for c in checks)
    return {
        'ok': overall_ok,
        'install_dir': e3d_install_dir,
        'projects_dir': projects_dir,
        'evars_init': evars_init,
        'custom_evars': custom_evars,
        'checks': checks,
        'issues': issues,
        'fixes': [c['fix'] for c in checks if c.get('fix')],
    }


def fix_e3d_config(e3d_install_dir=None, projects_dir=None, timeout=2, clean_offline_unc=True, clean_invalid_paths=True):
    """
    一键安全修复与清理 E3D 配置文件：
    1. 自动备份 evars.init 与 custom_evars.bat 为 .sep.bak；
    2. 安全注释 evars.init 中不存在的死路径；
    3. 安全注释 custom_evars.bat 中不存在的本地死路径与离线超时网络项；
    4. 刷新 custom_evars.bat 托管区，仅保留在线项目；
    返回 {ok, message, changes}
    """
    report = diagnose_e3d_config(e3d_install_dir, projects_dir, timeout=timeout)
    evars_init = report.get('evars_init')
    custom_evars = report.get('custom_evars')
    projects_dir = report.get('projects_dir')

    changes = []

    # 1. 修复 evars.init
    if evars_init and os.path.isfile(evars_init) and clean_invalid_paths:
        try:
            text, enc = util.read_text_smart(evars_init)
            lines = text.splitlines()
            modified = False
            new_lines = []
            for line in lines:
                raw = line.strip()
                if not raw or raw.lower().startswith('rem') or raw.startswith('::'):
                    new_lines.append(line)
                    continue
                paths = _extract_paths_from_set_line(raw, pml_only=True)
                has_invalid = False
                for p in paths:
                    ok, _ = _check_path_validity(p, timeout=timeout)
                    if not ok:
                        has_invalid = True
                        break
                if has_invalid:
                    new_lines.append(f'rem [SEP DISABLED - PATH NOT FOUND] {line}')
                    changes.append(f'evars.init: 注释失效路径行 -> {raw}')
                    modified = True
                else:
                    new_lines.append(line)
            if modified:
                bak = evars_init + '.sep.bak'
                if not os.path.exists(bak):
                    import shutil
                    shutil.copy2(evars_init, bak)
                util.write_text_preserve(evars_init, '\r\n'.join(new_lines) + '\r\n', enc)
        except Exception as e:
            return {'ok': False, 'message': f'修复 evars.init 失败: {e}', 'changes': changes}

    # 2. 修复 custom_evars.bat (非托管区)
    if custom_evars and os.path.isfile(custom_evars):
        try:
            text, enc = util.read_text_smart(custom_evars)
            lines = text.splitlines()
            modified = False
            new_lines = []
            in_managed = False
            for line in lines:
                raw = line.strip()
                if '>>> SEP MANAGED PROJECTS' in raw:
                    in_managed = True
                    new_lines.append(line)
                    continue
                if '<<< SEP MANAGED PROJECTS' in raw:
                    in_managed = False
                    new_lines.append(line)
                    continue

                if in_managed or not raw or raw.lower().startswith('rem') or raw.startswith('::'):
                    new_lines.append(line)
                    continue

                # 检查括号语法
                if raw in ('(', ')') or raw.endswith('(') or raw.startswith(')'):
                    new_lines.append(f'rem [SEP DISABLED - UNSUPPORTED SYNTAX] {line}')
                    changes.append(f'custom_evars.bat: 注释不支持的括号语法 -> {raw}')
                    modified = True
                    continue

                # 检查未定义变量的 if not exist / if exist (如 if not exist "%mms_installed_dir%")
                m_if_var = re.search(r'if\s+(?:not\s+)?exist\s+"%([^%]+)%"', raw, re.IGNORECASE)
                if m_if_var:
                    var_name = m_if_var.group(1).lower()
                    var_defined = any(
                        re.search(r'^\s*set\s+' + re.escape(var_name) + r'=', l, re.IGNORECASE)
                        for l in new_lines
                    )
                    if not var_defined and not os.environ.get(var_name):
                        new_lines.append(f'rem [SEP DISABLED - UNDEFINED VAR] {line}')
                        changes.append(f'custom_evars.bat: 注释未定义变量条件行 -> {raw}')
                        modified = True
                        continue

                # 检查 set
                paths = _extract_paths_from_set_line(raw)
                has_invalid = any(not _check_path_validity(p, timeout=timeout)[0] for p in paths)
                if has_invalid and clean_invalid_paths:
                    new_lines.append(f'rem [SEP DISABLED - PATH NOT FOUND] {line}')
                    changes.append(f'custom_evars.bat: 注释失效路径行 -> {raw}')
                    modified = True
                    continue

                # 检查 net use
                m_net = re.search(r'net\s+use\s+(?:[a-zA-Z]:|\*)\s+(\\\\[^\s]+)', raw, re.IGNORECASE)
                if m_net and clean_offline_unc:
                    unc_target = m_net.group(1)
                    if not _is_unc_path_reachable(unc_target, timeout=timeout):
                        new_lines.append(f'rem [SEP DISABLED - UNREACHABLE UNC] {line}')
                        changes.append(f'custom_evars.bat: 注释离线网络挂载 -> {raw}')
                        modified = True
                        continue

                # 检查手写 call
                m_call = re.search(r'(?:if\s+exist\s+["\']?([^"\'\r\n]+)["\']?\s+)?call\s+["\']?([^"\'\r\n]+)["\']?', raw, re.IGNORECASE)
                if m_call and clean_offline_unc:
                    target = util.normalize_path((m_call.group(1) or m_call.group(2)).strip())
                    if target.lower().endswith('.bat') and not any(k in target.lower() for k in ('projects.bat', 'custom_evars.bat')):
                        ok, _ = _check_path_validity(target, timeout=timeout)
                        if not ok:
                            new_lines.append(f'rem [SEP DISABLED - UNREACHABLE PROJECT] {line}')
                            changes.append(f'custom_evars.bat: 注释离线项目调用 -> {raw}')
                            modified = True
                            continue

                new_lines.append(line)

            if modified:
                bak = custom_evars + '.sep.bak'
                if not os.path.exists(bak):
                    import shutil
                    shutil.copy2(custom_evars, bak)
                util.write_text_preserve(custom_evars, '\r\n'.join(new_lines) + '\r\n', enc)
        except Exception as e:
            return {'ok': False, 'message': f'修复 custom_evars.bat 失败: {e}', 'changes': changes}

    # 3. 刷新 SEP 托管区中的离线项目
    try:
        import e3d_store as store
        import e3d_launcher as launcher
        managed_now = launcher.read_managed(projects_dir)
        valid_managed = [p for p in managed_now if _check_path_validity(p, timeout=timeout)[0]]
        if len(valid_managed) != len(managed_now):
            launcher.write_managed(projects_dir, valid_managed)
            removed = set(managed_now) - set(valid_managed)
            for r in removed:
                changes.append(f'托管区: 剔除离线项目 -> {r}')
    except Exception as e:
        changes.append(f'刷新托管区提示: {e}')

    return {
        'ok': True,
        'message': f'修复完成，共处理 {len(changes)} 处异常配置' if changes else '未发现需要修复的配置项',
        'changes': changes,
    }


def clean_userdata_cache(userdata_dir=None):
    """
    清理 E3D USERDATA 目录中的死锁与临时文件（*.lok, *.tmp, AvevaAbaLog.txt 等）。
    """
    if not userdata_dir:
        userdata_dir = r"D:\AVEVA\USERDATA"
    userdata_dir = util.normalize_path(userdata_dir)
    if not os.path.isdir(userdata_dir):
        return {'ok': False, 'message': f'USERDATA 目录不存在: {userdata_dir}', 'cleaned': []}

    cleaned = []
    total_bytes = 0

    try:
        for root, dirs, files in os.walk(userdata_dir):
            for f in files:
                low = f.lower()
                should_delete = False
                if low.endswith(('.lok', '.tmp', '.temp', '.dmp', '.crash')) or low in ('avevaabalog.txt',):
                    should_delete = True
                if should_delete:
                    fp = os.path.join(root, f)
                    try:
                        sz = os.path.getsize(fp)
                        os.remove(fp)
                        cleaned.append(os.path.relpath(fp, userdata_dir))
                        total_bytes += sz
                    except OSError:
                        pass
    except Exception as e:
        return {'ok': False, 'message': f'清理过程中出错: {e}', 'cleaned': cleaned}

    kb = total_bytes // 1024
    return {
        'ok': True,
        'message': f'清理完成，共清理 {len(cleaned)} 个文件（释放 {kb} KB 空间）' if cleaned else 'USERDATA 目录很干净，未发现死锁或临时残留文件',
        'cleaned': cleaned,
        'bytes_freed': total_bytes,
    }


def fix_cad_fonts_tool():
    """
    纯 Python 自动化修复 AutoCAD 缺失字体弹窗与映射问题：
    1. 自动定位 AutoCAD 2025 / 历史版本 Fonts 目录与 Support 目录；
    2. 生成常用缺失大字体与西文字体别名（hzfs, hzdx, @~!hztxt, superos, roma 等）；
    3. 全面更新 acad.fmp 映射表；
    4. 在 acaddoc.lsp 注入 FONTALT=gbcbig.shx 与 FONTEVAL=0。
    """
    changes = []
    font_candidates = [
        r"D:\AutoCAD 2025\Fonts",
        os.path.expandvars(r"%ProgramFiles%\Autodesk\AutoCAD 2025\Fonts"),
        r"D:\Program Files\Autodesk\AutoCAD 2025\Fonts",
    ]
    font_dirs = [d for d in font_candidates if os.path.isdir(d)]
    if font_dirs:
        cad_fonts = font_dirs[0]
        gbcbig = os.path.join(cad_fonts, 'gbcbig.shx')
        hztxt = os.path.join(cad_fonts, 'hztxt.shx')
        simplex = os.path.join(cad_fonts, 'simplex.shx')
        base_big = gbcbig if os.path.isfile(gbcbig) else (hztxt if os.path.isfile(hztxt) else None)
        base_smp = simplex if os.path.isfile(simplex) else None

        if base_big:
            aliases = {
                'hzfs.shx': base_big,
                'HZDX.SHX': base_big,
                '@~!hztxt.shx': base_big,
                'tssdchn.shx': base_big,
                'pkpm.shx': base_big,
                'fs68.shx': base_big,
                'fs.shx': base_big,
                'ht68.shx': base_big,
                'kt68.shx': base_big,
                'hztxt.shx': base_big,
            }
            if base_smp:
                aliases['SUPEROS.SHX'] = base_smp
                aliases['roma.shx'] = base_smp
                aliases['tssdeng.shx'] = base_smp
                aliases['txt.shx'] = base_smp

            for name, src in aliases.items():
                dst = os.path.join(cad_fonts, name)
                if not os.path.exists(dst):
                    try:
                        shutil.copy2(src, dst)
                        changes.append(f'CAD Fonts: 创建字体副本 {name}')
                    except Exception:
                        pass

    appdata = os.environ.get('APPDATA', '')
    support_candidates = [
        os.path.join(appdata, r"Autodesk\AutoCAD 2025\R25.0\chs\support"),
        os.path.join(appdata, r"Autodesk\AutoCAD 2024\R24.3\chs\support"),
        os.path.join(appdata, r"Autodesk\AutoCAD 2023\R24.2\chs\support"),
        r"D:\AutoCAD 2025\support",
    ]
    support_dirs = [d for d in support_candidates if os.path.isdir(d)]

    fmp_entries = [
        "hzfs;gbcbig.shx", "hzfs.shx;gbcbig.shx", "hzdx;gbcbig.shx", "hzdx.shx;gbcbig.shx", "HZDX.SHX;gbcbig.shx",
        "@~!hztxt;hztxt.shx", "@~!hztxt.shx;hztxt.shx", "hztxt;hztxt.shx", "hztxt.shx;gbcbig.shx",
        "hztxt_e;simplex.shx", "hztxt_e.shx;simplex.shx", "superos;simplex.shx", "superos.shx;simplex.shx",
        "SUPEROS.SHX;simplex.shx", "roma;simplex.shx", "roma.shx;simplex.shx",
        "FangSong_GB2312;simplex.shx", "FangSong_GB2312.shx;simplex.shx",
        "KaiTi_GB2312;simplex.shx", "KaiTi_GB2312.shx;simplex.shx",
        "@Arial Unicode MS;gbcbig.shx", "@Arial Unicode MS.shx;gbcbig.shx", "Arial Unicode MS;gbcbig.shx",
        "PC_TEXTSTYLE;gbcbig.shx", "YQ_DIM;gbcbig.shx", "hz2;gbcbig.shx", "hz;gbcbig.shx",
        "tssdchn;gbcbig.shx", "tssdchn.shx;gbcbig.shx", "tssdeng;simplex.shx", "tssdeng.shx;simplex.shx",
        "pkpm;gbcbig.shx", "pkpm.shx;gbcbig.shx", "fs68;gbcbig.shx", "fs68.shx;gbcbig.shx",
        "fs;gbcbig.shx", "fs.shx;gbcbig.shx", "ht68;gbcbig.shx", "ht68.shx;gbcbig.shx",
        "kt68;gbcbig.shx", "kt68.shx;gbcbig.shx", "txt;simplex.shx", "txt.shx;simplex.shx"
    ]

    for s_dir in support_dirs:
        fmp_file = os.path.join(s_dir, 'acad.fmp')
        if os.path.isfile(fmp_file):
            try:
                text, enc = util.read_text_smart(fmp_file)
                lines = [l.strip() for l in text.splitlines() if l.strip()]
                keys = {l.split(';')[0].strip().lower() for l in lines}
                added = 0
                for entry in fmp_entries:
                    k = entry.split(';')[0].strip().lower()
                    if k not in keys:
                        lines.append(entry)
                        keys.add(k)
                        added += 1
                if added > 0:
                    util.write_text_preserve(fmp_file, '\r\n'.join(lines) + '\r\n', enc)
                    changes.append(f'acad.fmp: 更新字体映射 ({added} 条新规则)')
            except Exception:
                pass

        lsp_file = os.path.join(s_dir, 'acaddoc.lsp')
        if os.path.isfile(lsp_file):
            try:
                text, enc = util.read_text_smart(lsp_file)
                if 'FONTALT' not in text:
                    lsp_code = '\r\n;;; CAD 缺失字体静默替用设置\r\n(vl-catch-all-apply \'setvar (list "FONTALT" "gbcbig.shx"))\r\n(vl-catch-all-apply \'setvar (list "FONTEVAL" 0))\r\n'
                    util.write_text_preserve(lsp_file, text.rstrip('\r\n') + lsp_code, enc)
                    changes.append('acaddoc.lsp: 注入 FONTALT/FONTEVAL 静默设置')
            except Exception:
                pass

    return {
        'ok': True,
        'message': f'CAD 字体修复完成（已应用 {len(changes)} 处优化）' if changes else 'CAD 字体配置已是最新状态',
        'changes': changes,
    }


def apply_fix(fix_id, path='', timeout=10):
    """
    尝试执行内置修复。
    目前支持：
      guest_auth        启用 AllowInsecureGuestAuth（需管理员）
      smb_reconnect     断开并重建指定共享连接
      e3d_config_clean  清理 E3D 配置文件死链与离线网络阻塞项
      clean_userdata    清理 USERDATA 临时死锁与日志缓存
      rebuild_pml_index 重建所有插件 pml.index 索引
      fix_cad_fonts     一键修复 AutoCAD 缺失字体弹窗与映射
    返回 {ok, message, output, needs_admin?}
    """
    norm = util.normalize_path(path)
    share = _share_root_of(norm) if norm.startswith('\\\\') else ''

    if fix_id == 'e3d_config_clean':
        res = fix_e3d_config(timeout=timeout)
        return {
            'ok': res.get('ok', False),
            'message': res.get('message', ''),
            'output': '\n'.join(res.get('changes', [])),
        }

    if fix_id == 'clean_userdata':
        res = clean_userdata_cache()
        return {
            'ok': res.get('ok', False),
            'message': res.get('message', ''),
            'output': '\n'.join(res.get('cleaned', [])),
        }

    if fix_id == 'rebuild_pml_index':
        import e3d_plugin
        res = e3d_plugin.rebuild_all_pml_indexes()
        lines = [f"{k}: {v['message']}" for k, v in res.items()]
        return {
            'ok': True,
            'message': f'已为 {len(res)} 个插件重构 PML 索引',
            'output': '\n'.join(lines),
        }

    if fix_id == 'fix_cad_fonts':
        res = fix_cad_fonts_tool()
        return {
            'ok': res.get('ok', False),
            'message': res.get('message', ''),
            'output': '\n'.join(res.get('changes', [])),
        }

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
