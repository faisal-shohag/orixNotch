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

$Blue = [System.Drawing.Color]::FromArgb(255, 46, 156, 255)
$Indigo = [System.Drawing.Color]::FromArgb(255, 79, 70, 255)
$Violet = [System.Drawing.Color]::FromArgb(255, 124, 60, 255)
$Pink = [System.Drawing.Color]::FromArgb(255, 217, 70, 239)
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

function New-NotchPath([float]$x, [float]$y, [float]$w, [float]$h) {
    $r = $w * 0.30; $t = $w * 0.12
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine($x + $t, $y, $x + $w - $t, $y) | Out-Null
    $p.AddArc($x + $w - 2 * $t, $y, 2 * $t, 2 * $t, 270, 90) | Out-Null
    $p.AddLine($x + $w, $y + $t, $x + $w, $y + $h - $r) | Out-Null
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90) | Out-Null
    $p.AddLine($x + $w - $r, $y + $h, $x + $r, $y + $h) | Out-Null
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90) | Out-Null
    $p.AddLine($x, $y + $h - $r, $x, $y + $t) | Out-Null
    $p.AddArc($x, $y, 2 * $t, 2 * $t, 180, 90) | Out-Null
    $p.CloseFigure() | Out-Null
    return $p
}

function New-BrandBrush([System.Drawing.RectangleF]$rect) {
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $Blue, $Pink, [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend(4)
    $blend.Colors = @($script:Blue, $script:Indigo, $script:Violet, $script:Pink)
    $blend.Positions = @(0.0, 0.38, 0.68, 1.0)
    $b.InterpolationColors = $blend
    return $b
}

function Paint-Background($g, [int]$w, [int]$h) {
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, $h)), $BgTop, $BgBottom)
    try { $g.FillRectangle($bg, 0, 0, $w, $h) } finally { $bg.Dispose() }
    # Soft brand glow behind the mark.
    $glowR = [Math]::Min($w, $h) * 0.75
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(($w - $glowR) / 2, $h * 0.30 - $glowR / 2, $glowR, $glowR) | Out-Null
    try {
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
        try {
            $pgb.CenterColor = [System.Drawing.Color]::FromArgb(70, 90, 80, 255)
            $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 90, 80, 255))
            $g.FillPath($pgb, $glow)
        } finally { $pgb.Dispose() }
    } finally { $glow.Dispose() }
}

function Paint-Pill($g, [float]$x, [float]$y, [float]$w, [float]$h, [System.Drawing.Color]$bg) {
    $pill = New-RoundedRect $x $y $w $h ($h / 2)
    try {
        $brush = New-BrandBrush ([System.Drawing.RectangleF]::new($x, $y, $w, $h))
        try { $g.FillPath($brush, $pill) } finally { $brush.Dispose() }
        $g.SetClip($pill)
        try {
            $sheen = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 255, 255, 255))
            try {
                $top = New-RoundedRect $x $y $w ($h * 0.5) ($h / 2)
                try { $g.FillPath($sheen, $top) } finally { $top.Dispose() }
            } finally { $sheen.Dispose() }
        } finally { $g.ResetClip() }
        $nw = $w * 0.42; $nh = $h * 0.32
        $nx = $x + ($w - $nw) / 2; $ny = $y - 1
        $g.SetClip($pill)
        try {
            $notch = New-NotchPath $nx $ny $nw $nh
            try {
                $g.FillPath((New-Object System.Drawing.SolidBrush($bg)), $notch)
                $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [Math]::Max(2.0, $w / 220.0))
                try { $g.DrawPath($pen, $notch) } finally { $pen.Dispose() }
            } finally { $notch.Dispose() }
        } finally { $g.ResetClip() }
    } finally { $pill.Dispose() }
}

function Paint-Wordmark($g, [int]$canvasW, [float]$y, [float]$size) {
    $font = Get-Font "Segoe UI Semibold" $size ([System.Drawing.FontStyle]::Bold)
    try {
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $wOrix = $g.MeasureString("Orix", $font).Width
        $wNotch = $g.MeasureString("Notch", $font).Width
        $total = $wOrix + $wNotch
        $x0 = ($canvasW - $total) / 2
        $nx = $x0 + $wOrix
        $g.DrawString("Orix", $font, [System.Drawing.Brushes]::White, $x0, $y)
        $grad = New-BrandBrush ([System.Drawing.RectangleF]::new($nx, $y, $wNotch, $size * 1.3))
        try { $g.DrawString("Notch", $font, $grad, $nx, $y) } finally { $grad.Dispose() }
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

function New-Art([int]$w, [int]$h, [string]$file, [float]$pillW, [float]$pillY, [float]$wordSize, [float]$tagSize) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            Paint-Background $g $w $h
            $pillH = $pillW / 2.6
            $pillX = ($w - $pillW) / 2
            Paint-Pill $g $pillX $pillY $pillW $pillH $BgTop
            Paint-Wordmark $g $w ($pillY + $pillH + $h * 0.045) $wordSize
            Paint-Tagline $g $w ($pillY + $pillH + $h * 0.045 + $wordSize * 1.55) $tagSize
        } finally { $g.Dispose() }
        $path = Join-Path $outDir $file
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "wrote $path ($w x $h)"
    } finally { $bmp.Dispose() }
}

New-Art 1080 1080 "BoxArt-1080.png" 620 300 104 40
New-Art 720 1080 "Poster-720x1080.png" 440 360 76 30
