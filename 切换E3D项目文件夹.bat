@echo off
chcp 936 >nul
cd /d "%~dp0"

if exist SEP.exe (
    powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%~dp0SEP.exe'"
    exit /b
) else (
    python -c "exit(0)" >nul 2>&1
    if %errorlevel% neq 0 (
        echo [´íÎó] Î´ÕÒµ½ Python£¬Çë°²×° Python 3.x
        pause
        exit /b 1
    )
    python switch_e3d_project.py %*
)

pause
