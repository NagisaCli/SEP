# ⚡ SEP — Smart E3D Project Switcher

> 一键切换 AVEVA Everything3D 项目文件夹。**双击即用，零依赖。**

[![Platform](https://img.shields.io/badge/platform-Windows-blue)]()

---

## 为什么需要它？

E3D 的项目路径硬编码在 `evars.bat` / `evars.init` 的 `set projects_dir=` 行里。每次换项目都要手动编辑两个文件，路径又长，容易改错。更麻烦的是 **每台电脑 E3D 安装位置不同**，脚本写死路径就没法通用。

SEP 自动搞定一切——在哪台电脑都能用，双击打开浏览器就能管。

---

## 特性

- 🔍 **5 层自动检测** E3D 安装位置，换电脑无需配置
- 🌐 **Web GUI** 暗色主题界面，浏览器内操作，比命令行舒服十倍
- 📁 **统一项目库** 添加/删除/切换都在同一个列表里
- 🛡️ **安全第一** 改前自动备份，改后运行验证，失败自动还原
- 📦 **单文件分发** 一个 8MB 的 `.exe`，复制即用
- 🎨 **零依赖** 内嵌 HTTP 服务 + HTML/CSS/JS，不装任何框架

---

## 快速开始

1. 下载 `switch_e3d_project.exe`
2. 双击运行 → 自动打开浏览器
3. 首次自动检测 E3D，之后秒开

> 命令行模式：`switch_e3d_project.exe --cli`

---

## 检测策略

| 优先级 | 策略 | 原理 | 速度 |
|:---:|------|------|:---:|
| 1 | **注册表** | 读取 `HKLM\...\Uninstall` 中 AVEVA Everything3D 的 `InstallLocation` | ⚡ |
| 2 | 本地缓存 | 上次成功检测的结果 (`e3d_paths.json`) | ⚡ |
| 3 | Everything SDK | 调用 Everything 引擎搜索 `evars.bat` | ⚡ |
| 4 | 常见路径 | `D:/C:/E:/F:\AVEVA\Everything3D*\` | 🔵 |
| 5 | 全盘扫描 | 广度优先遍历 AVEVA 目录 | 🐢 |

注册表是最可靠的——安装程序写死的路径，不会有歧义。

---

## CLI 参考

```
switch_e3d_project.exe                 启动 Web GUI（默认）
switch_e3d_project.exe --cli           终端菜单模式
switch_e3d_project.exe --to <名称>     直接切换到指定项目
switch_e3d_project.exe --add           交互式添加项目
switch_e3d_project.exe --list          列出所有项目
switch_e3d_project.exe --remove        交互式删除项目
switch_e3d_project.exe --dry-run       仅查看当前状态
switch_e3d_project.exe --detect        强制重新检测 E3D
switch_e3d_project.exe --status        一行显示当前项目
```

---

## 从源码构建

```bash
pip install pyinstaller
pyinstaller --onefile --console --name switch_e3d_project --add-data "e3d_config.py;." --hidden-import winreg switch_e3d_project.py
# 输出: dist/switch_e3d_project.exe
```

或直接运行 `build.bat`。

---

## 文件结构

```
├── switch_e3d_project.exe   # 主程序 (PyInstaller 打包)
├── switch_e3d_project.py    # 源码
├── e3d_config.py            # E3D 路径自动检测模块
├── build.bat                # 打包脚本
├── req.txt                  # Python 依赖
└── e3d_projects.json        # 运行时自动生成的项目库
```

---

## 安全性

- 修改前自动创建 `.bak` 备份
- 修改后运行 `evars.bat` 验证无报错
- 验证失败 → 自动还原备份 → 文件毫发无损
- 只替换 `set projects_dir=` 后的路径部分，其余内容原封不动
