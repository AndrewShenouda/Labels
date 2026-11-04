param([string]$Out = (Join-Path $PSScriptRoot 'AppIcon.ico'))
# Draws the app icon at several sizes and packs them into a PNG-compressed .ico.
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $s / 256.0
    # Squircle-ish background with a vertical blue gradient.
    $bg = RoundRect (8*$k) (8*$k) (240*$k) (240*$k) (56*$k)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 64, 156, 255)), ([System.Drawing.Color]::FromArgb(255, 0, 98, 230))
    $g.FillPath($grad, $bg)
    # Label sheet with a soft shadow.
    $shadow = RoundRect (70*$k) (50*$k) (116*$k) (166*$k) (12*$k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 0, 30, 90))), $shadow)
    $sheet = RoundRect (70*$k) (42*$k) (116*$k) (166*$k) (12*$k)
    $g.FillPath([System.Drawing.Brushes]::White, $sheet)
    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 29, 29, 31))
    $grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 199, 199, 204))
    if ($s -ge 32) {
        $g.FillRectangle($ink, 84*$k, 58*$k, 22*$k, 20*$k)
        $g.FillRectangle($grey, 114*$k, 60*$k, 56*$k, 6*$k)
        $g.FillRectangle($grey, 114*$k, 71*$k, 38*$k, 6*$k)
        $g.FillRectangle($ink, 84*$k, 90*$k, 88*$k, 3*$k)
        $g.FillRectangle($grey, 84*$k, 104*$k, 50*$k, 6*$k)
        $g.FillRectangle($grey, 84*$k, 116*$k, 72*$k, 6*$k)
        $g.FillRectangle($ink, 84*$k, 136*$k, 88*$k, 3*$k)
    }
    if ($s -lt 32) {
        # Tiny sizes: a clean sheet with two rules reads better than detail.
        $g.FillRectangle($ink, 88*$k, 70*$k, 80*$k, 14*$k)
        $g.FillRectangle($grey, 88*$k, 110*$k, 80*$k, 12*$k)
        $g.FillRectangle($grey, 88*$k, 140*$k, 56*$k, 12*$k)
        $g.Dispose(); return $bmp
    }
    # Barcode.
    $x = 84.0; $widths = 4, 2, 6, 2, 3, 5, 2, 4, 2, 6, 3, 2, 4
    $top = 150
    foreach ($w in $widths) {
        if ($x + $w -gt 172) { break }
        $g.FillRectangle($ink, [float]($x*$k), [float]($top*$k), [float]([Math]::Max(1, $w*$k)), [float]((192 - $top)*$k))
        $x += $w + 3
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
# Also keep a large PNG for previews.
$big = Draw 512; $big.Save((Join-Path (Split-Path $Out) 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
"wrote $Out"
