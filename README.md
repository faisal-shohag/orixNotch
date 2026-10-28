# OrixNotch

<img src="src/OrixNotch/Assets/Brand/logo-512.png" width="180" alt="OrixNotch logo — sparkles and a notched ring, pink to orange" />

> **Your Screen, Smarter** — Smoother • Faster • Smarter

A dynamic-island style toolbox for Windows.
A small black pill sits at the top-center of your screen. Hover it (or click it, or press **Ctrl+Alt+N**) and it springs open into a panel of tools.

## Tools

| Tool | What it does |
| --- | --- |
| Now Playing | Art, title, seek bar and controls for any app using Windows media controls (Spotify, browsers, Media Player…). The closed pill shows the art and an equalizer while music plays. |
| File Shelf | Drop files or text on the notch to park them; drag them back out, open, reveal or copy. Items expire after a configurable number of days. |
| Phone | Send files, folders and text both ways with any phone on the same Wi-Fi: scan the QR code on the notch to open a page. Drop files on the Phone tab (or Shelf → Send to phone) and they show up on the page right away; folders download as .zip. Files from the phone go to `Downloads\OrixNotch` (or the folder set in Settings) and show up in the Phone tab's Transfers list; text sent from the phone lands there too, click to copy. No app needed. Off until turned on. |
| Clipboard | Searchable history of text, links, images and files. Click to copy again. Skips clips that password managers mark as private. |
| Quick Note | Auto-saving scratchpad. |
| To-Dos | Quick list with stars and completion. |
| Pomodoro | Focus/break timer; the countdown stays visible in the closed pill and a notification fires at the end. |
| Calendar | Month view with your own events, plus read-only events from subscribed calendars (paste a Google, Outlook or iCloud `.ics` link in Settings; repeating events included). |
| Weather | Current conditions + 7-day forecast from Open-Meteo (no API key). |
| System Monitor | CPU (with history), memory, disk, network, battery, uptime. |
| Stocks | Watchlist with intraday sparklines (Yahoo Finance). |
| Unit Converter | Length, mass, temperature, volume and speed. |
| Emoji | Full emoji picker with search and recents; click to copy. |

Tool visibility and order, open mode (hover/click), hover delay, display, autostart and more live in **Settings** (gear icon or tray menu). Settings has a search box, and the first launch opens on Settings → Tabs to pick your tools. Teleprompter, Shortcuts, System, Stocks and Converter start switched off (AI Usage too, unless Claude Code or Codex is installed); turn them on there. Weather starts on your time zone's city, in °F where your region uses it.

The closed notch can stay visible, show only while something is happening (music, a timer, a reminder), or hide until the pointer touches the top edge of the screen (a thin accent line marks the spot). It steps aside for full-screen videos and games and for apps you pick (fading out or hiding completely, with alerts held or let through), can follow the pointer to another display, and can be left out of screenshots and screen sharing.

Short alerts pop out of the closed notch for reminders, Windows notifications, AI usage limits, phone transfers, charger plugged in or pulled and low battery, Bluetooth devices connecting, finished browser downloads, and optionally volume changes and new songs. Each can be turned off in Settings → General.

When a new version is out, the notch says so once. Microsoft Store builds install it through the Store, after downloading it quietly where Windows allows. The MSI install downloads the new setup in the background and installs it when you click; the portable exe opens the release page. Settings → About checks on demand and has the switch to turn this off.

## Look & feel

- Notch silhouette with concave top corners that flow into the screen edge, springing open and resizing smoothly to fit each tool.
- 17 color schemes (5 with a gradient notch), each with its own matching accent; override it with any of 23 accents (8 gradients). Applied live from Settings → Appearance.
- [Heroicons](https://heroicons.com/) (MIT, by the makers of Tailwind CSS) vendored as vector path data and rendered by `Shell/HeroIcon` — solid glyphs for small/chrome icons, outline glyphs for large/decorative ones; [Inter](https://rsms.me/inter/) typeface bundled in `Fonts/` (SIL OFL).

## Build & run

Requires the .NET 8 SDK on Windows 10 (2004) or later.

```powershell
dotnet run --project src/OrixNotch
# start with the panel open on a tool:
dotnet run --project src/OrixNotch -- --open weather
```

Installers (portable exe, per-user MSI, Store MSIX) are built by the scripts in `packaging/`; CI builds
them on every push and a `vX.Y.Z` tag publishes a GitHub Release. See [docs/RELEASING.md](docs/RELEASING.md)
for the release process, code signing and Microsoft Store setup.

## Data & privacy

Everything is stored locally in `%LOCALAPPDATA%\OrixNotch` (settings, notes, to-dos, events, clipboard history, shelf). Network calls happen only for features you use: Open-Meteo (weather), Yahoo Finance (stocks), LRCLIB (lyrics), the AI provider you pick, calendars you subscribe to (their links are stored encrypted), and GitHub or the Microsoft Store for update checks and downloads. Phone transfer, when turned on, listens only on your local network (port 53318, or 53328 if that one is taken) and never goes through a server.

## Project layout

```
src/OrixNotch/
  Shell/       NotchWindow (pill, spring animation, hover/leave logic), ToolRegistry
  Services/    Now playing, clipboard, shelf, pomodoro, settings, storage, tray
  Tools/       One UserControl per tool
  Settings/    Settings window
  Themes/      Dark theme and control styles
  Interop/     Win32 P/Invoke
```
