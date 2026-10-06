# Privacy Policy — OrixNotch

**Effective date:** October 6, 2026

OrixNotch ("the app") is a Windows utility developed by Faisal Shohag. This policy explains what data the app handles. In short: **your data stays on your PC — the app has no accounts, no analytics, and no advertising.**

## 1. Data stored on your device

Everything the app saves lives locally under `%LOCALAPPDATA%\OrixNotch` (settings, notes, to-dos, calendar events, clipboard history, shelf items, logs). Nothing is uploaded to the developer or any first-party server, because there isn't one.

| Data | Where it lives | Notes |
| --- | --- | --- |
| Settings & preferences (theme, tools, hotkeys) | Your PC only | Never transmitted |
| Quick notes, to-dos, calendar events | Your PC only | Never transmitted |
| Clipboard history | Your PC only | Clips flagged private by password managers are skipped |
| Shelf files/text you park on the notch | Your PC only | Auto-expires per your retention setting |
| Profile name, avatar | Your PC only | Optional, used for the greeting |
| AI usage stats (local Claude Code / Gemini CLI history) | Read from your user profile folder, displayed locally | Never transmitted |
| API keys (Anthropic / Google) | Your PC only, encrypted with Windows DPAPI | Only sent to the provider you choose, when you use Ask AI |

## 2. Network requests (only when you use the feature)

The app makes internet requests **only** while the relevant tool is open, and only to these third-party services:

| Feature | Service | What is sent |
| --- | --- | --- |
| Weather | Open-Meteo (`api.open-meteo.com`, `geocoding-api.open-meteo.com`) | City name you search, coordinates, unit preference |
| Stocks | Yahoo Finance (`query1.finance.yahoo.com`) | Ticker symbols on your watchlist |
| Lyrics (Now Playing) | LRCLIB (`lrclib.net`) | Current artist and track name |
| Ask AI | Anthropic API or Google Generative Language API | Your prompt and API key, using the provider/model you select |

These providers process requests under their own privacy policies. The app's developer receives none of this data.

## 3. What the app does NOT do

- No user accounts or sign-in.
- No telemetry, analytics, crash reporting, or tracking of any kind.
- No advertising and no sale or sharing of personal data.
- No background network activity — outside the features above, the app works fully offline.
- Media playback info (title, artist, art) is read through the Windows system media API on-device and never leaves your PC except as described in §2 (lyrics lookup).

## 4. System integrations (all local, under your control)

- **Autostart:** if you enable "Open at login," the app adds an entry to the Windows `HKCU\...\Run` registry key. Disable the toggle to remove it.
- **Global hotkey** (`Ctrl+Alt+N`): handled locally to open the panel.
- **Clipboard monitoring:** runs only while the app is running; history never leaves the device. Clear it anytime from the Clipboard tool.
- **Fonts:** a bundled Bengali font is installed per-user on first run so mixed-language text renders correctly.

## 5. Data retention & deletion

- Shelf items expire automatically after your chosen retention period (default 7 days).
- To erase everything, quit the app and delete `%LOCALAPPDATA%\OrixNotch`, then uninstall. Uninstalling via the Microsoft Store removes the app; delete the data folder if you want all local data gone.

## 6. Children's privacy

The app collects no data from anyone, including children under 13. It is a general-utility offline-first tool with no age-gated content.

## 7. Changes to this policy

If the app's data practices change, this policy will be updated and the effective date revised. Material changes will be noted in the release notes.

## 8. Contact

Questions about this policy: **[faisalshohagprog@gmail.com]**.
