param([string]$OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$S      = 120          # холст
$stroke = 7.0
$white  = [System.Drawing.Color]::White

function New-Canvas {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function New-Pen {
    $p = New-Object System.Drawing.Pen($white, $stroke)
    $p.LineJoin  = [System.Drawing.Drawing2D.LineJoin]::Round
    $p.StartCap  = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap    = [System.Drawing.Drawing2D.LineCap]::Round
    return $p
}

# треугольник-указатель: x0 — левый край, w — ширина, h — высота, dir 1 вправо / -1 влево
function Get-Triangle([double]$cx, [double]$cy, [double]$w, [double]$h, [int]$dir) {
    $half = $h / 2
    if ($dir -gt 0) {
        return @(
            (New-Object System.Drawing.PointF(($cx - $w/2), ($cy - $half))),
            (New-Object System.Drawing.PointF(($cx + $w/2), $cy)),
            (New-Object System.Drawing.PointF(($cx - $w/2), ($cy + $half)))
        )
    }
    return @(
        (New-Object System.Drawing.PointF(($cx + $w/2), ($cy - $half))),
        (New-Object System.Drawing.PointF(($cx - $w/2), $cy)),
        (New-Object System.Drawing.PointF(($cx + $w/2), ($cy + $half)))
    )
}

function Save-Icon($bmp, $g, $pen, [string]$name) {
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); if ($pen) { $pen.Dispose() }
    "  $name"
}

$c = $S / 2

# --- Play: один треугольник ---
$r = New-Canvas; $bmp = $r[0]; $g = $r[1]; $pen = New-Pen
$g.DrawPolygon($pen, [System.Drawing.PointF[]](Get-Triangle ($c + 4) $c 40 46 1))
Save-Icon $bmp $g $pen 'Play.png'

# --- Pause: две вертикали ---
$r = New-Canvas; $bmp = $r[0]; $g = $r[1]; $pen = New-Pen
$g.DrawLine($pen, [float]($c - 13), [float]($c - 23), [float]($c - 13), [float]($c + 23))
$g.DrawLine($pen, [float]($c + 13), [float]($c - 23), [float]($c + 13), [float]($c + 23))
Save-Icon $bmp $g $pen 'Pause.png'

# --- Next: два треугольника ---
$r = New-Canvas; $bmp = $r[0]; $g = $r[1]; $pen = New-Pen
$g.DrawPolygon($pen, [System.Drawing.PointF[]](Get-Triangle ($c - 16) $c 30 42 1))
$g.DrawPolygon($pen, [System.Drawing.PointF[]](Get-Triangle ($c + 18) $c 30 42 1))
Save-Icon $bmp $g $pen 'Next.png'

# --- Previous: зеркально ---
$r = New-Canvas; $bmp = $r[0]; $g = $r[1]; $pen = New-Pen
$g.DrawPolygon($pen, [System.Drawing.PointF[]](Get-Triangle ($c + 16) $c 30 42 -1))
$g.DrawPolygon($pen, [System.Drawing.PointF[]](Get-Triangle ($c - 18) $c 30 42 -1))
Save-Icon $bmp $g $pen 'Previous.png'
