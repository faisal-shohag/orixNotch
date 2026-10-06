# Builds the OrixNotch MSI installer (per-machine, x64).
# Requires WiX v5 (v7+ needs OSMF EULA acceptance):
#   dotnet tool install --global wix --version 5.0.2
# Run from repo root:  pwsh packaging/Build-Msi.ps1
# Silent install:  msiexec /i publish/OrixNotch.msi /qn
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $PSScriptRoot "msi-stage"
$outMsi = Join-Path $repoRoot "publish/OrixNotch.msi"

# Version from the csproj.
$csproj = Get-Content (Join-Path $repoRoot "src/OrixNotch/OrixNotch.csproj") -Raw
if ($csproj -notmatch "<Version>(\d+\.\d+\.\d+)</Version>") { throw "Version not found in csproj" }
$appVersion = $Matches[1]
Write-Host "App version: $appVersion"

# 1. Self-contained publish (matches the GitHub release exe).
Write-Host "Publishing app..."
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
& dotnet publish (Join-Path $repoRoot "src/OrixNotch") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force

# 2. Harvest files into a WiX fragment (stable auto GUIDs via WiX v4+ default).
$files = Get-ChildItem $stage -File | Sort-Object Name
$xml = @('<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">')
foreach ($f in $files) {
    $id = if ($f.Name -eq "OrixNotch.exe") { ' Id="MainExe"' } else { "" }
    $xml += "  <Component Bitness=`"always64`"><File$id Source=`"$($f.FullName)`" /></Component>"
}
$xml += "</Include>"
Set-Content (Join-Path $PSScriptRoot "MsiFiles.g.wxi") ($xml -join "`r`n") -Encoding UTF8
Write-Host "Harvested $($files.Count) files."

# 3. Build the MSI (WiX v5: no EULA gate; pin wix 5.x — v7 requires OSMF acceptance).
Write-Host "Building MSI..."
& wix build -arch x64 -d "ProductVersion=$appVersion" -d "StageDir=$stage" `
    -d "BrandIco=$(Join-Path $repoRoot 'src/OrixNotch/Assets/Brand/OrixNotch.ico')" `
    -o $outMsi (Join-Path $PSScriptRoot "OrixNotch.wxs")
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Write-Host "MSI ready: $outMsi"
Get-Item $outMsi | Select-Object Name, Length

# Mirror to tracked output/ dir (git tracks MSI/MSIX; EXE is Releases-only).
$outDir = Join-Path $repoRoot "output"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item $outMsi (Join-Path $outDir "OrixNotch.msi") -Force
Write-Host "Mirrored to output/OrixNotch.msi"
