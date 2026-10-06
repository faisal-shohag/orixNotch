# UI measurements

Sizes OrixNotch uses, measured from OmniNotch's public demo videos and converted to WPF DIPs.

**Calibration.** Frames were scaled at ≈1.15 px per pt (the menu-bar glyph height and the macOS switch, 38×22 pt, agree). Videos that zoom in on the panel were normalized against the open-panel width. 1 macOS pt ≈ 1 Windows DIP.

| Element | Measured | Where in code |
| --- | --- | --- |
| Closed notch | 207 × 32, ears ≈ 4, bottom radius ≈ 9 | `NotchWindow.ApplyNotchSize`, `EarCollapsed`, `RadiusCollapsed` |
| Open panel (every tool) | 726 × 323, ears ≈ 3, bottom radius ≈ 26 | `ToolRegistry.W/H` + `HeaderH`, `TabBarH`, `SidePad` |
| Content area | 690 × 203 (18 side padding) | `ToolRegistry` |
| Header | title 15 semibold, centered ≈ 31 from top; icons 15 on 28 buttons | `NotchWindow.xaml` Header |
| Tab bar | 36 pitch, 33 × 28 selected highlight (radius 8), 15 icons, ≈ 32 from bottom | `Theme.xaml` TabButton, `TabIndicator` |
| Home | avatar 28, greeting 15 / date 11; Now Playing card 430 × 140, radius 19, art 109 radius 10; card title 15, artist 12 | `HomeView.xaml` |
| Clipboard | search 203 × 28; chips ≈ 26 tall; cards 148 × 150, 13 gap, radius 10, 13 px text on 17 px lines | `ClipboardView.xaml` |
| Settings | panel ≈ 750 × 512, sidebar ≈ 197 (nav rows 30, 13.5 semibold), page title 22 bold, row title 13.5 / description 11.5, switch 38 × 22, controls 28 tall | `SettingsView`, `Theme.xaml` ToggleSwitch |
