# ============================================================
# AutoCAD 缺失字体一键静默修复脚本 (Fix CAD Fonts)
# 作用:
#   1. 自动定位 AutoCAD 2025 / 历史版本的 Fonts 与 Support 目录；
#   2. 自动生成常用缺失大字体与西文字体别名 (hzfs, hzdx, @~!hztxt, superos, roma 等)；
#   3. 全面更新 acad.fmp 字体映射表，强制将缺失字体静默映射为 gbcbig.shx / simplex.shx；
#   4. 在 acaddoc.lsp 中设置 FONTALT=gbcbig.shx 与 FONTEVAL=0，彻底避免开图弹窗阻断与取消报错。
# ============================================================

$ErrorActionPreference = "Continue"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  AutoCAD 缺失字体一键静默修复与映射工具" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. 查找 AutoCAD 字体目录与 Support 目录
$fontCandidates = @(
    "D:\AutoCAD 2025\Fonts",
    "C:\Program Files\Autodesk\AutoCAD 2025\Fonts",
    "D:\Program Files\Autodesk\AutoCAD 2025\Fonts"
)
$fontDirs = @($fontCandidates | Where-Object { Test-Path $_ })

$supportCandidates = @(
    "$env:APPDATA\Autodesk\AutoCAD 2025\R25.0\chs\support",
    "$env:APPDATA\Autodesk\AutoCAD 2024\R24.3\chs\support",
    "$env:APPDATA\Autodesk\AutoCAD 2023\R24.2\chs\support",
    "$env:APPDATA\Autodesk\AutoCAD 2022\R24.1\chs\support",
    "$env:APPDATA\Autodesk\AutoCAD 2021\R24.0\chs\support",
    "D:\AutoCAD 2025\support"
)
$supportDirs = @($supportCandidates | Where-Object { Test-Path $_ })

if ($fontDirs.Count -eq 0) {
    Write-Host "[!] 未找到 AutoCAD Fonts 目录，请手动指定路径。" -ForegroundColor Yellow
} else {
    $cadFontsDir = $fontDirs[0]
    Write-Host "[+] 找到 AutoCAD 字体目录: $cadFontsDir" -ForegroundColor Green

    $gbcbig = Join-Path $cadFontsDir "gbcbig.shx"
    $hztxt = Join-Path $cadFontsDir "hztxt.shx"
    $simplex = Join-Path $cadFontsDir "simplex.shx"

    $baseBigFont = if (Test-Path $gbcbig) { $gbcbig } elseif (Test-Path $hztxt) { $hztxt } else { $null }
    $baseSimplex = if (Test-Path $simplex) { $simplex } else { $null }

    if ($baseBigFont) {
        $aliases = @{
            "hzfs.shx"      = $baseBigFont
            "HZDX.SHX"      = $baseBigFont
            "@~!hztxt.shx"  = $baseBigFont
            "tssdchn.shx"   = $baseBigFont
            "pkpm.shx"      = $baseBigFont
            "fs68.shx"      = $baseBigFont
            "fs.shx"        = $baseBigFont
            "ht68.shx"      = $baseBigFont
            "kt68.shx"      = $baseBigFont
            "hztxt.shx"     = $baseBigFont
        }
        if ($baseSimplex) {
            $aliases["SUPEROS.SHX"] = $baseSimplex
            $aliases["roma.shx"]    = $baseSimplex
            $aliases["tssdeng.shx"] = $baseSimplex
            $aliases["txt.shx"]     = $baseSimplex
        }

        foreach ($name in $aliases.Keys) {
            $target = Join-Path $cadFontsDir $name
            if (-not (Test-Path $target)) {
                try {
                    Copy-Item -Path $aliases[$name] -Destination $target -Force
                    Write-Host "  [✓] 生成字体副本: $name" -ForegroundColor DarkGreen
                } catch {
                    Write-Host "  [!] 无法创建 ${name}: $_" -ForegroundColor Yellow
                }
            } else {
                Write-Host "  [-] 已存在: $name" -ForegroundColor Gray
            }
        }
    }
}

