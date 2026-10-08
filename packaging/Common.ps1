# Shared helpers for the packaging scripts (dot-source: . "$PSScriptRoot/Common.ps1").

$script:RepoRoot = Split-Path $PSScriptRoot -Parent
$script:ProjectDir = Join-Path $RepoRoot "src/OrixNotch"

# The app version: -Version if given (CI passes the tag), otherwise <Version> from the csproj.
# Always x.y.z — MSI and MSIX derive their own formats from it.
function Get-AppVersion([string]$Override) {
    if ($Override) {
        $v = $Override.TrimStart('v')
        if ($v -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z (got '$Override')" }
        return $v
    }
    $csproj = Get-Content (Join-Path $ProjectDir "OrixNotch.csproj") -Raw
    if ($csproj -notmatch "<Version>(\d+\.\d+\.\d+)</Version>") { throw "Version not found in csproj" }
    return $Matches[1]
}

# Self-contained publish (no .NET install needed on the user's PC). -SingleFile packs everything into one exe.
function Invoke-Publish([string]$OutDir, [string]$Version, [switch]$SingleFile) {
    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    $publishArgs = @(
        "publish", $ProjectDir, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:Version=$Version", "-o", $OutDir
    )
    if ($SingleFile) {
        $publishArgs += "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true"
    }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Get-ChildItem $OutDir -Filter *.pdb -Recurse | Remove-Item -Force
}

# Newest Windows SDK bin\x64 folder (makeappx / signtool); works locally and on GitHub runners.
function Get-WindowsSdkBin {
    $bin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $bin) { throw "Windows SDK not found (makeappx.exe). Install the Windows 10/11 SDK." }
    return Join-Path $bin "x64"
}

# Authenticode-signs files when a code-signing certificate is available:
# env ORIXNOTCH_SIGN_PFX (path to .pfx) + ORIXNOTCH_SIGN_PASSWORD. Without one, files stay unsigned.
function Invoke-CodeSign([string[]]$Files) {
    $pfx = $env:ORIXNOTCH_SIGN_PFX
    if (-not $pfx -or -not (Test-Path $pfx)) {
        Write-Host "No signing certificate configured — leaving unsigned: $($Files -join ', ')"
        return
    }
    $signtool = Join-Path (Get-WindowsSdkBin) "signtool.exe"
    foreach ($f in $Files) {
        & $signtool sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /f $pfx /p $env:ORIXNOTCH_SIGN_PASSWORD $f
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $f" }
    }
}

# Copies a finished installer into output/ (git-ignored) for local testing.
function Copy-ToOutput([string]$File, [string]$Name) {
    $outDir = Join-Path $RepoRoot "output"
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    Copy-Item $File (Join-Path $outDir $Name) -Force
}
