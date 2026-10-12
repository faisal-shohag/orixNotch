# OrixNotch

<img src="src/OrixNotch/Assets/Brand/logo-512.png" width="180" alt="OrixNotch brand — gradient notch mark" />

> **Your Screen, Smarter** — Smoother • Faster • Smarter

A dynamic-island style toolbox for Windows, inspired by [OmniNotch](https://omninotch.app/) for macOS.
A small black pill sits at the top-center of your screen. Hover it (or click it, or press **Ctrl+Alt+N**) and it springs open into a panel of tools.

## Tools

| Tool | What it does |
| --- | --- |
| Now Playing | Art, title, seek bar and controls for any app using Windows media controls (Spotify, browsers, Media Player…). The closed pill shows the art and an equalizer while music plays. |
| File Shelf | Drop files or text on the notch to park them; drag them back out, open, reveal or copy. Items expire after a configurable number of days. |
| Phone | Send files, folders and text both ways with any phone on the same Wi-Fi: scan the QR code on the notch to open a page. Drop files on the Phone tab (or Shelf → Send to phone) and they show up on the page right away; folders download as .zip. Files from the phone go to `Downloads\OrixNotch` and the Shelf. No app needed. Off until turned on. |
| Clipboard | Searchable history of text, links, images and files. Click to copy again. Skips clips that password managers mark as private. |
| Quick Note | Auto-saving scratchpad. |
| To-Dos | Quick list with stars and completion. |
| Pomodoro | Focus/break timer; the countdown stays visible in the closed pill and a notification fires at the end. |
| Calendar | Month view with local events. |
| Weather | Current conditions + 7-day forecast from Open-Meteo (no API key). |
| System Monitor | CPU (with history), memory, disk, network, battery, uptime. |
| Stocks | Watchlist with intraday sparklines (Yahoo Finance). |
| Unit Converter | Length, mass, temperature, volume and speed. |
| Emoji | Full emoji picker with search and recents; click to copy. |

Tool visibility and order, open mode (hover/click), hover delay, display, autostart and more live in **Settings** (gear icon or tray menu).

When a new version is out, the notch says so once (Microsoft Store builds install it through the Store; GitHub builds open the release). Settings → About checks on demand and has the switch to turn this off.

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

Everything is stored locally in `%LOCALAPPDATA%\OrixNotch` (settings, notes, to-dos, events, clipboard history, shelf). The only network calls are Open-Meteo (weather) and Yahoo Finance (stocks), made only while those tools are open. Phone transfer, when turned on, listens only on your local network (ports 53317–53318) and never goes through a server.

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
