# Builds the OrixNotch MSIX package.
#
#   Microsoft Store upload (Store signs it):
#     pwsh packaging/Build-Msix.ps1 -Version 1.2.3 -IdentityName <Store name> -Publisher "CN=..." -PublisherDisplayName "..."
#   Local sideload test (self-signed; trust publish/OrixNotch-test.cer in Local Machine > Trusted People first):
#     pwsh packaging/Build-Msix.ps1 -Sign
#
# The app is published self-contained: MSIX can't pull in the .NET 8 Desktop Runtime, so a
# framework-dependent package would fail to start on PCs without it.
param(
    [string]$Version,
    [string]$IdentityName = "OrixNotch",
    [string]$Publisher = "CN=OrixNotch",
    [string]$PublisherDisplayName = "Faisal Shohag",
    [string]$OutFile,
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/Common.ps1"
Add-Type -AssemblyName System.Drawing

$appVersion = Get-AppVersion $Version
$layout = Join-Path $PSScriptRoot "layout"
if (-not $OutFile) { $OutFile = Join-Path $RepoRoot "publish/OrixNotch.msix" }
Write-Host "MSIX $appVersion.0 · $IdentityName · $Publisher"

# 1. Self-contained folder publish.
Invoke-Publish -OutDir $layout -Version $appVersion

# 2. Manifest: version (x.y.z.0 — the Store requires revision 0) and identity.
$manifest = Get-Content (Join-Path $PSScriptRoot "Package.appxmanifest") -Raw
$manifest = $manifest -replace '(<Identity[^>]*?\sVersion=")[^"]+"', "`${1}$appVersion.0`""
$manifest = $manifest -replace '(<Identity[^>]*?\sName=")[^"]+"', "`${1}$IdentityName`""
$manifest = $manifest -replace '(<Identity[^>]*?\sPublisher=")[^"]+"', "`${1}$([Security.SecurityElement]::Escape($Publisher))`""
$manifest = $manifest -replace '<PublisherDisplayName>[^<]*</PublisherDisplayName>', "<PublisherDisplayName>$([Security.SecurityElement]::Escape($PublisherDisplayName))</PublisherDisplayName>"
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding UTF8

# 3. Tile assets resized from the brand logo.
$logo = Join-Path $ProjectDir "Assets/Brand/logo-512.png"
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
        } finally { $bmp.Dispose() }
    } finally { $src.Dispose() }
}
Write-Tile "StoreLogo.png" 50 50
Write-Tile "Square44x44Logo.png" 44 44
Write-Tile "Square150x150Logo.png" 150 150

# 4. Pack.
$sdkBin = Get-WindowsSdkBin
New-Item -ItemType Directory -Path (Split-Path $OutFile) -Force | Out-Null
& (Join-Path $sdkBin "makeappx.exe") pack /d $layout /p $OutFile /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

if ($Sign) {
    # Self-signed test certificate; its subject must equal the manifest Publisher.
    $certPath = Join-Path $RepoRoot "publish/OrixNotch-test.cer"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.FriendlyName -eq "OrixNotch test cert" } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "OrixNotch test cert" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    }
    Export-Certificate -Cert $cert -FilePath $certPath | Out-Null
    & (Join-Path $sdkBin "signtool.exe") sign /fd SHA256 /sha1 $cert.Thumbprint $OutFile
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
    Write-Host "Test-signed. Trust $certPath (Local Machine > Trusted People) before installing."
}

Write-Host "MSIX ready: $OutFile"
Copy-ToOutput $OutFile ($(if ($Sign) { "OrixNotch-test-signed.msix" } else { Split-Path $OutFile -Leaf }))
