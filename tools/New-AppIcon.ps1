<#
    New-AppIcon.ps1 -- draws WUGrid's icon and writes assets\WUGrid.ico (Windows PowerShell 5.1, System.Drawing only)

    The icon is the grid itself: status tiles on a dark rounded square (green = back clean, amber = flagged) with a refresh
    badge from 40 px up. Small sizes get their own pixel-aligned layout instead of a scaled-down copy. Frames below 256 px
    are 32-bit BMP (what every Windows and System.Drawing.Icon read), 256 px is PNG.

    .\tools\New-AppIcon.ps1                       # writes assets\WUGrid.ico
    .\tools\New-AppIcon.ps1 -Preview preview.png  # also a sheet of every size on light and dark
#>
param(
    [string] $Out = (Join-Path $PSScriptRoot '..\assets\WUGrid.ico'),
    [string] $Preview
)
Add-Type -AssemblyName System.Drawing

function Hex([string] $h) { [System.Drawing.ColorTranslator]::FromHtml($h) }
$Navy = Hex '#0F172A'; $NavyTop = Hex '#2B3D5B'; $Green = Hex '#22C55E'; $Amber = Hex '#F59E0B'; $Blue = Hex '#0F6CBD'

function RoundRect([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return ,$p }
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return ,$p
}

# Per size: tiles per side, tile size, gap (pixel-aligned by hand for the small ones)
$Layouts = @{ 16 = 2, 5, 2; 20 = 2, 6, 2; 24 = 2, 7, 2; 32 = 3, 6, 2; 40 = 3, 8, 2; 48 = 3, 10, 3; 64 = 3, 13, 4; 256 = 3, 52, 14 }

function Draw-Icon([int] $n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.SmoothingMode = 'AntiAlias'; $gfx.PixelOffsetMode = 'HighQuality'; $gfx.Clear([System.Drawing.Color]::Transparent)

    $inset = if ($n -ge 48) { [Math]::Round($n / 32) } else { 0 }
    $back = RoundRect $inset $inset ($n - 2 * $inset) ($n - 2 * $inset) ([Math]::Max(2, $n * 0.2))
    $shade = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $n), $NavyTop, $Navy)
    $gfx.FillPath($shade, $back)

    $cells, $tile, $gap = $Layouts[$n]
    $badge = $n -ge 40
    $off = [Math]::Floor(($n - ($cells * $tile + ($cells - 1) * $gap)) / 2)
    $flagged = if ($cells -eq 2) { 1 } else { 2 }   # top-right tile is the one server flagged
    $radius = if ($n -ge 48) { $tile * 0.2 } elseif ($n -ge 32) { 1 } else { 0 }
    for ($i = 0; $i -lt $cells * $cells; $i++) {
        if ($badge -and $i -eq $cells * $cells - 1) { continue }   # the badge sits on the bottom-right tile
        $x = $off + ($i % $cells) * ($tile + $gap)
        $y = $off + [Math]::Floor($i / $cells) * ($tile + $gap)
        $fill = New-Object System.Drawing.SolidBrush ($(if ($i -eq $flagged) { $Amber } else { $Green }))
        $gfx.FillPath($fill, (RoundRect $x $y $tile $tile $radius))
    }

    if ($badge) {
        # White disc with a clockwise refresh arrow, ringed in navy so it stands off the tiles
        $c = $off + ($cells - 1) * ($tile + $gap) + $tile / 2
        $r = $tile * 0.95
        $ring = $r + $n * 0.03
        $gfx.FillEllipse((New-Object System.Drawing.SolidBrush $Navy), $c - $ring, $c - $ring, 2 * $ring, 2 * $ring)
        $gfx.FillEllipse([System.Drawing.Brushes]::White, $c - $r, $c - $r, 2 * $r, 2 * $r)
        $arc = $r * 0.55
        $width = [Math]::Max(1.5, $r * 0.2)
        $pen = New-Object System.Drawing.Pen ($Blue, $width)
        $pen.StartCap = 'Round'
        $start = -70; $sweep = 280
        $gfx.DrawArc($pen, $c - $arc, $c - $arc, 2 * $arc, 2 * $arc, $start, $sweep)
        $t = ($start + $sweep) * [Math]::PI / 180
        $px = $c + $arc * [Math]::Cos($t); $py = $c + $arc * [Math]::Sin($t)
        $tx = -[Math]::Sin($t); $ty = [Math]::Cos($t)   # clockwise tangent
        $nx = [Math]::Cos($t); $ny = [Math]::Sin($t)
        $head = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($px + $tx * $width * 1.6), ($py + $ty * $width * 1.6)),
            (New-Object System.Drawing.PointF ($px + $nx * $width * 1.3), ($py + $ny * $width * 1.3)),
            (New-Object System.Drawing.PointF ($px - $nx * $width * 1.3), ($py - $ny * $width * 1.3)))
        $gfx.FillPolygon((New-Object System.Drawing.SolidBrush $Blue), $head)
    }
    $gfx.Dispose()
    return ,$bmp
}

# 32-bit BMP frame for an .ico: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, then an all-zero AND mask
function Get-BmpFrame([System.Drawing.Bitmap] $bmp) {
    $n = $bmp.Width
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $n, $n), 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $pixels = New-Object byte[] ($data.Stride * $n)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$n); $w.Write([int](2 * $n)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($row = $n - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $data.Stride, $n * 4) }
    $maskRow = [Math]::Ceiling($n / 32) * 4
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
    return ,$ms.ToArray()
}

function Get-PngFrame([System.Drawing.Bitmap] $bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) {
    $b = Draw-Icon $s
    [pscustomobject]@{ Size = $s; Bytes = $(if ($s -ge 256) { Get-PngFrame $b } else { Get-BmpFrame $b }) }
}

$Out = [System.IO.Path]::GetFullPath($Out)
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Out))
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)   # ICONDIR
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {   # ICONDIRENTRY
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$f.Bytes.Length); $w.Write([int]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Close()
"$Out  ($((Get-Item $Out).Length) bytes, sizes $($sizes -join ', '))"

if ($Preview) {
    $sheet = New-Object System.Drawing.Bitmap 900, 620
    $sg = [System.Drawing.Graphics]::FromImage($sheet)
    $sg.InterpolationMode = 'NearestNeighbor'; $sg.PixelOffsetMode = 'Half'
    foreach ($band in @(@(0, [System.Drawing.Color]::White), @(310, (Hex '#202020')))) {
        $top = $band[0]
        $sg.FillRectangle((New-Object System.Drawing.SolidBrush $band[1]), 0, $top, 900, 310)
        $sg.DrawImage((Draw-Icon 256), 10, $top + 27, 256, 256)
        $x = 290
        foreach ($s in 64, 48, 40, 32, 24, 20, 16) { $sg.DrawImage((Draw-Icon $s), $x, $top + 20, $s, $s); $x += $s + 16 }
        $x = 290
        foreach ($s in 40, 32, 24, 16) { $sg.DrawImage((Draw-Icon $s), $x, $top + 110, $s * 4, $s * 4); $x += $s * 4 + 12 }
    }
    $sheet.Save([System.IO.Path]::GetFullPath($Preview), [System.Drawing.Imaging.ImageFormat]::Png)
}
