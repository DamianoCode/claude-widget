# Uruchom w PowerShell 7: pwsh src/ClaudeWidget/Assets/make-icon.ps1 -IcoPath src/ClaudeWidget/Assets/widget.ico -PreviewPath preview.png
# Ikona widżetu: sygnalizator z trzema światłami w stylistyce karty widżetu.
# Każdy rozmiar rysowany osobno — małe (16–32 px) w uproszczeniu, żeby nie były rozmyte.
param([Parameter(Mandatory)][string]$IcoPath, [Parameter(Mandatory)][string]$PreviewPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Lamps = @('#FF5A4E', '#FFB224', '#3DD68C')

function C([string]$hex, [int]$alpha = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($alpha, $c) }
function Mix([System.Drawing.Color]$a, [System.Drawing.Color]$b, [double]$t) {
    [System.Drawing.Color]::FromArgb(255, [int]($a.R + ($b.R - $a.R) * $t), [int]($a.G + ($b.G - $a.G) * $t), [int]($a.B + ($b.B - $a.B) * $t))
}
function RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [float]($r * 2)
    $p.AddArc([float]$x, [float]$y, $d, $d, 180, 90)
    $p.AddArc([float]($x + $w - $d), [float]$y, $d, $d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc([float]$x, [float]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}
function Ellipse([double]$cx, [double]$cy, [double]$d) { [System.Drawing.RectangleF]::new([float]($cx - $d / 2), [float]($cy - $d / 2), [float]$d, [float]$d) }

function Draw-Icon([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $detail = if ($size -ge 48) { 'full' } elseif ($size -ge 32) { 'mid' } else { 'small' }
    # Proporcje w jednostkach 256 px; małe rozmiary dostają szerszą obudowę i większe światła.
    $s = $size / 256.0
    switch ($detail) {
        'full'  { $hx = 58; $hw = 140; $hy = 8; $hh = 240; $hr = 46; $lampD = 54; $socketD = 66; $cy = @(60, 128, 196) }
        'mid'   { $hx = 52; $hw = 152; $hy = 4; $hh = 248; $hr = 44; $lampD = 60; $socketD = 70; $cy = @(58, 128, 198) }
        default { $hx = 40; $hw = 176; $hy = 0; $hh = 256; $hr = 48; $lampD = 66; $socketD = 0; $cy = @(46, 128, 210) }
    }

    # Obudowa: ciemny gradient jak karta widżetu, jaśniejsza krawędź u góry.
    $body = RoundedRect ($hx * $s) ($hy * $s) ($hw * $s) ($hh * $s) ($hr * $s)
    $top = [System.Drawing.PointF]::new(0, [float]($hy * $s)); $bottom = [System.Drawing.PointF]::new(0, [float](($hy + $hh) * $s))
    if ($detail -eq 'full') {
        # Miękki cień pod obudową, jak w ikonach Windows 11.
        foreach ($step in 1..4) {
            $shadow = RoundedRect (($hx - $step) * $s) (($hy + 3 + $step) * $s) (($hw + 2 * $step) * $s) ($hh * $s) (($hr + $step) * $s)
            $g.FillPath([System.Drawing.SolidBrush]::new((C '#000000' 12)), $shadow)
        }
    }
    $fill = [System.Drawing.Drawing2D.LinearGradientBrush]::new($top, $bottom, (C '#383838'), (C '#151515'))
    $g.FillPath($fill, $body)
    # Jasny obrys: w małych rozmiarach wyraźniejszy, bo na ciemnym motywie obudowa zlewa się z tłem.
    $edgeAlpha = if ($detail -eq 'small') { 80 } else { 34 }
    $pen = [System.Drawing.Pen]::new((C '#FFFFFF' $edgeAlpha), [float][math]::Max(1, 3 * $s))
    $g.DrawPath($pen, $body)

    for ($i = 0; $i -lt 3; $i++) {
        $on = C $Lamps[$i]
        $cx = 128 * $s; $y = $cy[$i] * $s
        if ($detail -eq 'full') {
            # Poświata: kilka coraz słabszych kręgów wokół światła.
            foreach ($step in 5..1) {
                $alpha = [int](22 - $step * 3)
                $brush = [System.Drawing.SolidBrush]::new((C $Lamps[$i] $alpha))
                $g.FillEllipse($brush, (Ellipse $cx $y (($lampD + $step * 7) * $s)))
            }
        }
        if ($socketD -gt 0) {
            $g.FillEllipse([System.Drawing.SolidBrush]::new((C '#0B0B0B')), (Ellipse $cx $y ($socketD * $s)))
        }
        $rect = Ellipse $cx $y ($lampD * $s)
        if ($detail -eq 'small') {
            $g.FillEllipse([System.Drawing.SolidBrush]::new($on), $rect)
        } else {
            # Zapalone światło jak w widżecie: jasny odblask u góry, pełny kolor, ciemniejszy brzeg.
            $path = [System.Drawing.Drawing2D.GraphicsPath]::new(); $path.AddEllipse($rect)
            $radial = [System.Drawing.Drawing2D.PathGradientBrush]::new($path)
            $radial.CenterPoint = [System.Drawing.PointF]::new([float]($cx - $lampD * 0.16 * $s), [float]($y - $lampD * 0.2 * $s))
            $blend = [System.Drawing.Drawing2D.ColorBlend]::new(3)
            $blend.Colors = @((Mix $on ([System.Drawing.Color]::Black) 0.22), $on, (Mix $on ([System.Drawing.Color]::White) 0.55))
            $blend.Positions = @([float]0, [float]0.55, [float]1)
            $radial.InterpolationColors = $blend
            $g.FillEllipse($radial, $rect)
        }
    }
    $g.Dispose()
    $bmp
}

# Przecinek: bez niego PowerShell rozwinąłby tablicę bajtów w potok pojedynczych obiektów.
function PngBytes($bmp) { $ms = [IO.MemoryStream]::new(); $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , [byte[]]$ms.ToArray() }

# ICO z obrazami PNG (Windows Vista+): nagłówek, katalog, dane.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { $bmp = Draw-Icon $size; @{ Size = $size; Bytes = (PngBytes $bmp); Bitmap = $bmp } }
$out = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$img.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }
$w.Flush()
New-Item -ItemType Directory -Force (Split-Path $IcoPath) | Out-Null
[IO.File]::WriteAllBytes($IcoPath, $out.ToArray())

# Podgląd: 256 px oraz wszystkie rozmiary w skali 1:1 na jasnym i ciemnym tle (jak w Eksploratorze).
$preview = [System.Drawing.Bitmap]::new(900, 560)
$g = [System.Drawing.Graphics]::FromImage($preview)
$g.Clear((C '#F3F3F3'))
$g.FillRectangle([System.Drawing.SolidBrush]::new((C '#202020')), 0, 280, 900, 280)
$g.DrawImage($images[-1].Bitmap, 12, 12, 256, 256)
$g.DrawImage($images[-1].Bitmap, 12, 292, 256, 256)
foreach ($row in 0, 1) {
    $x = 300
    foreach ($img in $images | Where-Object { $_.Size -le 128 }) {
        $g.DrawImageUnscaled($img.Bitmap, $x, 20 + $row * 280 + [int]((128 - $img.Size) / 2))
        $x += $img.Size + 18
    }
}
$g.Dispose()
$preview.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
"ico: $IcoPath ($([math]::Round((Get-Item $IcoPath).Length / 1KB)) KB, $($images.Count) rozmiarów)"
