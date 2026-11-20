# Microsoft Store listing — English (United States)

Matches Submission 1 of the MSIX product (9PM8RN7CGSWC). Update "What's new" per release.

## Product name
OrixNotch

## Description
OrixNotch — Your Screen, Smarter.

A dynamic-island style toolbox for Windows. A small notch sits at the top of your screen (or any edge you choose) — hover it, click it, or press Ctrl+Alt+N and it springs open into a panel of everyday tools: Now Playing, Ask AI, Calendar with reminders, To-Dos, Quick Note, Clipboard history, File Shelf, phone transfer, Timers, Weather, System Monitor, Screen Time, Controls, Subscriptions, Shortcuts, Stocks, Unit Converter, Emoji picker and a Teleprompter.

When it's closed, the notch keeps working: live music and timer status, calendar reminders that pop out before an event starts, your Windows notifications, and an idle view you choose — clock, weather, battery, network speed, AI usage limits and more.

Everything is stored locally on your PC. Network is used only for weather, stocks, lyrics, currency rates and the optional AI features, and only while those are in use. Ask AI works with your own Anthropic, OpenAI (ChatGPT), Google Gemini or DeepSeek API key.

## Short description
A dynamic-island toolbox for Windows: music, calendar reminders, notifications, to-dos, timers, weather and AI at the top of your screen.

## What's new
What's new in OrixNotch 0.15
• New tools: Screen Time with daily goals and an end-of-day wrap, Controls with quick toggles and per-app volume, Subscriptions with renewal alerts, and a Shortcuts launcher.
• Your notch, your way: Island, Notch, Tab or Pill shapes, on any edge of the screen.
• Focus mode that holds alerts, with a focus log and streaks, plus quiet hours, break reminders and a morning brief.
• Command palette: type "timer 10", "todo buy milk" or "20 usd to eur" from anywhere.
• Ask AI explains text you select in any app, and you can copy text straight from the screen.
• Redesigned System, Weather and phone transfer screens, with glowing progress bars and smooth live charts.
• New looks: Coral themes, glass tinted by your wallpaper, and one-click looks you can share.
• More alerts: screenshot previews, Bluetooth battery levels, unread notification counts, and a heads-up before you hit an AI limit.
• Faster, lighter and more reliable, with many fixes and accessibility improvements.

## Product features
- Dynamic-island notch in four shapes (Island, Notch, Tab, Pill), on any screen edge
- Now Playing with album art, controls and synced lyrics
- Ask AI with Claude, ChatGPT, Gemini or DeepSeek: drop images, or explain text selected in any app
- Command palette: start a timer, add a to-do or convert currency by typing
- Phone and PC transfer by QR code, with no app to install
- Drag and drop onto the notch to send to the Shelf, your phone or Ask AI
- Clipboard history, a File Shelf and copy text from the screen
- Calendar with reminders, plus Google, Outlook and iCloud calendar sync
- To-Dos, Quick Notes and a Teleprompter for calls and recordings
- Pomodoro, countdown and stopwatch timers
- Focus mode with streaks, quiet hours, break reminders and a morning brief
- Screen Time with per-app daily goals and an end-of-day wrap
- Controls: quick toggles, per-app volume, mic mute and keep awake
- Subscriptions tracker with renewal alerts
- Windows notifications mirrored on the notch, with an unread count
- AI Usage for Claude Code, Codex, Cursor and more, with limit alerts
- Weather, Stocks, System Monitor, Unit Converter, Emoji picker and Shortcuts
- 19 color schemes, gradient accents, wallpaper-tinted glass and one-click looks
- Customizable idle notch: clock, weather, battery, network speed and more
- Steps aside in full screen, for apps you choose and in screenshots; live alerts for downloads, Bluetooth and battery

## Keywords (max 7)
dynamic island · notch · clipboard history · calendar reminders · pomodoro timer · productivity tools · ai usage

## Screenshots (store-assets/screenshots, 1920x1080)
01-hero, 02-alerts, 03-music, 04-calendar, 05-ask-ai, 06-phone, 07-shelf-clipboard, 08-focus, 09-out-of-your-way, 10-themes (made by marketing/store-shots/Make-StoreShots.ps1) · Box art: BoxArt-1080.png · Poster: Poster-720x1080.png

## Copyright and trademark info
Copyright © 2026. All rights reserved. OrixNotch and the OrixNotch logo are trademarks of Abu Nayim Faisal.

## Developed by
Abu Nayim Faisal

## Applicable license terms
OrixNotch License v1.0

You are granted a non-exclusive, non-transferable license to install and use OrixNotch on Windows devices associated with your Microsoft account.

1. The software is provided "as is", without warranty of any kind.
2. You may not reverse-engineer, decompile, or redistribute the software except as permitted by the Microsoft Store terms.
3. All data created by the app (settings, notes, clipboard history, shelf items) is stored locally on your device and remains yours.
4. Optional features (Weather, Stocks, Lyrics, Currency rates, Ask AI) use third-party services subject to their own terms: Open-Meteo, Yahoo Finance, LRCLIB, ExchangeRate-API, Anthropic, OpenAI, Google, and DeepSeek. Calendar sync fetches only the calendar links you subscribe to. API keys and calendar links you provide are stored encrypted on your device.
5. In no event shall the author be liable for any damages arising from the use of this software.

Bundled third-party components retain their own licenses: Inter typeface (SIL Open Font License 1.1), and Heroicons, Tabler Icons and Phosphor Icons (MIT License).

## runFullTrust justification (Submission options, max 500 chars)
OrixNotch is a WPF (.NET 8) desktop app packaged as MSIX, so it needs runFullTrust to run. It shows a topmost overlay window at the top of the screen that expands into a tools panel. Win32 APIs are used for the overlay window and hover tracking, the Ctrl+Alt+N hotkey, tray icon, clipboard history, drag-and-drop, media controls, CPU/memory/network stats and run-at-login (StartupTask). No services, drivers or elevation; all data stays local.
