[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "      WSL2 Samba 局域网端口转发配置向导" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# 1. 确认 WSL 正在运行并获取 IP
$wslIp = (wsl -d Ubuntu-24.04 -e hostname -I 2>$null)
if ($wslIp) {
    $wslIp = $wslIp.Trim().Split()[0]
}

if (-not $wslIp) {
    Write-Host "[提示] 正在唤醒 WSL ..." -ForegroundColor Yellow
    wsl -d Ubuntu-24.04 -e bash -c "systemctl restart smbd; systemctl restart nmbd"
    $wslIp = (wsl -d Ubuntu-24.04 -e hostname -I).Trim().Split()[0]
}

if (-not $wslIp) {
    Write-Host "[错误] 无法获取 WSL IP，请确认 WSL 是否正常安装。" -ForegroundColor Red
    pause
    exit
}

Write-Host "【1/3】已获取 WSL 当前 IP: $wslIp" -ForegroundColor Green

# 2. 让出 Windows 本机的 445 端口
Write-Host "【2/3】正在让出 Windows 本机 445 端口 (停止 LanmanServer 服务)..." -ForegroundColor Yellow
Stop-Service -Name LanmanServer -Force -ErrorAction SilentlyContinue
Set-Service -Name LanmanServer -StartupType Disabled -ErrorAction SilentlyContinue
Write-Host "Windows 原生 SMB 端口已释放！" -ForegroundColor Green

# 3. 添加端口转发与防火墙放行
Write-Host "【3/3】正在配置端口转发与防火墙规则 (0.0.0.0:445 -> $wslIp:445) ..." -ForegroundColor Yellow
netsh interface portproxy delete v4tov4 listenport=445 listenaddress=0.0.0.0 | Out-Null
netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=445 connectaddress=$wslIp connectport=445

netsh advfirewall firewall delete rule name="WSL_Samba_445" | Out-Null
netsh advfirewall firewall add rule name="WSL_Samba_445" dir=in action=allow protocol=TCP localport=445 | Out-Null
Write-Host "端口转发与防火墙规则已生效！" -ForegroundColor Green

# 4. 获取本机局域网物理 IP
$hostIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -like '*Ethernet*' -or $_.InterfaceAlias -like '*以太网*' -or $_.InterfaceAlias -like '*WLAN*' -or $_.InterfaceAlias -like '*Wi-Fi*' } | Select-Object -First 1).IPAddress
if (-not $hostIp) {
    $hostIp = "192.168.2.64"
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "                  🎉 局域网访问已开放！" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "局域网内的任何其他电脑，现在直接在资源管理器地址栏输入：" -ForegroundColor White
Write-Host ""
Write-Host "    \\$hostIp\e3d_share" -ForegroundColor Green
Write-Host ""
Write-Host "即可直接进入并协同访问本项目！" -ForegroundColor Cyan
Write-Host ""
Write-Host "按任意键退出..."
[Console]::ReadKey($true) | Out-Null
