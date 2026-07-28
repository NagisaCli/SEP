================================================================================
  E3D 项目文件夹切换工具 (跨环境便携版)
================================================================================

功能
----
  自动检测 AVEVA Everything3D 安装位置 → 修改 evars.bat / evars.init
  中的项目文件夹路径，实现一键切换 E3D 工作项目。

  一个 .exe 走天下：双击即用，内置菜单，无需 .bat 启动器。
  (建议配合 .bat 以解决 Windows 控制台中文编码问题)

跨环境使用
----------
  只需复制 switch_e3d_project.exe 到目标电脑。


检测策略 (按优先级)
--------------------
  1. 注册表查找    — 搜索 HKLM Uninstall 中的 AVEVA Everything3D 条目
                      这是最可靠的方式，直接读取安装程序写入的路径
  2. 本地缓存      — 读取上次成功检测的结果 (e3d_paths.json)
  3. Everything SDK — 调用 Everything 搜索引擎极速定位 evars.bat
                      (需 Everything 正在运行)
  4. 常见路径      — 检查 D:/C:/E:/F:\AVEVA\Everything3D*\ 目录
  5. 全盘扫描      — 广度优先遍历所有盘符 (兜底方案，较慢)


使用方式
--------

  图形菜单:
    双击 "切换E3D项目文件夹.bat"
    菜单顶部显示当前项目，一目了然：

      [1] 切换项目 — 可用项目列表 + 内置 [A] 添加新项目
      [2] 添加项目 — 直接进入添加界面
      [3] 删除项目 — 删除已保存的项目
      [4] 列出项目 — 查看所有已保存项目
      [5] 重新检测 — 强制重新扫描 E3D 安装路径

    切换项目时列表自带 [A] 添加新项目选项，无需退回主菜单。

  命令行 (高级):
    switch_e3d_project.exe --dry-run        仅查看当前状态
    switch_e3d_project.exe --to 本地        直接切换到"本地"项目
    switch_e3d_project.exe --add            交互式添加项目
    switch_e3d_project.exe --list           列出所有项目
    switch_e3d_project.exe --detect         重新检测 E3D 路径


手动指定路径 (检测失败时)
-------------------------
  如果所有自动检测策略都失败，程序会提示手动输入：
    evars.bat  的完整路径: 如 D:\AVEVA\Everything3D3.1\evars.bat
    evars.init 的完整路径: 如 D:\AVEVA\Everything3D3.1\evars.init
  输入后会保存到缓存，下次无需重新输入。


项目管理
--------
  项目列表保存在 e3d_projects.json 中，可手动编辑。
  预设项目自动从 E3D 注册表检测，无需手动配置。


从源码构建 .exe
----------------
  1. 安装 Python 3.x
  2. pip install pyinstaller
  3. 运行 build.bat
  4. 输出: dist\switch_e3d_project.exe


文件说明
--------
  switch_e3d_project.exe   -- 主程序 (PyInstaller 打包)
  switch_e3d_project.py    -- Python 源码
  e3d_config.py            -- E3D 路径自动检测模块
  切换E3D项目文件夹.bat     -- 启动菜单
  build.bat                -- PyInstaller 打包脚本
  req.txt                  -- Python 依赖
  e3d_paths.json           -- (自动生成) E3D 路径缓存
  e3d_projects.json        -- (自动生成) 项目列表


安全性
------
  - 修改前自动创建 .bak 备份文件
  - 修改后运行 evars.bat 验证无报错
  - 验证失败自动还原备份
  - 仅修改 set projects_dir= 行的路径部分，不动其余内容
================================================================================
