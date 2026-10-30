# Renders the RemoteCraft mark (two rounded diamonds, red + orange) into a multi-size .ico.
# Keep in sync with Logo.Draw in src\RemoteCraft\UI\Theme.cs.
param([string]$Out = "$PSScriptRoot\..\src\RemoteCraft\Assets\RemoteCraft.ico")

Add-Type -AssemblyName System.Drawing

function New-Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Fill-Diamond($g, $brush, [float]$cx, [float]$cy, [float]$side, [float]$radius) {
    $path = New-Rounded (-$side / 2) (-$side / 2) $side $side $radius
    $state = $g.Save()
    $g.TranslateTransform($cx, $cy)
    $g.RotateTransform(45)
    $g.FillPath($brush, $path)
    $g.Restore($state)
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = $s * 0.50
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xEF, 0x44, 0x3B))
    $orange = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 0xF4, 0x7B, 0x2A))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    Fill-Diamond $g $red ($s * 0.36) ($s * 0.5) $side ($s * 0.07)
    Fill-Diamond $g $orange ($s * 0.64) ($s * 0.5) $side ($s * 0.07)
    Fill-Diamond $g $white ($s * 0.64) ($s * 0.5) ($side * 0.36) ($s * 0.02)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "Wrote $Out"
