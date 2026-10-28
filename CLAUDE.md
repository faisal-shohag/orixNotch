# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

OrixNotch is a Windows "dynamic island" toolbox: a WPF (.NET 8, `net8.0-windows10.0.19041.0`) app. A black pill sits top-center and springs open into a panel of tools. There is a single project, `src/OrixNotch`, and no test project.

## Commands

```powershell
dotnet build OrixNotch.sln                         # CI builds with -c Release
dotnet run --project src/OrixNotch                 # run
dotnet run --project src/OrixNotch -- --open phone # start open on a tool id (see ToolRegistry); --settings opens Settings
```

- **The app is single-instance.** It holds a mutex (`OrixNotch.SingleInstance`), so stop any running copy before building or launching, including the installed one at `%LOCALAPPDATA%\Programs\OrixNotch`: `Stop-Process -Name OrixNotch -Force`. If you skip this, a running dev exe locks `bin/Debug` and the build fails. A second instance also just exits silently.
- **Test with throwaway data.** Set `ORIXNOTCH_DATA_DIR` to a scratch folder to run against isolated data instead of `%LOCALAPPDATA%\OrixNotch`. Other test hooks are env vars named `ORIXNOTCH_*`: `ORIXNOTCH_AILIMIT_TEST`, `ORIXNOTCH_WPN_DB`, `ORIXNOTCH_CURSOR_DB`, the `*_BASE` URL overrides in `AiService` and `AiUsageService`, and `ORIXNOTCH_UPDATE_BASE` (fake GitHub releases API) with `ORIXNOTCH_UPDATE_DELAY` (seconds to the first update check) for `UpdateService`. `ORIXNOTCH_SHOTS=<dir>` runs the Store screenshot mode (`Shell/NotchWindow.Shots.cs`; drive it with `marketing/store-shots/Make-StoreShots.ps1`), and `ORIXNOTCH_PC_NAME` overrides the PC name the phone page shows.
- **Packaging.** Run `pwsh packaging/Build-Exe.ps1`, `Build-Msi.ps1` or `Build-Msix.ps1`. These are self-contained win-x64 publishes into `publish/`, and the MSI needs WiX 5.0.2. CI (`.github/workflows/ci.yml`) runs all three on every PR, so a broken manifest or WiX change fails the PR.
- **Releases.** `pwsh tools/Bump-Version.ps1 X.Y.Z` sets `<Version>` in the csproj, commits and tags it. `git push origin main --follow-tags` then triggers `release.yml`, which calls `store.yml` to upload the `*-store.msix` to Partner Center (a draft unless `commit` is set). Details are in `docs/RELEASING.md`.
- **SDK** is pinned in `global.json` (8.0.400, `latestFeature` roll-forward).

## Architecture

**Shell (`Shell/NotchWindow.xaml.cs`, ~1.6k lines) owns everything visual about the notch:**
- the borderless top-most window and its custom silhouette geometry
- spring animation of width and height (`Spring.cs`)
- hover, leave and pointer-dodge logic, and the tab bar (`NotchWindow.TabDrag.cs` for reordering)
- the closed-pill "activity" display, driven by `Services/NotchActivityService`: music, timers, reminders, idle items
- the **peek** alert queue: `EnqueuePeek(PeekRequest)`, for transient alerts out of the closed notch. Reminders, Windows notifications, AI-limit alerts, phone transfers, updates and the device alerts all go through it. A request whose `Key` matches the alert on screen refreshes it in place (volume steps), and a "+N" badge counts the ones waiting.
- auto-hide (`Concealable`/`UpdateConceal`, polled with the dodge): `NotchVisibility` (Always / WhenActive / HoverOnly) and full-screen windows fade the pill to fully transparent, so clicks pass through; touching the top edge brings it back. Exclusive D3D games still get `Visibility.Hidden` in `Guard()`.

It also exposes `Instance`, `OpenTool(id)`, `ShowTool(id)` and `HoldOpen()`. Wrap any modal dialog in `using (NotchWindow.Instance?.HoldOpen())`, otherwise the notch collapses when the dialog takes focus.

**Tools:**
- `Shell/ToolRegistry.cs` lists every tool as `ToolDef(id, name, icon, width, height, factory)`.
- Each tool is a `UserControl` in `Tools/`. Optionally it implements `IToolView` (`OnShown`/`OnHidden`, to poll only while visible) and `IEscapeHandler`.
- The user's order and hidden set live in `AppSettings.ToolOrder` and `HiddenTools`. Ids missing from the order go at the end.
- Sizes follow `docs/ui-measurements.md`: content is about 690×203, and `ToolRegistry.W`/`H` hold the defaults.

**Services (`Services/`)** are either singletons (`X.Instance`) or static classes (`SettingsService`, `ThemeService`, `HotkeyService`, `StartupService`, `AiService`). Many are CommunityToolkit.Mvvm `ObservableObject`s with `[ObservableProperty]`, and views bind to them or listen to `PropertyChanged`. Work done off the UI thread (sockets, Kestrel, file I/O) must marshal back through `Application.Current.Dispatcher` before touching observable collections.

