# ⚡ SEP — E3D 项目管理系统

AVEVA Everything3D 项目管理系统：扫描项目库、维护项目信息（分类 / 标签 / 描述 / 备注 / 状态 / 负责人）、管理“我的项目”、一键启动 E3D，不再手改配置文件。

## 两种项目配置机制

| 机制 | 修改对象 | 效果 | 适用 |
|---|---|---|---|
| 方式 A：整库载入 | `evars.bat` / `evars.init` 的 `projects_dir=` | projects_dir 指向项目库根目录，E3D 面板显示整库项目 | 项目服务器 / 项目集合 |
| 方式 B：项目追加 | 本地项目库 `custom_evars.bat` 末尾托管区 `call "路径\evarsXXX.bat"` | 在本地基础项目上追加 1~N 个项目 | 单项目启动、载入我的全部 |

## 路径识别规则

添加路径时自动判定：

- 路径是 `evarsXXX.bat` 文件 → **单项目**（XXX 即项目名，长度不限）；
- 目录内直接存在 `evarsXXX.bat` → **项目文件夹**（单项目）；
- 目录下一层的子文件夹内存在 `evarsXXX.bat` → **项目库**（collection），该子文件夹即项目文件夹；
- 下一层没有 `evarsXXX.bat` 的文件夹不视为项目文件夹；
- `custom_evars.bat` 中用户自己手写的 `call` 行仍会被解析为项目，但以下两类会被排除：
  - SEP 自己写入的托管区（`SEP MANAGED PROJECTS` 块）——否则启动某个远程项目后，
    重新扫描本地项目库会凭空多出指向其他路径库的「幽灵项目」；
  - `projects.bat` / `evars.bat` / `custom_evars.bat` 等基础设施文件（变量展开后再判断，
    因此 `%projects_dir%projects.bat` 这类写法同样能被正确排除）；
- 支持本地路径、`\\服务器\共享`、`smb://服务器/共享`；
- `http(s)://` 已预留扩展窗口，本版本暂不支持。

## 项目管理功能

- **分类**：自定义分类及颜色，项目可归入一个分类；
- **标签**：每个项目最多 20 个标签，支持搜索筛选；
- **项目信息**：显示名称、负责人、描述、内部备注、状态（进行中 / 已完成 / 暂停 / 归档）；
- **我的项目**：独立管理页，支持单项目启动、全部载入并启动（本地 custom_evars 托管区）、批量移除；
- **批量编辑**：项目管理 / 我的项目 / 单个路径库内均可勾选多个项目，批量修改分类、状态、负责人、标签、描述、备注；
- **路径库折叠**：路径库默认收起，可单独展开/收起，也可一键全部展开或收起（状态本地记忆）；
- **概览页**：项目总数、我的项目、路径库、分类数、分类 / 状态分布、最近项目；
- **筛选**：按名称 / 路径 / 负责人 / 标签搜索，按分类、状态、标签筛选，或只看“我的项目”；
- 元信息按项目 ID 单独保存，重新扫描路径库不会丢失。

## 诊断与修复

- 每个路径库卡片提供「诊断」按钮，设置页可「诊断全部路径库」；
- 本地路径检查：存在性、读取 / 写入权限、evars 项目文件；
- 网络（SMB/UNC）检查：共享路径可达、主机名解析、SMB 445 端口、共享访问、来宾登录策略（AllowInsecureGuestAuth）、SMB 客户端服务、项目文件发现；
- 常见问题预案：本机不支持 Guest 访问远程库时，报告会给出启用 `AllowInsecureGuestAuth` 的修复步骤与命令，界面可一键复制，或「尝试修复」自动执行（需管理员权限）；
- 其他错误（找不到网络路径、凭据错误、连接冲突等）会显示明确的系统错误说明和对应处理建议。

## 静默启动与关闭

- 打包为无控制台模式（windowed），双击 `SEP.exe` 或启动批处理不会弹出命令行窗口；
- 面板右上角「退出」静默关闭程序。

## 快速开始

