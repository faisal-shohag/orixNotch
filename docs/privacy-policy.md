# Privacy Policy — OrixNotch

**Effective date:** October 9, 2026

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
| AI usage stats (Claude Code and Codex logs, Antigravity's cached quota) | Read from your user profile folder, displayed locally | Never transmitted |
| API keys (Anthropic / OpenAI / Google / DeepSeek) | Your PC only, encrypted with Windows DPAPI | Only sent to the provider you choose (see §2) |
| Windows notifications (title, text, app name) | Read on your PC, shown on the notch for a few seconds | Never stored or transmitted |

## 2. Network requests (only when you use the feature)

The app makes internet requests only for features you use — while the tool is open, or periodically for items you choose to show on the closed notch (e.g. weather, stocks, AI usage) — and only to these third-party services:

| Feature | Service | What is sent |
| --- | --- | --- |
| Weather | Open-Meteo (`api.open-meteo.com`, `geocoding-api.open-meteo.com`) | City name you search, coordinates, unit preference |
| Stocks | Yahoo Finance (`query1.finance.yahoo.com`) | Ticker symbols on your watchlist |
| Lyrics (Now Playing) | LRCLIB (`lrclib.net`) | Current artist and track name |
| Ask AI | Anthropic, OpenAI, Google Generative Language or DeepSeek API — only the one you select | Your messages and that provider's API key |
| AI Usage — Claude plan limits | Anthropic (`api.anthropic.com`) | Claude Code's own sign-in token, to read your plan usage |
| AI Usage — Cursor | Cursor (`cursor.com`) | Cursor's own saved sign-in, to read your plan usage |
| AI Usage — DeepSeek balance | DeepSeek (`api.deepseek.com`) | Your DeepSeek API key, to read your balance |

These providers process requests under their own privacy policies. The app's developer receives none of this data.

## 3. What the app does NOT do

- No user accounts or sign-in.
- No telemetry, analytics, crash reporting, or tracking of any kind.
- No advertising and no sale or sharing of personal data.
- No network activity outside the features above; everything else works fully offline.
- Media playback info (title, artist, art) is read through the Windows system media API on-device and never leaves your PC except as described in §2 (lyrics lookup).

## 4. System integrations (all local, under your control)

- **Open at login:** on by default. The Microsoft Store version uses Windows' startup task; other versions add an entry to the `HKCU\...\Run` registry key. Turn it off in Settings → General → Open at login (or Task Manager → Startup apps).
- **Notifications:** with your permission (Windows notification access), the app reads incoming Windows notifications to show them briefly on the notch. You can turn this off or mute individual apps in Settings → General → Notifications. Notification content is never stored or sent anywhere.
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
