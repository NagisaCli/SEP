@echo off
chcp 65001 >nul
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell.exe -Command "Start-Process cmd.exe -ArgumentList '/c `"%~f0`"' -Verb RunAs"
    exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup_portproxy.ps1"