Device alerts: `PowerWatcher` (polled from `Guard()`), `VolumeService` (Core Audio COM callback), `BluetoothWatcher` (WinRT `DeviceWatcher`), `DownloadWatcher` (`FileSystemWatcher` on the real Downloads folder, firing on the `.crdownload`/`.part` → final rename). Each raises an event on the UI thread that `NotchWindow` turns into a peek, gated by its `AppSettings` toggle. `ForegroundAppWatcher` (a `SetWinEventHook` foreground hook plus a `Recheck()` from `Guard()`) drives "Hide for these apps": a listed app in front fades the pill (`Concealable`) or hides the window (`Guard`, mode `Hidden`), and `AlertsHeld()` decides whether peeks wait. A listed app's alert setting beats the general full-screen rule, and exclusive games always hold alerts.

**Calendar:** `CalendarEvent.LoadAll()` merges the user's `events.json` with `events-feed.json`. The feed file is written by `CalendarFeedService` from subscribed `.ics` links (stored DPAPI-protected in `calendar-feeds.bin`; events carry only a hashed `Feed` id) and expanded by `IcsParser`, a pure class with no WPF dependencies. Calendar, Home, idle items and reminders all read through `LoadAll()`. Subscribed events are read-only.

**Updates (`UpdateService`):** Store builds check `StoreContext`, download quietly where allowed, then install through the Windows dialog. An MSI install (detected by `HKCU\Software\OrixNotch\InstallDir` matching the exe folder) downloads the `-setup.msi` release asset to `%TEMP%\OrixNotch-update`, checks it against GitHub's SHA-256 digest, and runs `msiexec /i … /passive`; the MSI closes, upgrades and relaunches the app. The portable exe opens the release page.

**Persistence:**
- `Services/Storage.cs`: `Storage.Load<T>` and `Save` write JSON files under the data dir (atomic tmp+move); `Storage.Dir(name)` gives a subfolder.
- `SettingsService.Current` (also `App.Settings`) is one `AppSettings` POCO. Add new settings as properties with defaults. `SettingsService.Save(notify)` raises `Changed` when `notify` is true, and the shell re-applies layout on that.
- Secrets (API keys, the phone key, subscribed calendar links) are stored DPAPI-protected via `ProtectedData`.
- `SettingsService.FirstRun` is true when no settings file existed at launch. The first launch opens Settings → Tabs as a "pick your tools" welcome. `ApplyFirstRunDefaults()` (called from `App` before the window) sets the PC-dependent defaults once: weather city from the time zone and region, °F/°C, and niche tools hidden. Static defaults stay as property initializers; existing users keep their saved values.

**Settings UI (`Settings/SettingsView.xaml.cs`)** is built in code rather than XAML. It uses the `Pages` table plus helpers: `Title`, `Section`, `Row(title, description, control)`, `Toggle`, `Combo`, `TextInput`. Consecutive `Row`s group into one card. Search renders every page and filters `Row` cards by their text, so a setting is only searchable if it is a `Row`.

**Icons:**
- `AppIcon` enum members are rendered by `HeroIcon`.
- The artwork comes from `HeroIconsData.g.cs` (generated Heroicons, do not hand-edit) or `HeroCustomIcons.cs` (hand-added Tabler/Phosphor paths with their own viewBox).
- To add an icon, add an enum member plus one of those entries.

**Brand:** `Assets/Brand/mark.svg` (the designer's file) is the single source of the logo. `Shell/BrandMark.cs` draws it in the app, and `tools/BrandMark.ps1` renders it for `tools/Export-BrandIcon.ps1` (`OrixNotch.ico`, `logo-512.png`, also used for the tray) and `store-assets/Export-StoreArt.ps1`. The colours are pink `#FD0388` → orange `#FC6F18` on tile `#0B0E17`. After changing the SVG, re-run both scripts.

**Theming:** `Themes/Theme.xaml` defines brushes (`SurfaceBrush`, `AccentBrush`, `SubTextBrush`, …) and styles (`IconButton`, `AccentButton`, `Card`, `Caps`, `SubText`, `ToggleSwitch`). `ThemeService` swaps the colors live, so reference brushes with `DynamicResource` or `SetResourceReference`, never hard-coded colors.

**Phone transfer (`Services/PhoneTransferService*.cs`, `Tools/PhoneView`, `Resources/phone.html`):**
- A Kestrel server (`Microsoft.AspNetCore.App` framework reference) serves a browser page over HTTP on 53318 (53328 if taken; the port that worked is saved in `PhonePort`). The phone opens it by scanning a QR code whose URL carries a key; every request checks it. The key is kept DPAPI-protected in `phone-key.bin` across restarts and only replaced when the user turns Phone off (`TurnOffAsync`); app exit uses `StopAsync`, which keeps it.
- Phone → PC uploads go to the receive folder and the Phone tab's Transfers list (text too, click to copy), not the Shelf. The page has separate Transfer and Shelf tabs.
- PC → phone goes through the in-memory `Outbox` (`PhoneTransferService.Outbox.cs`). The page learns of changes from Server-Sent Events on `/web/events`. Folders and "Download all" stream as a zip built on the fly; `ZipArchive` only writes synchronously, so that request sets `IHttpBodyControlFeature.AllowSynchronousIO`.
- Minimal-API handlers that return `Task<IResult>` must be passed as a `Delegate` via the `Handler(...)` wrapper. A bare method group binds as a `RequestDelegate` and silently drops the result.
- It is off by default (`PhoneEnabled`).

**Packaging and the Store:** `packaging/Package.appxmanifest` is the MSIX source; `packaging/layout` is generated output. When adding capabilities, put general ones before `rescap`. `StartupService` uses the MSIX StartupTask when packaged and the per-user Run key otherwise.
