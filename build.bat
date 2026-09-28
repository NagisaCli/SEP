@echo off
rem Builds dist\SEP.exe (self-contained, runs on any 64-bit Windows 10/11 PC) and the one-click installer package.
rem Pass -FrameworkDependent for a small exe that needs the .NET 10 Desktop Runtime installed.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if %errorlevel% neq 0 (
    pause
    exit /b %errorlevel%
)
pause
