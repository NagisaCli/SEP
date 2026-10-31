param(
    # Smaller exe that needs the .NET 10 Desktop Runtime on every PC. The default build carries the runtime
    # inside SEP.exe, so it starts on any 64-bit Windows 10/11 PC without installing anything first.
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SEP - AVEVA E3D Project Manager build (.NET 10)" -ForegroundColor Cyan
Write-Host "============================================================"
Write-Host ""

$null = & dotnet --version
if ($LASTEXITCODE -ne 0) {
    Write-Error "The .NET 10 SDK was not found (https://dotnet.microsoft.com/download)."
    exit 1
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$kind = if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }

Write-Host "[1/3] Publishing SEP.exe ($kind, single file)..." -ForegroundColor Yellow
if (Test-Path dist) {
    # loose files of older multi-file builds would otherwise sit next to the single-file exe (backups are kept)
    Get-ChildItem -Path dist\* -File -Include *.dll, SEP.deps.json, SEP.runtimeconfig.json, *.pdb | Remove-Item -Force
}
New-Item -ItemType Directory -Force -Path dist | Out-Null

$publishArgs = @(
    'publish', 'SEP.App\SEP.App.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $selfContained,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    # Pre-compiled (ReadyToRun) code: WPF, WPF-UI and the MVVM toolkit are no longer JIT-compiled on every start,
    # which is most of the cold-start time of a self-contained exe. The exe grows, mostly absorbed by compression.
    '-p:PublishReadyToRun=true',
    '-p:DebugType=embedded',
    '-o', 'dist'
)
if (-not $FrameworkDependent) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

Write-Host "[2/3] Writing the installer package..." -ForegroundColor Yellow
$version = (Get-Item dist\SEP.exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$installCmd = @'
@echo off
rem One-click install of SEP for the current Windows user (no administrator rights needed):
rem copies SEP.exe to %LOCALAPPDATA%\Programs\SEP, adds Start menu and desktop shortcuts and an
rem "Apps & features" entry, then starts SEP. Running it again updates an existing installation.
"%~dp0SEP.exe" --install
'@
Set-Content -Path dist\Install-SEP.cmd -Value $installCmd -Encoding ASCII
$readme = @"
SEP $version - AVEVA E3D Project Manager

Install:    double-click Install-SEP.cmd (per user, no administrator rights needed).
            Running it again installs an update in place.
Portable:   SEP.exe also runs directly from this folder or a network share.
Uninstall:  Windows Settings > Apps > Installed apps > SEP, or run: SEP.exe --uninstall
Data:       %APPDATA%\SEP (libraries, projects, settings) - kept on uninstall and update.
Log:        %LOCALAPPDATA%\SEP\sep_debug.log
"@
Set-Content -Path dist\README.txt -Value $readme -Encoding ASCII

$zip = "dist\SEP-$version-setup.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path dist\SEP.exe, dist\Install-SEP.cmd, dist\README.txt -DestinationPath $zip

Write-Host "[3/3] Syncing executable to the repository root..." -ForegroundColor Yellow
try { Copy-Item -Force dist\SEP.exe .\SEP.exe } catch { Write-Warning "SEP.exe in the repository root is in use and was not replaced." }

$sizeMb = [math]::Round((Get-Item dist\SEP.exe).Length / 1MB, 1)
Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  Build succeeded: SEP $version ($kind, $sizeMb MB)" -ForegroundColor Green
Write-Host "============================================================"
Write-Host ""
Write-Host "  dist\SEP.exe                     the application"
Write-Host "  dist\Install-SEP.cmd             one-click per-user install / update"
Write-Host "  dist\SEP-$version-setup.zip      both, ready to hand out"
Write-Host ""
