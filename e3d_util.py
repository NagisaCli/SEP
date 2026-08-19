#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SEP 通用工具模块
================
路径规范化、协议识别、稳定 ID、编码检测、带超时的远程文件访问等。
"""

import glob
import hashlib
import locale
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor, TimeoutError as FutureTimeout


# 目录定位（PyInstaller 兼容：运行数据在 exe 旁，打包数据在 _MEIPASS）
if getattr(sys, 'frozen', False):
    SCRIPT_DIR = os.path.dirname(sys.executable)
else:
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


def get_user_data_dir():
    """
    返回全局用户数据目录（例如 %APPDATA%\\SEP）。
    支持便携模式：如果 exe 同目录下存在 .portable 文件或环境变量 SEP_PORTABLE=1，则使用 SCRIPT_DIR。
    """
    if os.environ.get('SEP_PORTABLE') == '1' or os.path.exists(os.path.join(SCRIPT_DIR, '.portable')):
        return SCRIPT_DIR
    appdata = os.environ.get('APPDATA')
    if appdata:
        d = os.path.join(appdata, 'SEP')
    else:
        d = os.path.join(os.path.expanduser('~'), '.sep')
    try:
        os.makedirs(d, exist_ok=True)
    except OSError:
        pass
    return d


def get_config_file_path():
    """
    返回 e3d_projects.json 的有效存储路径。
    若全局目录中尚无配置文件，但本地 SCRIPT_DIR 中存在历史配置文件，
    则自动无缝迁移至全局目录，确保移动软件或更新 exe 时数据永不丢失。
    """
    data_dir = get_user_data_dir()
    global_cfg = os.path.join(data_dir, "e3d_projects.json")
    local_cfg = os.path.join(SCRIPT_DIR, "e3d_projects.json")
    if data_dir != SCRIPT_DIR and not os.path.exists(global_cfg) and os.path.exists(local_cfg):
        try:
            import shutil
            shutil.copy2(local_cfg, global_cfg)
        except Exception:
            pass
    return global_cfg


def get_paths_cache_path():
    """返回 e3d_paths.json 的存储路径。"""
    data_dir = get_user_data_dir()
    global_cache = os.path.join(data_dir, "e3d_paths.json")
    local_cache = os.path.join(SCRIPT_DIR, "e3d_paths.json")
    if data_dir != SCRIPT_DIR and not os.path.exists(global_cache) and os.path.exists(local_cache):
        try:
            import shutil
            shutil.copy2(local_cache, global_cache)
        except Exception:
            pass
    return global_cache


def resource_path(name):
    """返回打包后/源码目录下的资源文件绝对路径。"""
    if getattr(sys, 'frozen', False):
        base = getattr(sys, '_MEIPASS', SCRIPT_DIR)
        return os.path.join(base, name)
    return os.path.join(SCRIPT_DIR, name)


def add_script_path():
    """把源码/打包目录加入 sys.path，供动态导入 e3d_config 使用。"""
    for p in (SCRIPT_DIR, getattr(sys, '_MEIPASS', None)):
        if p and p not in sys.path:
            sys.path.insert(0, p)


def is_url(path):
    return bool(re.match(r'^https?://', str(path or '').strip(), re.IGNORECASE))



def normalize_path(path):
    """
    规范化用户输入的路径：
    - 去除首尾空白和引号
    - smb://host/share/... -> \\\\host\\share\\...
    - file:///C:/x -> C:\\x, file://server/share -> \\\\server\\share
    - 展开环境变量和 ~
    - 正斜杠转反斜杠，去掉多余尾斜杠
    URL (http/https) 原样返回。
    """
    if not path:
        return ''
    p = str(path).strip().strip('"')
    if not p:
        return ''
    low = p.lower()

    if re.match(r'^https?://', low):
        return p

    if low.startswith('smb://'):
        rest = p[6:].replace('/', '\\')
        if '\\' in rest:
            host, tail = rest.split('\\', 1)
            return '\\\\' + host + '\\' + tail
        return '\\\\' + rest

    if low.startswith('file://'):
        rest = p[7:]
        if rest.startswith('/'):
            rest = rest[1:]
        rest = rest.replace('/', '\\')
        if re.match(r'^[A-Za-z]:\\', rest):
            return _strip_trailing(rest)
        return '\\\\' + _strip_trailing(rest)

    p = os.path.expandvars(os.path.expanduser(p))
    p = p.replace('/', '\\')
    return _strip_trailing(p)


def _strip_trailing(p):
    p = p.strip()
    if not p:
        return ''
    if p in ('\\', '/'):
        return p
    # 盘符根目录保留尾斜杠
    if re.match(r'^[A-Za-z]:\\?$', p):
        return p.rstrip('\\') + '\\'
    while len(p) > 1 and p.endswith('\\'):
        p = p[:-1]
    return p


def path_protocol(path):
    """返回协议类型: local / unc / url"""
    p = normalize_path(path)
    if not p:
        return 'local'
    if is_url(p):
        return 'url'
    if p.startswith('\\\\'):
        return 'unc'
    return 'local'


def gen_id(prefix, *parts):
    """基于内容生成稳定 ID（相同路径扫描结果一致）。"""
    key = '|'.join(str(x).lower() for x in parts if x is not None)
    h = hashlib.sha1(key.encode('utf-8')).hexdigest()[:12]
    return f'{prefix}_{h}'


def now_iso():
    import datetime
    return datetime.datetime.now().isoformat(timespec='seconds')


def detect_encoding(filepath):
    """检测文件编码，优先 UTF-8(BOM)，其次 GBK，最后 latin-1。"""
    try:
        with open(filepath, 'rb') as f:
            data = f.read(4096)
    except OSError:
        return default_bat_encoding()
    if data.startswith(b'\xef\xbb\xbf'):
        return 'utf-8-sig'
    for enc in ('utf-8', 'gbk', 'latin-1'):
        try:
            data.decode(enc)
            return enc
        except (UnicodeDecodeError, UnicodeError):
            continue
    return default_bat_encoding()


def read_text_smart(filepath):
    """读取文本并返回 (text, encoding)。"""
    enc = detect_encoding(filepath)
    with open(filepath, 'r', encoding=enc, errors='replace') as f:
        return f.read(), enc


def write_text_preserve(filepath, text, encoding):
    """按指定编码写入文本，统一换行为 CRLF，保留 UTF-8 BOM。"""
    text = text.replace('\r\n', '\n').replace('\r', '\n').replace('\n', '\r\n')
    data = text.encode(encoding)
    with open(filepath, 'wb') as f:
        f.write(data)


def default_bat_encoding():
    """新创建的 bat 文件使用系统 ANSI 代码页（中文系统为 GBK）。"""
    try:
        enc = locale.getpreferredencoding(False).lower()
        if enc in ('cp936', 'gbk', 'gb2312', 'cp950', 'big5'):
            return 'gbk'
    except Exception:
        pass
    return 'utf-8'


def path_exists(path, timeout=6):
    """判断路径是否存在；UNC 路径带超时，避免卡死。"""
    p = normalize_path(path)
    if not p:
        return False
    if path_protocol(p) == 'unc':
        return run_with_timeout(lambda: os.path.exists(p), timeout, False)
    try:
        return os.path.exists(p)
    except OSError:
        return False


def run_with_timeout(fn, timeout=6, default=None):
    """在后台线程执行函数，超时返回 default。"""
    if timeout <= 0:
        try:
            return fn()
        except Exception:
            return default
    with ThreadPoolExecutor(max_workers=1) as ex:
        fut = ex.submit(fn)
        try:
            return fut.result(timeout=timeout)
        except FutureTimeout:
            fut.cancel()
            return default
        except Exception:
            return default


def validate_safe_bat_path(path):
    """
    校验并返回安全的批处理引用路径。
    防止双引号闭合、换行注入和批处理命令连接符 (&, |, <, >, ^)。
    """
    if not path:
        return ''
    p = normalize_path(path)
    if not p:
        return ''
    # 检查危险字符
    dangerous_chars = ['"', '\r', '\n', '\0', '&', '|', '<', '>', '^']
    found = [c for c in dangerous_chars if c in p]
    if found:
        raise ValueError(f'路径包含非法危险字符 {found}: {path}')
    return p


def find_e3d_lnk(preferred=''):
    """查找 E3D 启动快捷方式，找不到返回 None。支持开始菜单与桌面搜索。"""
    if preferred and os.path.exists(preferred):
        return preferred

    candidates = [
        r'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\AVEVA\Design\AVEVA Everything3D 3.1.lnk',
        os.path.expandvars(r'%ProgramData%\Microsoft\Windows\Start Menu\Programs\AVEVA\Design\AVEVA Everything3D 3.1.lnk'),
        os.path.expandvars(r'%AppData%\Microsoft\Windows\Start Menu\Programs\AVEVA\Design\AVEVA Everything3D 3.1.lnk'),
    ]

    for c in candidates:
        if c and os.path.exists(c):
            return c

    # 在开始菜单和桌面上递归模糊搜索
    roots = [
        os.path.expandvars(r'%ProgramData%\Microsoft\Windows\Start Menu'),
        os.path.expandvars(r'%AppData%\Microsoft\Windows\Start Menu'),
        os.path.expandvars(r'%USERPROFILE%\Desktop'),
        os.path.expandvars(r'%PUBLIC%\Desktop'),
        r'C:\Users\Public\Desktop',
    ]

    # 优先匹配更精确的 Everything3D 3.1 / Everything3D
    found_any = None
    for root in roots:
        if not os.path.isdir(root):
            continue
        try:
            for p in glob.glob(os.path.join(root, '**', '*.lnk'), recursive=True):
                base = os.path.basename(p).lower()
                if 'everything3d 3.1' in base:
                    return p
                elif 'everything3d' in base:
                    return p
                elif 'e3d' in base and found_any is None:
                    found_any = p
        except Exception:
            continue

    return found_any

