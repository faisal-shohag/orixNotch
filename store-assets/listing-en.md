# Microsoft Store listing — English (United States)

Matches Submission 1 of the MSIX product (9PM8RN7CGSWC). Update "What's new" per release.

## Product name
OrixNotch

## Description
OrixNotch — Your Screen, Smarter.

A dynamic-island style toolbox for Windows. A small pill sits at the top-center of your screen — hover it, click it, or press Ctrl+Alt+N and it springs open into a panel of everyday tools: Now Playing, Calendar with reminders, To-Dos, Quick Note, Clipboard history, File Shelf, Pomodoro and timers, Weather, System Monitor, Stocks, Unit Converter, Emoji picker and Ask AI.

When it's closed, the notch keeps working: live music and timer status, calendar reminders that pop out before an event starts, your Windows notifications, and an idle view you choose — clock, weather, battery, network speed, AI usage limits and more.

Everything is stored locally on your PC. Network is used only for weather, stocks, lyrics and the optional AI features, and only while those are in use. Ask AI works with your own Anthropic, OpenAI (ChatGPT), Google Gemini or DeepSeek API key.

## Short description
A dynamic-island toolbox for Windows: music, calendar reminders, notifications, to-dos, timers, weather and AI at the top of your screen.

## What's new
v0.6.3: redesigned text fields with icons, clear buttons and inline errors; the closed notch playfully dodges the pointer (Settings → General); Esc closes inline editors first; the notch stays open while you type; polish across Calendar, To-Dos, Converter, Shortcuts and Stocks.

## Product features
- Dynamic-island notch with smooth spring animations
- Now Playing controls and synced lyrics for Spotify, browsers and media apps
- Calendar with reminders that pop out of the notch, with sound
- Windows notifications mirrored on the notch
- Customizable idle notch: clock, weather, battery, network speed, AI usage
- AI Usage for Claude Code, Codex, Cursor, Antigravity and more
- Ask AI with ChatGPT, Claude, Gemini or DeepSeek (your own API key)
- To-Dos, Quick Note, Clipboard history and File Shelf
- Pomodoro, countdown and stopwatch timers
- Weather, Stocks, System Monitor, Unit Converter and Emoji picker

## Keywords (max 7)
dynamic island · notch · clipboard history · calendar reminders · pomodoro timer · productivity tools · ai usage

## Screenshots (store-assets/screenshots, 1920x1080)
1-home, 2-calendar, 3-todos, 4-weather, 5-timers, 6-closed-notch · Box art: BoxArt-1080.png · Poster: Poster-720x1080.png

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
4. Optional features (Weather, Stocks, Lyrics, Ask AI) use third-party services subject to their own terms: Open-Meteo, Yahoo Finance, LRCLIB, Anthropic, OpenAI, Google, and DeepSeek. API keys you provide are stored encrypted on your device.
5. In no event shall the author be liable for any damages arising from the use of this software.

Bundled third-party components retain their own licenses: Inter typeface (SIL Open Font License 1.1) and Heroicons (MIT License).

## runFullTrust justification (Submission options, max 500 chars)
OrixNotch is a WPF (.NET 8) desktop app packaged as MSIX, so it needs runFullTrust to run. It shows a topmost overlay window at the top of the screen that expands into a tools panel. Win32 APIs are used for the overlay window and hover tracking, the Ctrl+Alt+N hotkey, tray icon, clipboard history, drag-and-drop, media controls, CPU/memory/network stats and run-at-login (StartupTask). No services, drivers or elevation; all data stays local.
