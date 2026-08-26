@echo off
chcp 65001 >nul
echo 正在检测并挂载 WSL 项目共享 (Z:)...

for /f "tokens=1" %%i in ('wsl -d Ubuntu-24.04 -e hostname -I') do set WSL_IP=%%i

if "%WSL_IP%"=="" (
    echo [错误] 无法获取 WSL IP 地址，请确认 WSL 是否已安装并正常运行。
    pause
    exit /b 1
)

echo 当前 WSL IP: %WSL_IP%
net use Z: /delete /y >nul 2>&1
net use Z: \\%WSL_IP%\e3d_share 25812 /user:yhl /persistent:yes

if %ERRORLEVEL% equ 0 (
    echo [成功] 已成功挂载 WSL 项目共享至 Z: 盘！
    echo 正在打开 Z: 盘...
    explorer Z:\
) else (
    echo [失败] 挂载失败，请检查网络或 Samba 服务。
    pause
)
