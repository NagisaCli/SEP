@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================================
echo   SEP — E3D 项目管理系统 - 打包程序
echo ============================================================
echo.

:: 检查 Python
python -c "exit(0)" >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 需要 Python 环境
    pause
    exit /b 1
)

:: 安装 PyInstaller
echo [1/3] 检查 PyInstaller...
pip show pyinstaller >nul 2>&1
if %errorlevel% neq 0 (
    echo   正在安装 PyInstaller...
    pip install pyinstaller
    if %errorlevel% neq 0 (
        echo [错误] PyInstaller 安装失败
        pause
        exit /b 1
    )
)
echo   ✓ PyInstaller 已就绪

:: 清理旧构建
echo.
echo [2/3] 清理旧构建...
if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
if exist SEP.spec del /f SEP.spec
echo   ✓ 清理完成

:: 打包
echo.
echo [3/3] 开始打包...
echo   (通常需要 1-3 分钟)
echo.

pyinstaller --onefile --windowed ^
    --name "SEP" ^
    --icon "SEP.ico" ^
    --add-data "e3d_config.py;." ^
    --add-data "web_ui.html;." ^
    --hidden-import winreg ^
    --hidden-import e3d_util ^
    --hidden-import e3d_store ^
    --hidden-import e3d_scanner ^
    --hidden-import e3d_launcher ^
    --hidden-import e3d_web ^
    --hidden-import e3d_diag ^
    switch_e3d_project.py

if %errorlevel% neq 0 (
    echo.
    echo [错误] 打包失败
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   ✓ 构建成功!
echo ============================================================
echo.
echo   输出文件: dist\SEP.exe
echo.
echo   跨电脑分发时应包含:
echo     • SEP.exe                 (主程序)
echo     • 切换E3D项目文件夹.bat    (快捷启动)
echo     • e3d_projects.json        (自动生成)
echo     • e3d_paths.json           (自动生成)
echo.
echo   将上述文件复制到目标电脑同一文件夹即可使用。
echo   .exe 文件可独立运行，无需安装 Python。
echo.
echo   运行单元测试: python -m unittest discover tests -v
echo ============================================================
pause
