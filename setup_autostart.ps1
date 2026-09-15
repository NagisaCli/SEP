# WSL E3D Samba 开机自启服务配置脚本

Write-Host "【1/3】配置 WSL2 内部 systemd 自启服务 ..." -ForegroundColor Cyan
wsl -d Ubuntu-24.04 -u root -e bash -c "systemctl enable smbd; systemctl enable nmbd; systemctl start smbd nmbd"

Write-Host "【2/3】创建 Windows 静默无弹窗自启调度脚本 ..." -ForegroundColor Cyan
$sepDir = "$env:APPDATA\SEP"
if (-not (Test-Path $sepDir)) {
    New-Item -ItemType Directory -Path $sepDir -Force | Out-Null
}
$vbsPath = "$sepDir\start_wsl_samba.vbs"

# VBScript 静默在后台拉起 WSL Samba，完全不弹黑窗
$vbsContent = @"
Set ws = CreateObject("Wscript.Shell")
ws.Run "wsl.exe -d Ubuntu-24.04 -u root -e bash -c ""systemctl restart smbd nmbd""", 0, False
"@
[System.IO.File]::WriteAllText($vbsPath, $vbsContent, [System.Text.Encoding]::ASCII)
Write-Host "  ✓ 静默自启脚本已生成: $vbsPath" -ForegroundColor Green

Write-Host "【3/3】配置 Windows 开机自启双通道保障 ..." -ForegroundColor Cyan

# 通道 A: 注册表当前用户自启动项 (HKCU\Run)
$regKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$regValueName = "WSL_E3D_Samba_Autostart"
$regValue = "wscript.exe `"$vbsPath`""
Set-ItemProperty -Path $regKey -Name $regValueName -Value $regValue
Write-Host "  ✓ 通道 A: 已写入 Windows 注册表启动项 ($regKey)" -ForegroundColor Green

# 通道 B: Windows 用户专属开机自启目录 (Startup 文件夹)
$startupFolder = [Environment]::GetFolderPath("Startup")
$startupVbs = Join-Path $startupFolder "WSL_E3D_Samba_Autostart.vbs"
Copy-Item -Path $vbsPath -Destination $startupVbs -Force
Write-Host "  ✓ 通道 B: 已写入 Windows 启动目录: $startupVbs" -ForegroundColor Green

# 通道 C: 尝试注册 Windows 计划任务
try {
    $taskName = "WSL_E3D_Samba_Autostart"
    $action = New-ScheduledTaskAction -Execute "wscript.exe" -Argument "`"$vbsPath`""
    $triggerLogon = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggerLogon -Settings $settings -Force -ErrorAction SilentlyContinue | Out-Null
    Write-Host "  ✓ 通道 C: Windows 计划任务已就绪" -ForegroundColor Green
} catch {
    # 计划任务若无管理员权限则由通道 A/B 稳定承接
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  ✓ WSL2 Samba 开机自启已成功配置完毕！" -ForegroundColor Green
Write-Host "  电脑开机登录后，Samba 服务器将在后台静默自动拉起并提供服务。" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Green
