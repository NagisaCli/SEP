[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "       WSL2 局域网物理桥接模式 (Bridged Mode) 配置向导" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# 1. 查找物理网卡
$eth = Get-NetAdapter -Name "以太网" -ErrorAction SilentlyContinue
if (-not $eth) {
    $eth = Get-NetAdapter -Name "Ethernet" -ErrorAction SilentlyContinue
}
if (-not $eth) {
    $eth = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual -and $_.InterfaceDescription -notlike "*Tunnel*" -and $_.InterfaceDescription -notlike "*singbox*" -and $_.InterfaceDescription -notlike "*Tailscale*" } | Select-Object -First 1
}

if (-not $eth) {
    Write-Host "[错误] 未能找到活动的物理以太网卡。" -ForegroundColor Red
    Get-NetAdapter | Format-Table Name, InterfaceDescription, Status
    pause
    exit
}

$adapterName = $eth.Name
Write-Host "【1/4】锁定物理网卡: $adapterName ($($eth.InterfaceDescription))" -ForegroundColor Green

$switchName = "WSLBridge"

# 2. 检查并安装 Hyper-V 模块支持 (如果尚未安装)
Write-Host "【2/4】检查 Hyper-V 虚拟交换机模块与组件 ..." -ForegroundColor Yellow
if (-not (Get-Command New-VMSwitch -ErrorAction SilentlyContinue)) {
    Write-Host "正在启用 Hyper-V 管理支持组件 (无需重启系统)..." -ForegroundColor Yellow
    dism.exe /online /enable-feature /featurename:Microsoft-Hyper-V-Management-PowerShell /all /norestart | Out-Null
    dism.exe /online /enable-feature /featurename:Microsoft-Hyper-V-Services /all /norestart | Out-Null
    Import-Module Hyper-V -ErrorAction SilentlyContinue
}

# 3. 创建纯英文名 External 虚拟交换机
Write-Host "【3/4】正在为物理网卡绑定外部虚拟交换机: $switchName ..." -ForegroundColor Yellow
try {
    # 如果已存在先复用
    $existing = Get-NetAdapter | Where-Object { $_.Name -like "*$switchName*" }
    if (-not $existing) {
        New-VMSwitch -Name $switchName -NetAdapterName $adapterName -AllowManagementOS $true -ErrorAction Stop | Out-Null
        Write-Host "虚拟交换机 $switchName 创建成功！" -ForegroundColor Green
    } else {
        Write-Host "虚拟交换机 $switchName 已就绪！" -ForegroundColor Green
    }
} catch {
    Write-Host "通过标准命令创建虚拟交换机..." -ForegroundColor Yellow
    # 尝试备用 HNS 创建
    try {
        $hnsJson = "{`"Type`":`"Transparent`",`"Name`":`"$switchName`",`"NetworkAdapterName`":`"$adapterName`"}"
        $tmpFile = "$env:TEMP\hns_bridge.json"
        Set-Content -Path $tmpFile -Value $hnsJson -Encoding ASCII
        hnsdiag.exe create network $tmpFile | Out-Null
        Remove-Item -Path $tmpFile -Force -ErrorAction SilentlyContinue
    } catch {}
}

# 4. 写入 .wslconfig
Write-Host "【4/4】写入 $HOME\.wslconfig 配置文件 ..." -ForegroundColor Yellow
$wslConfig = "[wsl2]`nnetworkingMode=bridged`nvmSwitch=$switchName`n"
[System.IO.File]::WriteAllText("$HOME\.wslconfig", $wslConfig, [System.Text.Encoding]::ASCII)
Write-Host ".wslconfig 写入成功！" -ForegroundColor Green

Write-Host "正在重启 WSL2 虚拟机以生效物理桥接 ..." -ForegroundColor Yellow
wsl --shutdown
Start-Sleep -Seconds 3
wsl -u root -d Ubuntu-24.04 -e bash -c "systemctl restart smbd; systemctl restart nmbd"

Write-Host ""
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "                     桥接生效结果" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$wslIp = (wsl -d Ubuntu-24.04 -e hostname -I 2>$null)
if ($wslIp) {
    $wslIp = $wslIp.Trim().Split()[0]
}

if ($wslIp -and $wslIp -notlike "172.22.*" -and $wslIp -notlike "127.*") {
    Write-Host "🎉 物理桥接成功！" -ForegroundColor Green
    Write-Host "WSL 独立局域网 IP:   $wslIp" -ForegroundColor Green
    Write-Host "局域网其他电脑访问路径: \\$wslIp\e3d_share" -ForegroundColor Cyan
    Write-Host "局域网主机名访问路径:   \\WSLE3D\e3d_share" -ForegroundColor Cyan
} else {
    Write-Host "WSL 当前 IP: $wslIp" -ForegroundColor Yellow
    Write-Host "访问路径: \\$wslIp\e3d_share 或 \\WSLE3D\e3d_share" -ForegroundColor Cyan
    Write-Host "（若提示需要重启电脑以完成 Hyper-V 驱动加载，可重启一次电脑即可生效）" -ForegroundColor Gray
}

Write-Host ""
Write-Host "按任意键退出..."
[Console]::ReadKey($true) | Out-Null
