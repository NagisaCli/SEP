@echo off
chcp 65001 >nul
cd /d "%~dp0"

if exist SEP.exe (
    powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%~dp0SEP.exe'"
    exit /b
) else (
    python -c "exit(0)" >nul 2>&1
    if %errorlevel% neq 0 (
        echo [错误] 未找到 Python，请安装 Python 3.x 或使用 SEP.exe
        pause
        exit /b 1
    )
    python switch_e3d_project.py %*
)

pause