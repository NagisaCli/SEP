@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================================
echo   E3D 项目切换工具 - 打包构建
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
if exist switch_e3d_project.spec del /f switch_e3d_project.spec
echo   ✓ 清理完成

:: 构建
echo.
echo [3/3] 开始打包...
echo   (可能需要 1-3 分钟)
echo.

pyinstaller --onefile --console ^
    --name "switch_e3d_project" ^
    --add-data "e3d_config.py;." ^
    --hidden-import winreg ^
    switch_e3d_project.py

if %errorlevel% neq 0 (
    echo.
    echo [错误] 打包失败
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   ✓ 打包完成!
echo ============================================================
echo.
echo   输出文件: dist\switch_e3d_project.exe
echo.
echo   跨环境分发包应包含:
echo     • switch_e3d_project.exe   ^(主程序^)
echo     • 切换E3D项目文件夹.bat    ^(启动器^)
echo     • e3d_projects.json        ^(自动生成^)
echo     • e3d_paths.json           ^(自动生成^)
echo.
echo   将以上文件复制到目标电脑任一文件夹即可使用。
echo   .exe 文件可独立运行，无需安装 Python。
echo ============================================================
pause
