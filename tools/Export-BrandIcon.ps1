# Exports the OrixNotch app icon (gradient mark on a dark rounded square) to logo-512.png + a multi-size .ico.
# The mark comes from src/OrixNotch/Assets/Brand/mark.svg (see tools/BrandMark.ps1): pink #FD0388 -> orange #FC6F18 on #0B0E17.
# Run: pwsh tools/Export-BrandIcon.ps1
param(
    [string]$OutDir = "src/OrixNotch/Assets/Brand"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $repoRoot "OrixNotch.sln"))) { $repoRoot = (Get-Location).Path }
. (Join-Path $repoRoot "tools/BrandMark.ps1")
$out = Join-Path $repoRoot $OutDir
New-Item -ItemType Directory -Path $out -Force | Out-Null

# Small sizes get a bigger mark so the little star still reads at 16 px.
function Get-MarkScale([int]$size) { if ($size -le 32) { 0.78 } elseif ($size -le 64) { 0.7 } else { 0.62 } }

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$temp = Join-Path ([IO.Path]::GetTempPath()) "OrixNotch-icon"
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$pngs = @()
foreach ($s in $sizes) {
    $pngPath = Join-Path $temp "icon-$s.png"
    Save-BrandMarkPng $repoRoot $s $pngPath -MarkScale (Get-MarkScale $s) -Tile
    $pngs += $pngPath
}

# 512px logo for README, the MSIX package and About.
$logoPath = Join-Path $out "logo-512.png"
Save-BrandMarkPng $repoRoot 512 $logoPath -Tile
Write-Host "wrote $logoPath"

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
Remove-Item $temp -Recurse -Force
Write-Host "wrote $icoPath"
