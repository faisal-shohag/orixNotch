<#
.SYNOPSIS
    Builds the Microsoft Store screenshots: seeds demo data, captures the real notch with the app's
    screenshot mode (ORIXNOTCH_SHOTS), grabs the phone page, then renders the marketing frames.

.DESCRIPTION
    Output: out/raw (transparent 2x notch captures), out/store-3840 (3840x2160) and out/store-1920 (1920x1080).
    Play a song (Spotify, Media Player, a browser tab) before running to get the Now Playing shots.

.EXAMPLE
    pwsh marketing/store-shots/Make-StoreShots.ps1
    pwsh marketing/store-shots/Make-StoreShots.ps1 -SkipCapture   # re-render frames only
#>
param(
    [switch]$SkipCapture,
    [switch]$NoMusic,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Resolve-Path "$here/../.."
$out = Join-Path $here "out"
$raw = Join-Path $out "raw"
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw "Chrome or Edge is needed to render the frames." }
Add-Type -AssemblyName System.Drawing, System.Security

# ------------------------------------------------------------------ demo data

function New-DemoImage([string]$path, [int]$w, [int]$h, [string]$from, [string]$to, [string]$label) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.TextRenderingHint = "AntiAliasGridFit"
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.ColorTranslator]::FromHtml($from)), ([System.Drawing.ColorTranslator]::FromHtml($to)), 35
    $g.FillRectangle($brush, $rect)
    if ($label) {
        # A small bar chart, so it reads as a real screenshot.
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 255, 255, 255))
        $vals = 0.35, 0.55, 0.45, 0.7, 0.62, 0.85, 0.78
        $bw = [int]($w / 12)
        for ($i = 0; $i -lt $vals.Count; $i++) {
            $bh = [int]($h * 0.6 * $vals[$i]); $x = [int]($w * 0.12 + $i * $bw * 1.35)
            $g.FillRectangle($white, $x, $h - $bh - [int]($h * 0.12), $bw, $bh)
        }
        $font = New-Object System.Drawing.Font "Segoe UI Semibold", ([int]($h / 11))
        $g.DrawString($label, $font, $white, [int]($w * 0.1), [int]($h * 0.08))
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Write-Json($path, $value) {
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
}

