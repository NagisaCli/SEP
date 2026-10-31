<#
.SYNOPSIS
    Draws SEP's E3D ribbon icons and writes them to SEP.PluginPanel\Assets\SEPIcons.resources.

.DESCRIPTION
    E3D's ribbon (CAF) resolves an ImageResourceId "SEPIcons:ID_SEP_BAR" to the icon "ID_SEP_BAR" in
    SEPIcons.resources next to des.exe; SEP deploys the file together with SEP.PluginPanel.dll. CAF reads it with
    the .NET Framework ResourceManager (Icon objects, BinaryFormatter), so this runs in Windows PowerShell 5.1,
    not pwsh. Every icon is drawn at each size (no scaling blur) and stored as one multi-size icon.

        powershell -NoProfile -File scripts\Build-RibbonIcons.ps1 [-PreviewDir <folder>]

    -PreviewDir also writes a PNG per icon and size, to look at them.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\SEP.PluginPanel\Assets\SEPIcons.resources'),
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this with Windows PowerShell 5.1 (powershell.exe): CAF needs .NET Framework resources.' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64

function Color([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush (Color $hex); $b.Freeze(); $b }
function Gradient([string[]]$stops) {
    $g = New-Object System.Windows.Media.LinearGradientBrush
    $g.StartPoint = '0,0'; $g.EndPoint = '1,1'
    for ($i = 0; $i -lt $stops.Count; $i++) { $g.GradientStops.Add((New-Object System.Windows.Media.GradientStop (Color $stops[$i]), ($i / [Math]::Max(1, $stops.Count - 1)))) }
    $g.Freeze(); $g
}

# a rounded tile with a soft top sheen: the large buttons
function Draw-Tile($dc, [double]$s, $fill) {
    $r = [Math]::Max(2.5, $s * 0.22)
    $inset = [Math]::Max(0.5, $s * 0.03)
    $rect = New-Object System.Windows.Rect $inset, $inset, ($s - 2 * $inset), ($s - 2 * $inset)
    $dc.DrawRoundedRectangle($fill, $null, $rect, $r, $r)
    $sheen = New-Object System.Windows.Media.LinearGradientBrush ((Color '#38FFFFFF'), (Color '#00FFFFFF'), 90)
    $dc.DrawRoundedRectangle($sheen, $null, (New-Object System.Windows.Rect $inset, $inset, ($s - 2 * $inset), (($s - 2 * $inset) * 0.55)), $r, $r)
}

function Draw-Glyph($dc, [double]$s, [string]$glyph, $brush, [double]$scale = 0.86, [string]$font = 'Segoe MDL2 Assets', [string]$weight = 'Normal') {
    $tf = New-Object System.Windows.Media.Typeface ((New-Object System.Windows.Media.FontFamily $font), [System.Windows.FontStyles]::Normal, [System.Windows.FontWeight]::FromOpenTypeWeight($(if ($weight -eq 'Black') { 900 } elseif ($weight -eq 'Bold') { 700 } else { 400 })), [System.Windows.FontStretches]::Normal)
    $ft = New-Object System.Windows.Media.FormattedText ($glyph, [Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $tf, ($s * $scale), $brush, 1.0)
    $geo = $ft.BuildGeometry((New-Object System.Windows.Point 0, 0))
    $b = $geo.Bounds
    $tx = [Math]::Round(($s - $b.Width) / 2 - $b.X)
    $ty = [Math]::Round(($s - $b.Height) / 2 - $b.Y)
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform $tx, $ty))
    $dc.DrawGeometry($brush, $null, $geo)
    $dc.Pop()
}

$Teal = '#0E7490'