1. 双击 `SEP.exe`（或 `切换E3D项目文件夹.bat`）→ 自动打开浏览器启动面板；
2. 在「路径库」中输入项目库根目录（或项目文件夹 / 项目 bat 文件路径），点「识别并添加」；
3. 在「项目管理」中为项目分类、打标签、填写信息；
4. 把需要的项目「添加到我的」；
5. 选择启动方式：
   - **启动**：单项目启动，E3D 面板只显示这一个项目；
   - **临时载入**：加载单个项目但不加入我的项目；
   - **载入全部并启动**：加载我的全部项目；
   - **整库载入**：方式 A，面板显示整个路径库的项目。

启动通过开始菜单 `AVEVA Everything3D 3.1.lnk`，不弹出命令行窗口。GUI 模式会自动隐藏控制台。

## CLI 参考

> 打包后的 `SEP.exe` 为无控制台模式，CLI 命令请在源码目录使用 `python switch_e3d_project.py` 运行。

```
python switch_e3d_project.py           启动 Web 面板（默认）
python switch_e3d_project.py --status  显示当前状态
python switch_e3d_project.py --scan <路径>
python switch_e3d_project.py --lib add <路径>
python switch_e3d_project.py --lib list
python switch_e3d_project.py --lib remove <ID>
python switch_e3d_project.py --lib rescan <ID>
python switch_e3d_project.py --my add <项目ID>
python switch_e3d_project.py --my list
python switch_e3d_project.py --my remove <ID>
python switch_e3d_project.py --my clear
python switch_e3d_project.py --launch <名称或ID>
python switch_e3d_project.py --launch-all
python switch_e3d_project.py --load <名称或ID>
python switch_e3d_project.py --launch-lib <库ID>
python switch_e3d_project.py --cli
python switch_e3d_project.py --detect
python switch_e3d_project.py --diag-e3d   全面诊断 E3D 配置文件与环境
python switch_e3d_project.py --fix-e3d    一键安全修复 E3D 配置文件死链与阻塞项
python switch_e3d_project.py --plugin list            列出 D:\AVEVA\Plugins 插件与状态
python switch_e3d_project.py --plugin enable <名称>   启用指定插件
python switch_e3d_project.py --plugin disable <名称>  禁用指定插件
python switch_e3d_project.py --plugin reindex [名称]  重构插件 pml.index 索引
python switch_e3d_project.py --clean-userdata         清理 USERDATA 临时缓存与死锁
python switch_e3d_project.py --fix-cad-fonts          一键修复 AutoCAD 缺失字体弹窗
```

旧命令 `--add` / `--remove` / `--list` 继续兼容。

## 文件结构

```
├── SEP.exe                 # 主程序（PyInstaller 打包独立运行版）
├── switch_e3d_project.py   # 入口：CLI + Web 面板
├── e3d_config.py           # E3D 安装路径自动检测
├── e3d_util.py             # 路径规范化 / 编码 / 超时工具
├── e3d_store.py            # 数据层（schema v3 + 旧数据迁移）
├── e3d_scanner.py          # 路径分类与项目扫描
├── e3d_launcher.py         # 配置写入 / 验证 / .lnk 启动
├── e3d_web.py              # 内嵌 HTTP 服务
├── e3d_plugin.py           # E3D 插件管理与 pml.index 索引重构引擎
├── e3d_diag.py             # 网络 / 路径 / E3D 配置 / USERDATA 诊断与修复
├── fix_cad_fonts.ps1       # AutoCAD 缺失字体一键静默修复脚本
├── web_ui.html             # 项目与插件管理面板页面
├── e3d_projects.json       # 运行时自动生成的项目数据
├── e3d_paths.json          # E3D 路径检测缓存（自动生成）
├── build.bat               # 打包脚本
└── tests/                  # 单元测试
```

## 数据与安全

- 配置写入前自动备份，写入后回读验证，失败自动还原；
- `custom_evars.bat` 只操作标记托管区，用户原有内容原样保留；
- 重复写入幂等，不会累积重复 `call`；
- 支持 GBK / UTF-8 编码的 bat 文件，中文路径不乱码；
- 旧版 `e3d_projects.json`（v1 / v2）首次启动自动迁移到 v3，分类与项目信息自动补齐。

## 构建与测试

```bash
build.bat                          # 打包 dist\SEP.exe
python -m unittest discover tests -v   # 运行单元测试
```
