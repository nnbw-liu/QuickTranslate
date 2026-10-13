# make-icon.ps1 - 生成 QuickTranslate 的托盘/应用图标 (app.ico)
param([string]$OutPath = (Join-Path $PSScriptRoot '..\assets\app.ico'))

Add-Type -AssemblyName System.Drawing

function New-IcoFromPngs {
    param([System.Collections.Generic.List[object]]$Pngs, [string]$Path)
    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type: icon
    $bw.Write([UInt16]$Pngs.Count)       # count
    $offset = 6 + 16 * $Pngs.Count
    foreach ($p in $Pngs) {
        $s = $p[0]; $data = $p[1]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([Byte]$dim)            # width
        $bw.Write([Byte]$dim)            # height
        $bw.Write([Byte]0)               # color count
        $bw.Write([Byte]0)               # reserved
        $bw.Write([UInt16]1)             # planes
        $bw.Write([UInt16]32)            # bpp
        $bw.Write([UInt32]$data.Length)  # bytes in resource
        $bw.Write([UInt32]$offset)       # offset
        $offset += $data.Length
    }
    foreach ($p in $Pngs) { $bw.Write($p[1]) }
    $bw.Close(); $fs.Close()
}

$pngs = New-Object 'System.Collections.Generic.List[object]'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = New-Object System.Drawing.RectangleF(($s * 0.08), ($s * 0.08), ($s * 0.84), ($s * 0.84))
    $d = $s * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $r,
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235),
        [System.Drawing.Color]::FromArgb(255, 16, 185, 129),
        45)
    $g.FillPath($grad, $path)

    $font = New-Object System.Drawing.Font('Microsoft YaHei', ($s * 0.5), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    try {
        $g.DrawString('译', $font, [System.Drawing.Brushes]::White, $r, $sf)
    } catch {
        $g.FillEllipse([System.Drawing.Brushes]::White, ($s * 0.3), ($s * 0.25), ($s * 0.4), ($s * 0.4))
    }
    $font.Dispose(); $sf.Dispose(); $path.Dispose(); $grad.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs.Add(@($s, $ms.ToArray()))
    $g.Dispose(); $bmp.Dispose(); $ms.Dispose()
}

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
New-IcoFromPngs -Pngs $pngs -Path $OutPath
Write-Host "icon written: $OutPath"
