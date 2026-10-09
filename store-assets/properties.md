# Microsoft Store properties

Copied from the original Partner Center draft (EXE/MSI product) for reuse with the MSIX product.

- **Category:** Utilities & tools · **Secondary:** Productivity
- **Privacy policy:** text from `docs/privacy-policy.md` (the old draft pasted it as text; an MSIX product needs a public URL, e.g. the GitHub file or the landing site)
- **Support contact / email:** faisalshohagprog@gmail.com
- **Generative AI disclosure:** ticked ("This product incorporates generative AI features…")
- **Input:** Keyboard, Mouse

## Notes for certification

OrixNotch is a dynamic-island style toolbox for Windows. No account, sign-in, or license key is required — all features work immediately after install.

HOW TO OPEN THE APP
- After launch, a small dark pill sits at the top-center of the screen. Hover over it, click it, or press Ctrl+Alt+N to expand the tool panel. Press Esc or click Close to collapse it (collapsing does NOT quit the app).
- If you can't see the pill, use the system tray icon (OrixNotch logo) > "Open panel", or press Ctrl+Alt+N.
- The app is single-instance: launching it again focuses the existing instance.

WHAT TO TEST
- Home, Clipboard, Quick Note, To-Dos, Pomodoro/Timer, Calendar, File Shelf, Unit Converter, Emoji picker, System Monitor, Shortcuts, Teleprompter: fully offline, no setup needed.
- Weather (Open-Meteo), Stocks (Yahoo Finance), and Lyrics (LRCLIB, artist/track lookup in Now Playing): require internet but no keys — just open the tool.
- Ask AI (Anthropic, OpenAI, Google, DeepSeek): OPTIONAL and requires the tester's own API key (Settings > Ask AI). Please skip this if you don't have a key; every other feature is unaffected. Keys are stored encrypted (Windows DPAPI) on the test machine only.

NOTES
- Play audio in any app (e.g. browser/Spotify) to test Now Playing via the Windows media API.
- "Open at login" is on by default and uses the package's StartupTask; it can be turned off in Settings > General or Task Manager > Startup apps.
- Windows notifications are mirrored on the notch only after the user allows notification access (Settings > General > Notifications).
- Local data (settings, notes, history) is stored locally; no data leaves the machine except the third-party requests listed above.
- To fully quit: tray icon > "Quit OrixNotch" (or Settings > About > Quit OrixNotch).
- Requires Windows 10 version 2004 or later.