function New-Seed([string]$dir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir, "$dir/shelf", "$dir/clipboard", "$dir/askai-images" | Out-Null
    $now = Get-Date
    $today = $now.ToString("yyyy-MM-dd")
    $iso = { param($d) $d.ToString("yyyy-MM-ddTHH:mm:ss") }

    Write-Json "$dir/settings.json" ([ordered]@{
        DisplayName = "Alex"; ThemeScheme = "midnight"; ThemeAccent = "auto"; Glass = $false
        CheckForUpdates = $false; LaunchAtStartup = $false; StartupDefaultApplied = $true
        ClipboardEnabled = $false; ShelfRetentionDays = 0
        WeatherCity = "San Francisco"; UseFahrenheit = $true
        StockTickers = @("AAPL", "MSFT", "NVDA", "BTC-USD")
        AiProvider = "anthropic"; AiModel = "claude-opus-5-5"
        AskAiHeight = 400; PhoneEnabled = $true; LastNoteId = "note-standup"; LastTool = "home"
    })

    Write-Json "$dir/todos.json" @(
        @{ Text = "Review Q4 roadmap with design"; Done = $false; Starred = $true; Created = & $iso $now.AddHours(-5) }
        @{ Text = "Send release notes for v2.4"; Done = $false; Starred = $true; Created = & $iso $now.AddHours(-4) }
        @{ Text = "Book flights to Berlin"; Done = $false; Starred = $false; Created = & $iso $now.AddHours(-3) }
        @{ Text = "Reply to Priya about the API limits"; Done = $false; Starred = $false; Created = & $iso $now.AddHours(-2) }
        @{ Text = "Renew domain certificate"; Done = $true; Starred = $false; Created = & $iso $now.AddDays(-1) }
        @{ Text = "Gym — leg day"; Done = $true; Starred = $false; Created = & $iso $now.AddDays(-1) }
    )

    $events = @(
        @{ Date = $today; Time = "09:30"; Title = "Team standup" }
        @{ Date = $today; Time = "13:00"; Title = "Lunch with Sam" }
        @{ Date = $today; Time = "15:30"; Title = "Design review · Notch v2" }
    )
    foreach ($o in 1, 2, 4, 6, 9) {
        $d = $now.AddDays($o).ToString("yyyy-MM-dd")
        $events += @{ Date = $d; Time = @("10:00", "14:00", "11:30", "16:00", "09:00")[$o % 5]; Title = @("1:1 with Jordan", "Sprint planning", "Dentist", "Product demo", "Quarterly review")[$o % 5] }
    }
    Write-Json "$dir/events.json" $events

    Write-Json "$dir/notes.json" @(
        @{ Id = "note-standup"; Created = & $iso $now.AddHours(-1); Updated = & $iso $now.AddMinutes(-12)
           Text = "Standup — Tuesday`n`n• Shipped the new onboarding flow`n• Blocked on payments sandbox keys`n• Demo the notch to marketing at 3:30`n`nFollow up with Priya re: rate limits" }
        @{ Id = "note-ideas"; Created = & $iso $now.AddDays(-2); Updated = & $iso $now.AddDays(-2)
           Text = "Launch ideas`n`nShort video of the island springing open, posted with the Store link." }
    )

    New-DemoImage "$dir/askai-images/chart.png" 1200 760 "#4F46FF" "#D946EF" "Weekly active users"
    Write-Json "$dir/askai-chat.json" @(
        @{ FromUser = $true; Text = "What stands out in this chart? Keep it short."; Display = $null; At = & $iso $now.AddMinutes(-6); Images = @("chart.png") }
        @{ FromUser = $false; Text = "**Steady growth with one dip.**`n`n- Usage climbs about **2.4×** across the week`n- Thursday dips ~12%, likely the outage window`n- The weekend holds near the peak, which suggests real habit`n`nWant a one-line summary for the update?"; Display = $null; At = & $iso $now.AddMinutes(-5); Images = $null }
    )
    [IO.File]::WriteAllBytes("$dir/ai-key.bin", [byte[]](1..32)) # placeholder: enables the composer, never decrypts

    # Shelf: a few real files.
    New-DemoImage "$dir/shelf/Hero banner.png" 1600 900 "#0EA5E9" "#6366F1" ""
    New-DemoImage "$dir/shelf/Moodboard.png" 1200 1200 "#F97316" "#DB2777" ""
    "%PDF-1.4`n1 0 obj<</Type/Catalog>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF" | Set-Content "$dir/shelf/Invoice-0423.pdf" -NoNewline
    "Q4 plan" | Set-Content "$dir/shelf/Q4 Roadmap.docx"
    Compress-Archive -Path "$dir/shelf/Hero banner.png" -DestinationPath "$dir/shelf/Brand assets.zip" -Force
    $shelf = Get-ChildItem "$dir/shelf" | Sort-Object Name | ForEach-Object -Begin { $i = 0 } -Process {
        $i++; @{ Path = $_.FullName; AddedAt = & $iso $now.AddMinutes(-10 * $i); Owned = $true }
    }
    Write-Json "$dir/shelf.json" @($shelf)

    New-DemoImage "$dir/clipboard/shot.png" 900 560 "#10B981" "#0EA5E9" "Signups"
    Write-Json "$dir/clipboard.json" @(
        @{ Id = [guid]::NewGuid(); Kind = 1; Text = "https://github.com/orixnotch/orixnotch/releases"; Time = & $iso $now.AddMinutes(-2); Pinned = $false }
        @{ Id = [guid]::NewGuid(); Kind = 2; ImagePath = (Resolve-Path "$dir/clipboard/shot.png").Path; Time = & $iso $now.AddMinutes(-8); Pinned = $false }
        @{ Id = [guid]::NewGuid(); Kind = 0; Text = "Thanks for the quick turnaround! Let's ship it on Thursday."; Time = & $iso $now.AddMinutes(-15); Pinned = $true }
        @{ Id = [guid]::NewGuid(); Kind = 3; Files = @((Resolve-Path "$dir/shelf/Q4 Roadmap.docx").Path); Time = & $iso $now.AddMinutes(-25); Pinned = $false }
        @{ Id = [guid]::NewGuid(); Kind = 0; Text = "npm run build && npm run deploy -- --prod"; Time = & $iso $now.AddMinutes(-40); Pinned = $false }
    )

    Write-Json "$dir/notifications.json" @(
        @{ Id = "n1"; AppId = "MSTeams_8wekyb3d8bbwe!MSTeams"; AppName = "Microsoft Teams"; Title = "Priya Shah"; Body = "Can you share the deck before the review?"; At = & $iso $now.AddMinutes(-3); Source = "app" }
        @{ Id = "n2"; AppId = "Microsoft.Office.OUTLOOK.EXE.15"; AppName = "Outlook"; Title = "Design review · Notch v2"; Body = "Today 3:30 PM · Room 4B"; At = & $iso $now.AddMinutes(-18); Source = "app" }
        @{ Id = "n3"; AppId = "MSEdge"; AppName = "Microsoft Edge"; Title = "Download complete"; Body = "Brand assets.zip · 24 MB"; At = & $iso $now.AddMinutes(-42); Source = "app" }
        @{ Id = "n4"; AppId = "com.squirrel.Discord.Discord"; AppName = "Discord"; Title = "#launch"; Body = "Sam: the Store listing looks great 🚀"; At = & $iso $now.AddHours(-1); Source = "app" }
        @{ Id = "n5"; AppId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"; AppName = "Settings"; Title = "Updates are ready"; Body = "Restart tonight to finish installing"; At = & $iso $now.AddHours(-3); Source = "app" }
    )

    $shortcuts = @(
        @{ Name = "VS Code"; Target = "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe"; Arguments = "" }
        @{ Name = "Terminal"; Target = "$env:LOCALAPPDATA\Microsoft\WindowsApps\wt.exe"; Arguments = "" }
        @{ Name = "Explorer"; Target = "$env:WINDIR\explorer.exe"; Arguments = "" }
        @{ Name = "Edge"; Target = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"; Arguments = "" }
        @{ Name = "Calculator"; Target = "$env:WINDIR\System32\calc.exe"; Arguments = "" }
        @{ Name = "Notepad"; Target = "$env:WINDIR\notepad.exe"; Arguments = "" }
    ) | Where-Object { Test-Path $_.Target }
    Write-Json "$dir/shortcuts.json" @($shortcuts)

    "Good morning, everyone.`n`nToday we're introducing OrixNotch — the Dynamic Island, reimagined for Windows.`n`nYour music, your timers, your AI assistant and your files, one glance away at the top of the screen.`n`nLet's take a look." |
        Set-Content "$dir/teleprompter.txt" -Encoding utf8
}

# ------------------------------------------------------------------ demo music
# A quiet generated pad played in a small Chrome window, with Media Session metadata and album art, so
# Windows reports a playing track (title, artist, art) to the Now Playing tool.

function Start-DemoMusic([string]$dir) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    New-DemoImage "$dir/art.png" 600 600 "#F97316" "#7C3AED" ""
    $rate = 16000; $seconds = 45; $n = $rate * $seconds
    $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $bw.Write([int](36 + $n * 2)); $bw.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
    $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1); $bw.Write([int]$rate); $bw.Write([int]($rate * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
    $bw.Write([Text.Encoding]::ASCII.GetBytes("data")); $bw.Write([int]($n * 2))
    $buf = New-Object byte[] ($n * 2)
    for ($i = 0; $i -lt $n; $i++) {
        $t = $i / $rate
        $v = [Math]::Sin(2 * [Math]::PI * 220 * $t) + 0.6 * [Math]::Sin(2 * [Math]::PI * 277.18 * $t) + 0.5 * [Math]::Sin(2 * [Math]::PI * 329.63 * $t)
        $s = [int16](600 * $v * (0.6 + 0.4 * [Math]::Sin(2 * [Math]::PI * 0.1 * $t)))
        $buf[2 * $i] = [byte]($s -band 0xFF); $buf[2 * $i + 1] = [byte](($s -shr 8) -band 0xFF)
    }
    $bw.Write($buf); $bw.Flush()
    [IO.File]::WriteAllBytes("$dir/pad.wav", $ms.ToArray())
    @"
<!doctype html><meta charset=utf-8><title>Golden Hour</title>
<audio id=a src="pad.wav" autoplay loop></audio>
<script>
  const a = document.getElementById('a'); a.volume = 0.05;
  navigator.mediaSession.metadata = new MediaMetadata({ title: 'Golden Hour', artist: 'Lumen Drive', album: 'Night Signals',
    artwork: [{ src: 'data:image/png;base64,$([Convert]::ToBase64String([IO.File]::ReadAllBytes("$dir/art.png")))', sizes: '600x600', type: 'image/png' }] });
  a.play();
  a.addEventListener('playing', () => { try { navigator.mediaSession.setPositionState({ duration: 214, position: 83, playbackRate: 1 }); } catch {} });
</script>
"@ | Set-Content "$dir/player.html" -Encoding utf8
    $profileDir = Join-Path ([IO.Path]::GetTempPath()) "orixnotch-shots-music"
    Start-Process $chrome -ArgumentList "--user-data-dir=`"$profileDir`"", "--autoplay-policy=no-user-gesture-required",
        "--allow-file-access-from-files", "--window-size=420,300", "--window-position=40,700", "--app=$(([Uri]"$dir/player.html").AbsoluteUri)" | Out-Null
    Start-Sleep 8 # Windows needs a few seconds of playback before it lists the session
    return $profileDir
}

function Stop-DemoMusic([string]$profileDir) {
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe'" |
        Where-Object { $_.CommandLine -like "*$profileDir*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# ------------------------------------------------------------------ capture

if (-not $SkipCapture) {
    Get-Process OrixNotch -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
    dotnet build "$repo/OrixNotch.sln" -c $Configuration | Out-Null
    if ($LASTEXITCODE) { throw "Build failed." }
    $exe = Get-ChildItem "$repo/src/OrixNotch/bin/$Configuration" -Recurse -Filter OrixNotch.exe | Select-Object -First 1

    $data = Join-Path ([IO.Path]::GetTempPath()) "orixnotch-store-seed"
    New-Seed $data
    if (Test-Path $raw) { Remove-Item $raw -Recurse -Force }
    New-Item -ItemType Directory $raw | Out-Null

    $music = if ($NoMusic) { $null } else { Start-DemoMusic (Join-Path $data "music") }

    Write-Host "Capturing (keep the mouse away from the top of the screen)…"
    $env:ORIXNOTCH_DATA_DIR = $data
    $env:ORIXNOTCH_SHOTS = $raw
    $env:ORIXNOTCH_PC_NAME = "Alex's PC"
    $env:ORIXNOTCH_AIUSAGE_DEMO = "1"
    $proc = Start-Process $exe.FullName -PassThru
    Remove-Item Env:ORIXNOTCH_SHOTS, Env:ORIXNOTCH_DATA_DIR, Env:ORIXNOTCH_PC_NAME, Env:ORIXNOTCH_AIUSAGE_DEMO

    # The phone page, while the app is up: decrypt the throwaway key and screenshot it at phone size.
    while (-not (Test-Path "$raw/manifest.json") -and -not $proc.HasExited) { Start-Sleep 1 }
    $settings = Get-Content "$data/settings.json" -Raw | ConvertFrom-Json
    $key = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes("$data/phone-key.bin"), [Text.Encoding]::UTF8.GetBytes("OrixNotch.PhoneKey"), "CurrentUser"))
    $profileDir = Join-Path ([IO.Path]::GetTempPath()) "orixnotch-shots-chrome"
    & $chrome --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$profileDir" --force-device-scale-factor=3 `
        --window-size=500,1094 --timeout=5000 --screenshot="$raw/phone-page.png" "http://localhost:$($settings.PhonePort)/?k=$key" 2>$null | Out-Null
    New-Item "$raw/continue.flag" -ItemType File | Out-Null
    $proc.WaitForExit(60000) | Out-Null
    if ($music) { Stop-DemoMusic $music }
    Write-Host "Captured: $((Get-ChildItem $raw -Filter *.png).Count) images"
}

# ------------------------------------------------------------------ frames

$big = Join-Path $out "store-3840"
$small = Join-Path $out "store-1920"
New-Item -ItemType Directory -Force $big, $small | Out-Null
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "orixnotch-shots-chrome"
foreach ($frame in Get-ChildItem "$here/frames" -Filter "*.html" | Sort-Object Name) {
    $png = Join-Path $big ($frame.BaseName + ".png")
    & $chrome --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$profileDir" --allow-file-access-from-files `
        --force-device-scale-factor=2 --window-size=1920,1080 --virtual-time-budget=6000 --screenshot="$png" ([Uri]$frame.FullName).AbsoluteUri 2>$null | Out-Null

    # 1920x1080 copy, high-quality downscale.
    $src = [System.Drawing.Image]::FromFile($png)
    $dst = New-Object System.Drawing.Bitmap 1920, 1080
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = "HighQualityBicubic"; $g.PixelOffsetMode = "HighQuality"; $g.CompositingQuality = "HighQuality"
    $g.DrawImage($src, 0, 0, 1920, 1080)
    $dst.Save((Join-Path $small ($frame.BaseName + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $dst.Dispose(); $src.Dispose()
    Write-Host "Rendered $($frame.BaseName)"
}
Write-Host "Done: $big"
