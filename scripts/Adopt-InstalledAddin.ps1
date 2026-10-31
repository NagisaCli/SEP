# Makes an add-in that its own installer already put into the E3D folder a normal SEP plug-in, so it shows on the
# Plug-ins page and every E3D start keeps loading it, without SEP and that installer fighting over the DLL.
#
#   scripts\Adopt-InstalledAddin.ps1 -Name E3DLiveBridge -Addin E3DLiveBridge.Addin
#   ... -Uic C:\Muvsera\Projects\e3d_live\integrations\e3d_live\deployment\sep-plugin\E3DLiveBridge.uic
#
# -Uic copies the add-in's ribbon file into the plug-in folder; SEP merges it into its ribbon tab, so the add-in's
# own command (for example the button that opens its panel) appears there instead of SEP's "add-in is active" line.
#
# The plug-in's bin\<Addin>.dll is a hard link to the E3D folder's copy (one file, two names): the plug-in's own
# installer can keep copying new builds into the E3D folder (Copy-Item writes into the linked file), and SEP's add-in
# sync sees the same size and time and never puts an older copy back. Both paths must be on the same drive.
param(
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $Addin,
    [string] $E3dHome = 'D:\AVEVA\Everything3D3.1',
    [string] $PluginsDir = 'D:\AVEVA\Plugins',
    [string] $Sep,   # default: SEP.exe in the repository root (set below; Windows PowerShell 5.1 has no $PSScriptRoot here)
    [string] $Uic
)
$ErrorActionPreference = 'Stop'
if (-not $Sep) { $Sep = Join-Path (Split-Path $PSScriptRoot) 'SEP.exe' }
$installed = Join-Path $E3dHome "$Addin.dll"
if (-not (Test-Path $installed)) { throw "$installed not found: install the add-in with its own installer first." }
if (-not (Test-Path $Sep)) { throw "SEP not found at $Sep" }
$bin = Join-Path $PluginsDir "$Name\bin"
New-Item -ItemType Directory -Force -Path $bin | Out-Null
if ($Uic) {
    if (-not (Test-Path $Uic)) { throw "Ribbon file not found: $Uic" }
    Copy-Item $Uic (Join-Path $PluginsDir "$Name\$Name.uic") -Force
    "Ribbon file: $(Join-Path $PluginsDir "$Name\$Name.uic")"
}

function Test-SameFile([string] $a, [string] $b) {
    if (-not (Test-Path $b)) { return $false }
    $links = @(fsutil hardlink list $a 2>$null | ForEach-Object { $_.Trim().TrimStart('\') })
    $rel = (Resolve-Path $b).Path.Substring(3)
    return $links -contains $rel
}
foreach ($ext in '.dll', '.pdb') {
    $source = Join-Path $E3dHome "$Addin$ext"
    if (-not (Test-Path $source)) { continue }
    $link = Join-Path $bin "$Addin$ext"
    if (Test-SameFile $source $link) { "$link already links to $source"; continue }
    if (Test-Path $link) { Remove-Item $link -Force }
    New-Item -ItemType HardLink -Path $link -Target $source | Out-Null
    "Linked $link -> $source"
}
@(
    "# $Name (adopted by SEP scripts\Adopt-InstalledAddin.ps1, $(Get-Date -Format 'yyyy-MM-dd'))",
    '',
    "bin\$Addin.dll is a hard link to $installed, which the add-in's own installer maintains.",
    'Update the add-in with that installer; SEP keeps loading it. Removing this folder leaves the E3D copy in place.'
) | Set-Content -Encoding UTF8 (Join-Path $PluginsDir "$Name\INSTALL.md")

$work = Join-Path $env:LOCALAPPDATA 'SEP\adopt'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$request = Join-Path $work "enable-$Name.json"
$result = Join-Path $work "enable-$Name.result.json"
Remove-Item $result -ErrorAction SilentlyContinue
@{ plugins = @($Name); disable = @(); pluginsDir = $PluginsDir; e3dHome = $E3dHome; result = $result } | ConvertTo-Json | Set-Content -Encoding UTF8 $request
$p = Start-Process -FilePath $Sep -ArgumentList "--apply-plugins=`"$request`"" -PassThru -Wait
$r = if (Test-Path $result) { Get-Content -Raw $result | ConvertFrom-Json } else { $null }
"SEP exit code $($p.ExitCode): $($r.message)"
if ($r) { "  enabled: $($r.enabled -join ', ')"; if ($r.missing) { "  missing: $($r.missing -join ', ')" } }
"Still one file: $(Test-SameFile $installed (Join-Path $bin "$Addin.dll"))"
exit $p.ExitCode
