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
- **Test with throwaway data.** Set `ORIXNOTCH_DATA_DIR` to a scratch folder to run against isolated data instead of `%LOCALAPPDATA%\OrixNotch`. Other test hooks are env vars named `ORIXNOTCH_*`: `ORIXNOTCH_AILIMIT_TEST`, `ORIXNOTCH_WPN_DB`, `ORIXNOTCH_CURSOR_DB`, the `*_BASE` URL overrides in `AiService` and `AiUsageService`, and `ORIXNOTCH_UPDATE_BASE` (fake GitHub releases API) with `ORIXNOTCH_UPDATE_DELAY` (seconds to the first update check) for `UpdateService`.
- **Packaging.** Run `pwsh packaging/Build-Exe.ps1`, `Build-Msi.ps1` or `Build-Msix.ps1`. These are self-contained win-x64 publishes into `publish/`, and the MSI needs WiX 5.0.2. CI (`.github/workflows/ci.yml`) runs all three on every PR, so a broken manifest or WiX change fails the PR.
- **Releases.** `pwsh tools/Bump-Version.ps1 X.Y.Z` sets `<Version>` in the csproj, commits and tags it. `git push origin main --follow-tags` then triggers `release.yml`. Details are in `docs/RELEASING.md`.

## Architecture

**Shell (`Shell/NotchWindow.xaml.cs`, ~1.6k lines) owns everything visual about the notch:**
- the borderless top-most window and its custom silhouette geometry
- spring animation of width and height (`Spring.cs`)
- hover, leave and pointer-dodge logic, and the tab bar (`NotchWindow.TabDrag.cs` for reordering)
- the closed-pill "activity" display, driven by `Services/NotchActivityService`: music, timers, reminders, idle items
- the **peek** alert queue: `EnqueuePeek(PeekRequest)`, for transient alerts out of the closed notch. Reminders, Windows notifications, AI-limit alerts and phone-transfer requests all go through it.

It also exposes `Instance`, `OpenTool(id)`, `ShowTool(id)` and `HoldOpen()`. Wrap any modal dialog in `using (NotchWindow.Instance?.HoldOpen())`, otherwise the notch collapses when the dialog takes focus.

**Tools:**
- `Shell/ToolRegistry.cs` lists every tool as `ToolDef(id, name, icon, width, height, factory)`.
- Each tool is a `UserControl` in `Tools/`. Optionally it implements `IToolView` (`OnShown`/`OnHidden`, to poll only while visible) and `IEscapeHandler`.
- The user's order and hidden set live in `AppSettings.ToolOrder` and `HiddenTools`. Ids missing from the order go at the end.
- Sizes follow `docs/ui-measurements.md`: content is about 690×203, and `ToolRegistry.W`/`H` hold the defaults.

**Services (`Services/`)** are either singletons (`X.Instance`) or static classes (`SettingsService`, `ThemeService`, `HotkeyService`, `StartupService`, `AiService`). Many are CommunityToolkit.Mvvm `ObservableObject`s with `[ObservableProperty]`, and views bind to them or listen to `PropertyChanged`. Work done off the UI thread (sockets, Kestrel, file I/O) must marshal back through `Application.Current.Dispatcher` before touching observable collections.

**Persistence:**
- `Services/Storage.cs`: `Storage.Load<T>` and `Save` write JSON files under the data dir (atomic tmp+move); `Storage.Dir(name)` gives a subfolder.
- `SettingsService.Current` (also `App.Settings`) is one `AppSettings` POCO. Add new settings as properties with defaults. `SettingsService.Save(notify)` raises `Changed` when `notify` is true, and the shell re-applies layout on that.
- Secrets (API keys, the phone TLS cert) are stored DPAPI-protected via `ProtectedData`.

**Settings UI (`Settings/SettingsView.xaml.cs`)** is built in code rather than XAML. It uses the `Pages` table plus helpers: `Title`, `Section`, `Row(title, description, control)`, `Toggle`, `Combo`, `TextInput`. Consecutive `Row`s group into one card.

**Icons:**
- `AppIcon` enum members are rendered by `HeroIcon`.
- The artwork comes from `HeroIconsData.g.cs` (generated Heroicons, do not hand-edit) or `HeroCustomIcons.cs` (hand-added Tabler/Phosphor paths with their own viewBox).
- To add an icon, add an enum member plus one of those entries.

**Theming:** `Themes/Theme.xaml` defines brushes (`SurfaceBrush`, `AccentBrush`, `SubTextBrush`, …) and styles (`IconButton`, `AccentButton`, `Card`, `Caps`, `SubText`, `ToggleSwitch`). `ThemeService` swaps the colors live, so reference brushes with `DynamicResource` or `SetResourceReference`, never hard-coded colors.

**Phone transfer (`Services/PhoneTransferService*.cs`, `Tools/PhoneView`, `Resources/phone.html`):**
- A Kestrel server (`Microsoft.AspNetCore.App` framework reference) serves a browser page over HTTP on 53318 (53328 if taken; the port that worked is saved in `PhonePort`). The phone opens it by scanning a QR code whose URL carries a key; every request checks it. The key is kept DPAPI-protected in `phone-key.bin` across restarts and only replaced when the user turns Phone off (`TurnOffAsync`); app exit uses `StopAsync`, which keeps it.
- Phone → PC uploads go to the receive folder and the Phone tab's Transfers list (text too, click to copy), not the Shelf. The page has separate Transfer and Shelf tabs.
- PC → phone goes through the in-memory `Outbox` (`PhoneTransferService.Outbox.cs`). The page learns of changes from Server-Sent Events on `/web/events`. Folders and "Download all" stream as a zip built on the fly; `ZipArchive` only writes synchronously, so that request sets `IHttpBodyControlFeature.AllowSynchronousIO`.
- Minimal-API handlers that return `Task<IResult>` must be passed as a `Delegate` via the `Handler(...)` wrapper. A bare method group binds as a `RequestDelegate` and silently drops the result.
- It is off by default (`PhoneEnabled`).

**Packaging and the Store:** `packaging/Package.appxmanifest` is the MSIX source; `packaging/layout` is generated output. When adding capabilities, put general ones before `rescap`. `StartupService` uses the MSIX StartupTask when packaged and the per-user Run key otherwise.