# id -> drawing (s = pixel size)
$Icons = [ordered]@{
    # the floating bar: a white strip with three plug-in tiles on SEP's gradient
    'ID_SEP_BAR' = {
        param($dc, $s)
        Draw-Tile $dc $s (Gradient '#22C3E6', '#0891B2', '#4F46E5')
        $u = $s / 32.0
        $pill = New-Object System.Windows.Rect ([Math]::Round(5 * $u)), ([Math]::Round(11 * $u)), ([Math]::Round(22 * $u)), ([Math]::Max(5, [Math]::Round(10 * $u)))
        $dc.DrawRoundedRectangle((Brush '#F2FFFFFF'), $null, $pill, ($pill.Height / 2), ($pill.Height / 2))
        $d = [Math]::Max(2, [Math]::Round(4.4 * $u))
        $cy = $pill.Y + ($pill.Height - $d) / 2
        $colors = '#0891B2', '#7C3AED', '#DB2777'
        $gap = ($pill.Width - 3 * $d) / 4
        for ($i = 0; $i -lt 3; $i++) {
            $x = [Math]::Round($pill.X + $gap * ($i + 1) + $d * $i)
            $dc.DrawRoundedRectangle((Brush $colors[$i]), $null, (New-Object System.Windows.Rect $x, ([Math]::Round($cy)), $d, $d), ($d * 0.3), ($d * 0.3))
        }
    }
    # SEP itself: a bold S on deep blue
    'ID_SEP_APP' = {
        param($dc, $s)
        Draw-Tile $dc $s (Gradient '#1E3A8A', '#0F172A')
        Draw-Glyph $dc $s 'S' (Brush '#FFFFFF') 0.74 'Segoe UI' 'Black'
        $u = $s / 32.0
        if ($s -ge 20) { $dc.DrawEllipse((Brush '#22C3E6'), $null, (New-Object System.Windows.Point (24.5 * $u), (24.5 * $u)), (2.6 * $u), (2.6 * $u)) }
    }
    'ID_SEP_FIND'    = { param($dc, $s) Draw-Glyph $dc $s ([string][char]0xE721) (Brush $Teal) 0.82 }
    'ID_SEP_MANAGE'  = { param($dc, $s) Draw-Glyph $dc $s ([string][char]0xEA86) (Brush $Teal) 0.86 }
    'ID_SEP_REFRESH' = { param($dc, $s) Draw-Glyph $dc $s ([string][char]0xE72C) (Brush $Teal) 0.8 }
}

function Render([scriptblock]$draw, [int]$s) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetEdgeMode($dv, 'Unspecified')
    $dc = $dv.RenderOpen()
    & $draw $dc $s
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $rtb
}

# one ICO with a 32-bit DIB frame per size (CAF picks the frame with new Icon(icon, size, size))
function To-Ico($frames) {
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
    $blobs = foreach ($f in $frames) {
        $s = $f.PixelWidth
        $stride = $s * 4
        $px = New-Object byte[] ($stride * $s)
        $f.CopyPixels($px, $stride, 0)
        # Pbgra32 is premultiplied: icons want straight alpha
        for ($i = 0; $i -lt $px.Length; $i += 4) {
            $a = $px[$i + 3]
            if ($a -gt 0 -and $a -lt 255) { for ($c = 0; $c -lt 3; $c++) { $px[$i + $c] = [byte][Math]::Min(255, [Math]::Round($px[$i + $c] * 255.0 / $a)) } }
        }
        $b = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter $b
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2)); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]0); $bw.Write([UInt32]($stride * $s)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $stride, $stride) }
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskStride * $s)))
        $bw.Flush()
        , @($s, $b.ToArray())
    }
    $offset = 6 + 16 * $frames.Count
    foreach ($blob in $blobs) {
        $s = $blob[0]; $data = $blob[1]
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($blob in $blobs) { $w.Write([byte[]]$blob[1]) }
    $w.Flush()
    $ms.Position = 0
    $ms
}

$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
if ($PreviewDir) { New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null }

$writer = New-Object System.Resources.ResourceWriter $Output
# CAF checks a resource file by asking it for "Test"
$writer.AddResource('Test', 'SEP ribbon icons')
foreach ($id in $Icons.Keys) {
    $frames = foreach ($s in $Sizes) { Render $Icons[$id] $s }
    if ($PreviewDir) {
        foreach ($f in $frames) {
            $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
            $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($f))
            $fs = [IO.File]::Create((Join-Path $PreviewDir ("{0}_{1}.png" -f $id, $f.PixelWidth)))
            $enc.Save($fs); $fs.Close()
        }
    }
    $icon = New-Object System.Drawing.Icon (To-Ico $frames)
    $writer.AddResource($id, $icon)
}
$writer.Generate()
$writer.Close()
Write-Host "Wrote $Output ($((Get-Item $Output).Length) bytes, $($Icons.Count) icons)"
