# Generates Microsoft Store listing artwork from the OrixNotch brand.
# Outputs (repo-rooted): store-assets/BoxArt-1080.png (1:1, required),
# store-assets/Poster-720x1080.png (2:3, recommended).
# Run from repo root:  pwsh store-assets/Export-StoreArt.ps1
param()

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repoRoot "store-assets"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

. (Join-Path $repoRoot "tools/BrandMark.ps1") # the mark itself (from Assets/Brand/mark.svg)

$Pink = [System.Drawing.Color]::FromArgb(255, 253, 3, 136)
$Orange = [System.Drawing.Color]::FromArgb(255, 252, 111, 24)
$BgTop = [System.Drawing.Color]::FromArgb(255, 6, 9, 19)
$BgBottom = [System.Drawing.Color]::FromArgb(255, 16, 23, 54)

function Get-Font([string]$name, [float]$size, [System.Drawing.FontStyle]$style) {
    try { return New-Object System.Drawing.Font($name, $size, $style) }
    catch { return New-Object System.Drawing.Font("Segoe UI", $size, $style) }
}

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90) | Out-Null
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90) | Out-Null
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90) | Out-Null
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90) | Out-Null
    $p.CloseFigure() | Out-Null
    return $p
}

function New-BrandBrush([System.Drawing.RectangleF]$rect) {
    return New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $Pink, $Orange, [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
}

function Paint-Background($g, [int]$w, [int]$h) {
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, $h)), $BgTop, $BgBottom)
    try { $g.FillRectangle($bg, 0, 0, $w, $h) } finally { $bg.Dispose() }
    # Soft brand glow behind the mark.
    $glowR = [Math]::Min($w, $h) * 0.75
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(($w - $glowR) / 2, $h * 0.37 - $glowR / 2, $glowR, $glowR) | Out-Null
    try {
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
        try {
            $pgb.CenterColor = [System.Drawing.Color]::FromArgb(60, 253, 3, 136)
            $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 253, 3, 136))
            $g.FillPath($pgb, $glow)
        } finally { $pgb.Dispose() }
    } finally { $glow.Dispose() }
}

function Paint-Mark($g, [float]$x, [float]$y, [float]$size) {
    # Rendered by WPF from the SVG (tools/BrandMark.ps1), then placed on the System.Drawing canvas.
    $png = Join-Path ([IO.Path]::GetTempPath()) "orixnotch-mark-$([int]$size).png"
    Save-BrandMarkPng $repoRoot ([int]$size) $png -MarkScale 1.0
    $img = [System.Drawing.Image]::FromFile($png)
    try { $g.DrawImage($img, $x, $y, $size, $size) } finally { $img.Dispose(); Remove-Item $png }
}

function Paint-Wordmark($g, [int]$canvasW, [float]$y, [float]$size) {
    $font = Get-Font "Segoe UI Semibold" $size ([System.Drawing.FontStyle]::Bold)
    try {
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $tight = [System.Drawing.StringFormat]::GenericTypographic # no side padding: "OrixNotch" is one word
        $wOrix = $g.MeasureString("Orix", $font, [System.Drawing.PointF]::Empty, $tight).Width
        $wNotch = $g.MeasureString("Notch", $font, [System.Drawing.PointF]::Empty, $tight).Width
        $total = $wOrix + $wNotch
        $x0 = ($canvasW - $total) / 2
        $nx = $x0 + $wOrix
        $g.DrawString("Orix", $font, [System.Drawing.Brushes]::White, $x0, $y, $tight)
        $grad = New-BrandBrush ([System.Drawing.RectangleF]::new($nx, $y, $wNotch, $size * 1.3))
        try { $g.DrawString("Notch", $font, $grad, $nx, $y, $tight) } finally { $grad.Dispose() }
    } finally { $font.Dispose() }
}

function Paint-Tagline($g, [int]$canvasW, [float]$y, [float]$size) {
    $font = Get-Font "Segoe UI" $size ([System.Drawing.FontStyle]::Regular)
    try {
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $br = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 150, 158, 180))
        try { $g.DrawString("Your Screen, Smarter", $font, $br, $canvasW / 2, $y, $sf) } finally { $br.Dispose() }
    } finally { $font.Dispose() }
}

function New-Art([int]$w, [int]$h, [string]$file, [float]$markSize, [float]$markY, [float]$wordSize, [float]$tagSize) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            Paint-Background $g $w $h
            Paint-Mark $g (($w - $markSize) / 2) $markY $markSize
            Paint-Wordmark $g $w ($markY + $markSize + $h * 0.045) $wordSize
            Paint-Tagline $g $w ($markY + $markSize + $h * 0.045 + $wordSize * 1.55) $tagSize
        } finally { $g.Dispose() }
        $path = Join-Path $outDir $file
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "wrote $path ($w x $h)"
    } finally { $bmp.Dispose() }
}

New-Art 1080 1080 "BoxArt-1080.png" 420 210 104 40
New-Art 720 1080 "Poster-720x1080.png" 320 290 76 30
