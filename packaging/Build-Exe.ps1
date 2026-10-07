# Builds the portable self-contained OrixNotch exe.
# Run from repo root:  pwsh packaging/Build-Exe.ps1
# Output goes to publish/ and output/ (output/ is git-ignored; installers ship via GitHub Releases).
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$publishExe = Join-Path $repoRoot "publish/OrixNotch.exe"
$outDir = Join-Path $repoRoot "output"
$outExe = Join-Path $outDir "OrixNotch.exe"

Write-Host "Publishing exe..."
New-Item -ItemType Directory -Path (Split-Path $publishExe) -Force | Out-Null
& dotnet publish (Join-Path $repoRoot "src/OrixNotch") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o (Split-Path $publishExe)
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item $publishExe $outExe -Force

Write-Host "Exe ready: $publishExe (mirrored to output/OrixNotch.exe, git-ignored)"
Get-Item $publishExe, $outExe | Select-Object FullName, Length
