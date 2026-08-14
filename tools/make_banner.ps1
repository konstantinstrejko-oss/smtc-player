# Баннер для окна установщика Rainmeter: строго 400x60, BMP.
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'RMSKIN.bmp'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$W = 400; $H = 60
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

# фон — вертикальный градиент графита
$rect  = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(22, 22, 24),
    [System.Drawing.Color]::FromArgb(12, 12, 13),
    [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
$g.FillRectangle($brush, $rect)

$fontName = New-Object System.Drawing.Font('Segoe UI Semilight', 18, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fontSub  = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$dim      = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 255, 255, 255))

$g.DrawString('SMTC PLAYER', $fontName, [System.Drawing.Brushes]::White, 20, 11)
$g.DrawString('now playing, from any media app', $fontSub, $dim, 22, 34)

# линия прогресса справа — фирменная деталь виджета, вне текстового блока
$penDim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(55, 255, 255, 255), 1.0)
$g.DrawLine($penDim, 258, 30, 378, 30)
$penLit = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.6)
$g.DrawLine($penLit, 258, 30, 322, 30)
$g.FillEllipse([System.Drawing.Brushes]::White, 319.0, 27.0, 6.0, 6.0)

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose(); $brush.Dispose(); $penDim.Dispose(); $penLit.Dispose()
$fontName.Dispose(); $fontSub.Dispose(); $dim.Dispose()
"OK: $Out"
