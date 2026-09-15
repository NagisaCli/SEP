Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SEP — AVEVA E3D Native Client Build (.NET 10)" -ForegroundColor Cyan
Write-Host "============================================================"
Write-Host ""

$dotnetVer = dotnet --version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Error: .NET SDK not found."
    exit 1
}

Write-Host "[1/2] Building Windows 11 Fluent Native App (WPF-UI)..." -ForegroundColor Yellow
if (-not (Test-Path dist)) { New-Item -ItemType Directory -Path dist | Out-Null }

dotnet publish SEP.App\SEP.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if ($LASTEXITCODE -ne 0) {
    Write-Error "Error: Build failed."
    exit 1
}

Write-Host "[2/2] Syncing executable to root..." -ForegroundColor Yellow
Copy-Item -Force dist\SEP.exe .\SEP.exe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  Build succeeded! (Single-file size ~7.2MB, instant startup)" -ForegroundColor Green
Write-Host "============================================================"
Write-Host ""
Write-Host "  Output:"
Write-Host "    - SEP.exe (Root executable)"
Write-Host "    - dist\SEP.exe (Distribution archive)"
Write-Host ""