# 2. 完善 acad.fmp 映射表
$fmpEntries = @(
    "hzfs;gbcbig.shx",
    "hzfs.shx;gbcbig.shx",
    "hzdx;gbcbig.shx",
    "hzdx.shx;gbcbig.shx",
    "HZDX.SHX;gbcbig.shx",
    "@~!hztxt;hztxt.shx",
    "@~!hztxt.shx;hztxt.shx",
    "hztxt;hztxt.shx",
    "hztxt.shx;gbcbig.shx",
    "hztxt_e;simplex.shx",
    "hztxt_e.shx;simplex.shx",
    "superos;simplex.shx",
    "superos.shx;simplex.shx",
    "SUPEROS.SHX;simplex.shx",
    "roma;simplex.shx",
    "roma.shx;simplex.shx",
    "FangSong_GB2312;simplex.shx",
    "FangSong_GB2312.shx;simplex.shx",
    "KaiTi_GB2312;simplex.shx",
    "KaiTi_GB2312.shx;simplex.shx",
    "@Arial Unicode MS;gbcbig.shx",
    "@Arial Unicode MS.shx;gbcbig.shx",
    "Arial Unicode MS;gbcbig.shx",
    "PC_TEXTSTYLE;gbcbig.shx",
    "YQ_DIM;gbcbig.shx",
    "hz2;gbcbig.shx",
    "hz;gbcbig.shx",
    "tssdchn;gbcbig.shx",
    "tssdchn.shx;gbcbig.shx",
    "tssdeng;simplex.shx",
    "tssdeng.shx;simplex.shx",
    "pkpm;gbcbig.shx",
    "pkpm.shx;gbcbig.shx",
    "fs68;gbcbig.shx",
    "fs68.shx;gbcbig.shx",
    "fs;gbcbig.shx",
    "fs.shx;gbcbig.shx",
    "ht68;gbcbig.shx",
    "ht68.shx;gbcbig.shx",
    "kt68;gbcbig.shx",
    "kt68.shx;gbcbig.shx",
    "txt;simplex.shx",
    "txt.shx;simplex.shx"
)

foreach ($sDir in $supportDirs) {
    $fmpFile = Join-Path $sDir "acad.fmp"
    if (Test-Path $fmpFile) {
        $existing = @(Get-Content $fmpFile -Encoding utf8)
        $newLines = [System.Collections.Generic.List[string]]::new()
        foreach ($l in $existing) {
            $newLines.Add($l)
        }
        $added = 0
        foreach ($entry in $fmpEntries) {
            $key = $entry.Split(';')[0].Trim().ToLower()
            $hasKey = $false
            foreach ($line in $existing) {
                if ($line.Split(';')[0].Trim().ToLower() -eq $key) {
                    $hasKey = $true
                    break
                }
            }
            if (-not $hasKey) {
                $newLines.Add($entry)
                $added++
            }
        }
        if ($added -gt 0) {
            Set-Content -Path $fmpFile -Value $newLines -Encoding utf8
            Write-Host "[+] 已更新 $fmpFile (新增 $added 条字体映射)" -ForegroundColor Green
        } else {
            Write-Host "[-] $fmpFile 映射已是最新" -ForegroundColor Gray
        }
    }

    # 3. 检查并注入 acaddoc.lsp 免阻断设置
    $lspFile = Join-Path $sDir "acaddoc.lsp"
    $lspCode = @"

;;; ============================================================
;;; CAD 缺失字体静默替用与抑制阻断设置 (Auto-fix Missing Fonts)
;;; ============================================================
(vl-catch-all-apply 'setvar (list "FONTALT" "gbcbig.shx"))
(vl-catch-all-apply 'setvar (list "FONTEVAL" 0))
"@
    if (Test-Path $lspFile) {
        $lspContent = Get-Content $lspFile -Raw -Encoding utf8
        if ($lspContent -notmatch "FONTALT") {
            Add-Content -Path $lspFile -Value $lspCode -Encoding utf8
            Write-Host "[+] 已在 $lspFile 注入 FONTALT / FONTEVAL 静默设置" -ForegroundColor Green
        }
    }
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  修复完成！现在重新打开 CAD 图纸将不再提示缺失字体。" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Cyan
