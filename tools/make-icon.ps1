# Renders Marquee's app icon (an amber play triangle setting behind a horizon) into
# src/Marquee/Assets: Marquee.ico (16-256 px) plus PNGs for the title bar and About page.
# Geometry follows the design's 256-unit artboard.
param([string]$Accent = '#F5B544')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\Marquee\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Color([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Triangle([float[]]$points) {
    $pts = for ($i = 0; $i -lt $points.Length; $i += 2) { [System.Drawing.PointF]::new($points[$i], $points[$i + 1]) }
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddPolygon([System.Drawing.PointF[]]$pts)
    $p
}

$size = 1024
$master = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = 'AntiAlias'
$g.PixelOffsetMode = 'HighQuality'
$g.ScaleTransform($size / 256, $size / 256)

$tile = RoundedRect 0 0 256 256 56
$g.SetClip($tile)

$g.FillRectangle([System.Drawing.SolidBrush]::new((Color '#15171B')), 0, 0, 256, 256)
$sky = [System.Drawing.Drawing2D.GraphicsPath]::new()
$sky.AddEllipse(128 - 205, 46 - 205, 410, 410)
$skyBrush = [System.Drawing.Drawing2D.PathGradientBrush]::new($sky)
$skyBrush.CenterPoint = [System.Drawing.PointF]::new(128, 46)
$skyBrush.CenterColor = Color '#2E333C'
$skyBrush.SurroundColors = [System.Drawing.Color[]]@((Color '#15171B'))
$g.FillPath($skyBrush, $sky)
$g.FillRectangle([System.Drawing.SolidBrush]::new((Color '#0F1114')), 0, 162, 256, 94)

$accentBrush = [System.Drawing.SolidBrush]::new((Color $Accent))
$pen = [System.Drawing.Pen]::new((Color $Accent), 20)
$pen.LineJoin = 'Round'
$above = Triangle @(86, 58, 206, 128, 86, 198)
$g.SetClip([System.Drawing.RectangleF]::new(0, 0, 256, 162), 'Intersect')
$g.FillPath($accentBrush, $above)
$g.DrawPath($pen, $above)

$g.SetClip($tile)
$g.SetClip([System.Drawing.RectangleF]::new(0, 166, 256, 90), 'Intersect')
$reflection = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(0, 166), [System.Drawing.PointF]::new(0, 250), (Color $Accent 87), (Color $Accent 0))
$below = Triangle @(86, 270, 206, 200, 86, 130)
$g.FillPath($reflection, $below)
$reflectionPen = [System.Drawing.Pen]::new($reflection, 20)
$reflectionPen.LineJoin = 'Round'
$g.DrawPath($reflectionPen, $below)

$g.SetClip($tile)
$g.FillRectangle([System.Drawing.SolidBrush]::new((Color $Accent 140)), 0, 162, 256, 3)
$g.ResetClip()
$g.DrawPath([System.Drawing.Pen]::new((Color '#FFFFFF' 20), 1), (RoundedRect 0.5 0.5 255 255 55.5))
$g.Dispose()

function Scaled([int]$px) {
    $bmp = [System.Drawing.Bitmap]::new($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($bmp)
    $sg.InterpolationMode = 'HighQualityBicubic'
    $sg.PixelOffsetMode = 'HighQuality'
    $sg.DrawImage($master, 0, 0, $px, $px)
    $sg.Dispose()
    $bmp
}

function PngBytes($bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    # The leading comma stops PowerShell from unrolling the byte array into the pipeline.
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($s in $sizes) { , (PngBytes (Scaled $s)) }

$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'Marquee.ico'), $ico.ToArray())
(Scaled 256).Save((Join-Path $assets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(Scaled 48).Save((Join-Path $assets 'AppIcon48.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"Wrote Marquee.ico ($($sizes -join ', ') px), AppIcon.png and AppIcon48.png to $((Resolve-Path $assets).Path)"
