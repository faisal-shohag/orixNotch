# Exports OrixNotch brand icon (dark rounded-square + gradient notch pill) to PNG + multi-size .ico.
# Brand board: blue #2E9CFF -> indigo #4F46FF -> violet #7C3CFF -> pink #D946EF, on near-black #0B0E17.
# Run: pwsh tools/Export-BrandIcon.ps1  (requires .NET / System.Drawing on Windows)
param(
    [string]$OutDir = "src/OrixNotch/Assets/Brand"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $repoRoot "OrixNotch.sln"))) { $repoRoot = (Get-Location).Path }
$out = Join-Path $repoRoot $OutDir
New-Item -ItemType Directory -Path $out -Force | Out-Null

function New-AppIconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        # Dark rounded-square background (matches APP ICON (DARK) on the board).
        $bg = [System.Drawing.Color]::FromArgb(255, 11, 14, 23)
        $radius = [float]($size * 0.24)
        $bgPath = RoundedRect 0 0 $size $size $radius
        try {
            $g.FillPath((New-Object System.Drawing.SolidBrush($bg)), $bgPath)
        } finally { $bgPath.Dispose() }

        # Gradient pill centered.
        $pillW = $size * 0.70
        $pillH = $size * 0.235
        $pillX = ($size - $pillW) / 2
        $pillY = ($size - $pillH) / 2 - $size * 0.02
        $pillPath = RoundedRect $pillX $pillY $pillW $pillH ($pillH / 2)
        try {
            $rect = New-Object System.Drawing.RectangleF($pillX, $pillY, $pillW, $pillH)
            $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                $rect,
                [System.Drawing.Color]::FromArgb(255, 46, 156, 255),
                [System.Drawing.Color]::FromArgb(255, 217, 70, 239),
                [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
            try {
                $blend = New-Object System.Drawing.Drawing2D.ColorBlend(4)
                $blend.Colors = @(
                    [System.Drawing.Color]::FromArgb(255, 46, 156, 255),
                    [System.Drawing.Color]::FromArgb(255, 79, 70, 255),
                    [System.Drawing.Color]::FromArgb(255, 124, 60, 255),
                    [System.Drawing.Color]::FromArgb(255, 217, 70, 239))
                $blend.Positions = @(0.0, 0.38, 0.68, 1.0)
                $brush.InterpolationColors = $blend
                $g.FillPath($brush, $pillPath)
            } finally { $brush.Dispose() }

            # Gloss sheen on top half of the pill.
            $glossPath = RoundedRect $pillX ($pillY) $pillW ($pillH * 0.55) ($pillH / 2)
            try {
                $g.SetClip($pillPath)
                $gloss = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
                try { $g.FillPath($gloss, $glossPath) } finally { $gloss.Dispose() }
            } finally { $g.ResetClip(); $glossPath.Dispose() }

            # Notch cutout: punch through the top-center with the bg color, rounded bottom.
            $notchW = $pillW * 0.42
            $notchH = $pillH * 0.48
            $notchX = $pillX + ($pillW - $notchW) / 2
            $notchY = $pillY - 1
            $notch = NotchPath $notchX $notchY $notchW $notchH
            try {
                $g.FillPath((New-Object System.Drawing.SolidBrush($bg)), $notch)
                $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 255, 255, 255), [float]([Math]::Max(1.0, $size / 128.0)))
                try { $g.DrawPath($pen, $notch) } finally { $pen.Dispose() }
            } finally { $notch.Dispose() }
        } finally { $pillPath.Dispose() }
    } finally { $g.Dispose() }
    return $bmp
}

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90) | Out-Null
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90) | Out-Null
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90) | Out-Null
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90) | Out-Null
    $p.CloseFigure() | Out-Null
    return $p
}

function NotchPath([float]$x, [float]$y, [float]$w, [float]$h) {
    $r = $w * 0.16
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine($x, $y, $x + $w, $y) | Out-Null
    $p.AddLine($x + $w, $y, $x + $w, $y + $h - $r) | Out-Null
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90) | Out-Null
    $p.AddLine($x + $w - $r, $y + $h, $x + $r, $y + $h) | Out-Null
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90) | Out-Null
    $p.AddLine($x, $y + $h - $r, $x, $y) | Out-Null
    $p.CloseFigure() | Out-Null
    return $p
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-AppIconBitmap $s
    try {
        $pngPath = Join-Path $out "icon-$s.png"
        $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += $pngPath
        Write-Host "wrote $pngPath"
    } finally { $bmp.Dispose() }
}

# 256px logo for README / About.
$big = New-AppIconBitmap 512
try {
    $logoPath = Join-Path $out "logo-512.png"
    $big.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "wrote $logoPath"
} finally { $big.Dispose() }

# Combine PNGs into a Vista-style .ico (PNG-compressed entries).
$icoPath = Join-Path $out "OrixNotch.ico"
$entries = New-Object Collections.Generic.List[byte[]]
foreach ($p in $pngs) { $entries.Add([IO.File]::ReadAllBytes($p)) }
$fs = [IO.File]::Open($icoPath, [IO.FileMode]::Create)
try {
    $bw = New-Object IO.BinaryWriter($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    for ($idx = 0; $idx -lt $entries.Count; $idx++) {
        $s = $sizes[$idx]
        $len = $entries[$idx].Length
        $bw.Write([byte](($s -eq 256) ? 0 : $s))
        $bw.Write([byte](($s -eq 256) ? 0 : $s))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($b in $entries) { $bw.Write($b) }
    $bw.Flush()
} finally { $fs.Close() }
Write-Host "wrote $icoPath"
