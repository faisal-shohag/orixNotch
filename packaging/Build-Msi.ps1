# Builds the OrixNotch MSI (per-user, x64, no admin needed).
# Requires WiX v5 (v7+ needs OSMF EULA acceptance) and its Util extension:
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.Util.wixext/5.0.2
# Run from repo root:  pwsh packaging/Build-Msi.ps1 [-Version 1.2.3]
# Silent install:      msiexec /i publish/OrixNotch.msi /qn LAUNCHAPP=0
param([string]$Version, [string]$OutFile)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/Common.ps1"

$appVersion = Get-AppVersion $Version
$stage = Join-Path $PSScriptRoot "msi-stage"
if (-not $OutFile) { $OutFile = Join-Path $RepoRoot "publish/OrixNotch.msi" }
Write-Host "MSI $appVersion"

# 1. Self-contained single-file publish (same exe as the portable download), signed if a cert is set up.
Invoke-Publish -OutDir $stage -Version $appVersion -SingleFile
Invoke-CodeSign (Join-Path $stage "OrixNotch.exe")

# 2. File list for the AppBinaries component.
$files = Get-ChildItem $stage -File | Sort-Object Name
$xml = @('<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">')
foreach ($f in $files) {
    $id = if ($f.Name -eq "OrixNotch.exe") { ' Id="MainExe"' } else { "" }
    $xml += "  <File$id Source=`"$($f.FullName)`" />"
}
$xml += "</Include>"
Set-Content (Join-Path $PSScriptRoot "MsiFiles.g.wxi") ($xml -join "`r`n") -Encoding UTF8

# 3. Build.
if (-not ((wix extension list -g) -match "WixToolset.Util.wixext")) {
    wix extension add -g WixToolset.Util.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw "could not add WixToolset.Util.wixext" }
}
New-Item -ItemType Directory -Path (Split-Path $OutFile) -Force | Out-Null
& wix build -arch x64 -ext WixToolset.Util.wixext -d "ProductVersion=$appVersion" `
    -d "BrandIco=$(Join-Path $ProjectDir 'Assets/Brand/OrixNotch.ico')" `
    -o $OutFile (Join-Path $PSScriptRoot "OrixNotch.wxs")
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
Remove-Item ([IO.Path]::ChangeExtension($OutFile, ".wixpdb")) -ErrorAction SilentlyContinue # build debug info, not shipped
Invoke-CodeSign $OutFile

Write-Host "MSI ready: $OutFile"
Copy-ToOutput $OutFile (Split-Path $OutFile -Leaf)
