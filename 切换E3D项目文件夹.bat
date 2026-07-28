@echo off
chcp 65001 >nul
cd /d "%~dp0"

if exist switch_e3d_project.exe (
    switch_e3d_project.exe %*
) else (
    python -c "exit(0)" >nul 2>&1
    if %errorlevel% neq 0 (
        echo [错误] 未找到 Python，请安装 Python 3.x
        pause
        exit /b 1
    )
    python switch_e3d_project.py %*
)

pause
