# Builds the OrixNotch MSIX package (unsigned, ready for Microsoft Store upload).
# Store signs the package on submission. For local sideload testing use -Sign
# (creates a self-signed CN=OrixNotch cert; install the .cer to Trusted People first).
#
# Run from repo root:  pwsh packaging/Build-Msix.ps1 [-Sign]
param([switch]$Sign)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$layout = Join-Path $repoRoot "packaging/layout"
$outMsix = Join-Path $repoRoot "publish/OrixNotch.msix"

# Sync manifest version from the csproj (x.y.z -> x.y.z.0).
$csproj = Get-Content (Join-Path $repoRoot "src/OrixNotch/OrixNotch.csproj") -Raw
if ($csproj -notmatch "<Version>(\d+\.\d+\.\d+)</Version>") { throw "Version not found in csproj" }
$appVersion = "$($Matches[1]).0"
Write-Host "App version: $appVersion"

# 1. Publish framework-dependent win-x64 (small; Store machines have .NET via the framework package graph).
Write-Host "Publishing app..."
& dotnet publish (Join-Path $repoRoot "src/OrixNotch") -c Release -r win-x64 --self-contained false -o $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. Manifest with synced version.
$manifest = Get-Content (Join-Path $PSScriptRoot "Package.appxmanifest") -Raw
$manifest = $manifest -replace 'Version="\d+\.\d+\.\d+\.\d+"', "Version=`"$appVersion`""
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding UTF8

# Drop debug symbols from the package.
Get-ChildItem $layout -Filter *.pdb -Recurse | Remove-Item -Force

# 3. Tile assets resized from the brand logo.
$logo = Join-Path $repoRoot "src/OrixNotch/Assets/Brand/logo-512.png"
$assetsDir = Join-Path $layout "Assets"
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
function Write-Tile([string]$name, [int]$w, [int]$h) {
    $src = [System.Drawing.Image]::FromFile($logo)
    try {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.Clear([System.Drawing.Color]::FromArgb(255, 11, 14, 23))
                $g.DrawImage($src, 0, 0, $w, $h)
            } finally { $g.Dispose() }
            $bmp.Save((Join-Path $assetsDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "wrote Assets/$name"
        } finally { $bmp.Dispose() }
    } finally { $src.Dispose() }
}
Write-Tile "StoreLogo.png" 50 50
Write-Tile "Square44x44Logo.png" 44 44
Write-Tile "Square150x150Logo.png" 150 150

# 4. Pack (locate the newest installed Windows SDK: works locally and on GitHub runners).
$sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $sdkBin) { throw "makeappx.exe not found (install the Windows 10/11 SDK)" }
$sdkBin = Join-Path $sdkBin "x64"
$makeappx = Join-Path $sdkBin "makeappx.exe"
New-Item -ItemType Directory -Path (Split-Path $outMsix) -Force | Out-Null
Write-Host "Packing MSIX..."
& $makeappx pack /d $layout /p $outMsix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

if ($Sign) {
    $certPath = Join-Path $repoRoot "publish/OrixNotch-test.cer"
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=OrixNotch" -KeyUsage DigitalSignature `
        -FriendlyName "OrixNotch test cert" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    Export-Certificate -Cert $cert -FilePath $certPath | Out-Null
    Write-Host "Test cert exported to $certPath — install it to Local Machine > Trusted People before installing the MSIX."
    & (Join-Path $sdkBin "signtool.exe") sign /fd SHA256 /a /f $certPath /p "" $outMsix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
}

Write-Host "MSIX ready: $outMsix"
Get-Item $outMsix | Select-Object Name, Length

# Mirror to local output/ dir (git-ignored; installers ship via GitHub Releases).
# Test-signed builds get a distinct name so they're never mistaken for the Store upload.
$outDir = Join-Path $repoRoot "output"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$mirrorName = if ($Sign) { "OrixNotch-test-signed.msix" } else { "OrixNotch.msix" }
Copy-Item $outMsix (Join-Path $outDir $mirrorName) -Force
Write-Host "Mirrored to output/$mirrorName"
