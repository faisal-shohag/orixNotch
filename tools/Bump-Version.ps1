# Sets the app version, commits it and creates the matching tag. Pushing the tag starts the
# Release workflow (.github/workflows/release.yml).
#
#   pwsh tools/Bump-Version.ps1 1.2.0            # then: git push origin main --follow-tags
#   pwsh tools/Bump-Version.ps1 1.3.0-beta.1     # pre-release tag; csproj gets 1.3.0
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "Use x.y.z or x.y.z-suffix" }
$core = $Matches[1]
$tag = "v$Version"

$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    if (git status --porcelain) { throw "Working tree has uncommitted changes; commit or stash them first." }
    if (git tag --list $tag) { throw "Tag $tag already exists." }

    $csprojPath = "src/OrixNotch/OrixNotch.csproj"
    $csproj = Get-Content $csprojPath -Raw
    $updated = $csproj -replace '<Version>[^<]+</Version>', "<Version>$core</Version>"
    if ($updated -ne $csproj) {
        Set-Content $csprojPath $updated -NoNewline -Encoding utf8
        git add $csprojPath
        git commit -m "release: v$Version"
    }
    git tag -a $tag -m "OrixNotch $Version"
    Write-Host "Tagged $tag. Push with:  git push origin HEAD --follow-tags"
}
finally {
    Pop-Location
}
