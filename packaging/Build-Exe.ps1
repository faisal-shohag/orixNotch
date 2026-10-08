# Builds the portable OrixNotch.exe (self-contained single file, no install).
# Run from repo root:  pwsh packaging/Build-Exe.ps1 [-Version 1.2.3]
param([string]$Version, [string]$OutDir)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/Common.ps1"

$appVersion = Get-AppVersion $Version
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot "publish/exe" }
Write-Host "Portable exe $appVersion"

Invoke-Publish -OutDir $OutDir -Version $appVersion -SingleFile
$exe = Join-Path $OutDir "OrixNotch.exe"
Invoke-CodeSign $exe

Write-Host "Exe ready: $exe"
Copy-ToOutput $exe "OrixNotch.exe"
