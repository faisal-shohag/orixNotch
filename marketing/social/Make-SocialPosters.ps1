<#
.SYNOPSIS
    Renders the social media posters: every posters/*.html at four sizes, 2x, into out/<size>/ (gitignored).

.DESCRIPTION
    Sizes: square 1080x1080 (feed), portrait 1080x1350 (feed, taller), story 1080x1920 (Stories/Reels/TikTok),
    landscape 1600x900 (X, LinkedIn, Reddit, Product Hunt). Images come out at 2x for crisp text.
    The posters use the real notch captures and the logo from marketing/store-shots/out, so run
    marketing/store-shots/Make-StoreShots.ps1 first (it captures once; reruns of this script are quick).

.EXAMPLE
    pwsh marketing/social/Make-SocialPosters.ps1
    pwsh marketing/social/Make-SocialPosters.ps1 -Only 03-alerts
#>
param([string]$Only)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$shots = Join-Path $here "../store-shots/out"
if (-not (Test-Path "$shots/raw/home.png") -or -not (Test-Path "$shots/mark.png")) {
    throw "No notch captures yet: run marketing/store-shots/Make-StoreShots.ps1 first."
}
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw "Chrome or Edge is needed to render the posters." }

$sizes = [ordered]@{ square = "1080,1080"; portrait = "1080,1350"; story = "1080,1920"; landscape = "1600,900" }
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "orixnotch-social-chrome"
$posters = Get-ChildItem "$here/posters" -Filter "*.html" | Where-Object { -not $Only -or $_.BaseName -eq $Only } | Sort-Object Name

foreach ($size in $sizes.Keys) {
    $dir = Join-Path $here "out/$size"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($poster in $posters) {
        $png = Join-Path $dir ($poster.BaseName + ".png")
        $url = ([Uri]$poster.FullName).AbsoluteUri + "#$size"
        & $chrome --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$profileDir" --allow-file-access-from-files `
            --force-device-scale-factor=2 --window-size=$($sizes[$size]) --virtual-time-budget=6000 --screenshot="$png" $url 2>$null | Out-Null
        Write-Host "Rendered $size/$($poster.BaseName)"
    }
}
Write-Host "Done: $(Join-Path $here 'out')"
