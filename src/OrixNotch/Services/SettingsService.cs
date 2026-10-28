using Microsoft.Win32;

namespace OrixNotch.Services;

public enum OpenMode
{
    Hover,
    Click,
}

public sealed class AppSettings
{
    public OpenMode OpenMode { get; set; } = OpenMode.Hover;
    public int HoverDelayMs { get; set; } = 150;
    public List<string> ToolOrder { get; set; } = new();
    public List<string> HiddenTools { get; set; } = new();
    public int MonitorIndex { get; set; } = -1; // -1 = primary display, -2 = the display the pointer is on
    /// <summary>Open at login. Turned on once for new installs (see StartupService.SyncAsync).</summary>
    public bool LaunchAtStartup { get; set; }
    public bool StartupDefaultApplied { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Hide while one of these apps is in front: process names, lowercase, no ".exe" (ForegroundAppWatcher).</summary>
    public List<string> HideForApps { get; set; } = new();
    public string HideForAppsMode { get; set; } = "Fade";     // "Fade" (top edge brings it back) | "Hidden"
    public bool HideForAppsHoldAlerts { get; set; } = true;   // alerts wait until the app is no longer in front
    /// <summary>"Always" | "WhenActive" (music, timer, reminder) | "HoverOnly"; hidden, it comes back at the top edge.</summary>
    public string NotchVisibility { get; set; } = "Always";
    /// <summary>Leave the notch out of screenshots, recordings and screen sharing.</summary>
    public bool HideFromCapture { get; set; }
    public bool PowerAlerts { get; set; } = true;  // charger in/out, low battery
    public bool VolumeHud { get; set; }            // off: Windows shows its own volume flyout too
    public bool BluetoothAlerts { get; set; } = true;
    public bool DownloadAlerts { get; set; } = true;
    public bool TrackPeek { get; set; }            // a short alert with the new song when the track changes
    /// <summary>The closed notch dodges a pointer coming from its sides (it can be caught from below).</summary>
    public bool RepelCursor { get; set; } = true;

    public bool ClipboardEnabled { get; set; } = true;
    public bool ClipboardSkipSensitive { get; set; } = true;
    public int ClipboardMaxItems { get; set; } = 200;

    public int ShelfRetentionDays { get; set; } = 7; // 0 = keep forever

    // Phone transfer over Wi-Fi (browser page opened from a QR code). Off until turned on: it opens a port on the LAN.
    public bool PhoneEnabled { get; set; }
    public string PhoneReceiveFolder { get; set; } = "";    // empty = Downloads\OrixNotch
    public double PhoneHeight { get; set; } = 220;
    public int PhonePort { get; set; }                      // last port that worked, reused so the link survives restarts

    public string WeatherCity { get; set; } = "London";
    public bool UseFahrenheit { get; set; }

    public List<string> StockTickers { get; set; } = new() { "AAPL", "MSFT", "NVDA", "BTC-USD" };

    public int PomodoroWorkMin { get; set; } = 25;
    public int PomodoroBreakMin { get; set; } = 5;
    public int TimerMinutes { get; set; } = 10;
    public string PomodoroSound { get; set; } = "Soft Chime"; // a timer that ends silently is easy to miss
    public string TimerSound { get; set; } = "Soft Chime";

    // Calendar reminders on the closed notch
    public bool RemindersEnabled { get; set; } = true;
    public int ReminderLeadMin { get; set; } = 10;   // show this long before an event
    public int ReminderLingerMin { get; set; } = 5;  // keep showing this long after it starts
    public string ReminderSound { get; set; } = "Calendar";

    // Windows notifications mirrored on the closed notch
    public bool NotifyEnabled { get; set; } = true;
    public bool NotifyShowText { get; set; } = true;
    public List<string> NotifyMutedApps { get; set; } = new();  // app ids (AUMIDs)
    public List<string> NotifySeenApps { get; set; } = new();   // "aumid|display name", newest first
    public bool NotifyKeepHistory { get; set; } = true;         // save them for the Notifications tool

    // AI tools nearing their session / weekly limit peek out of the closed notch
    public bool AiLimitAlerts { get; set; } = true;
    public List<string> AiLimitAlerted { get; set; } = new();   // "tool|window|threshold|resetTicks" already shown

    // Idle closed notch: what to show when nothing is live
    public List<string> IdleItems { get; set; } = new() { "clock" };
    public string IdleLayout { get; set; } = "Rotate"; // Rotate | Split | Row
    public int IdleRotateSec { get; set; } = 8;
    /// <summary>"Auto" = the AI tool closest to its limit; "Choose" = the tools in <see cref="IdleAiTools"/>.</summary>
    public string IdleAiMode { get; set; } = "Auto";
    public List<string> IdleAiTools { get; set; } = new();

    public string LastTool { get; set; } = "home";

    /// <summary>Look for a newer version (Microsoft Store or GitHub releases) and say so once per version.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public string? UpdateNotified { get; set; }

    public string ThemeScheme { get; set; } = "midnight";
    public string ThemeAccent { get; set; } = "auto"; // "auto" = the scheme's own accent
    public bool Glass { get; set; }                    // see-through, tinted notch with a glass sheen
    public double GlassStrength { get; set; } = 0.6;   // 0 = nearly clear, 1 = nearly opaque tint

    /// <summary>Small, Default, Large or ExtraLarge — size of the closed notch.</summary>
    public string NotchSize { get; set; } = "Default";

    /// <summary>Name shown in the Home greeting; empty = Windows user name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Time-synced lyrics on Now Playing (looked up on LRCLIB).</summary>
    public bool LyricsEnabled { get; set; } = true;

    /// <summary>Action id → gesture such as "Ctrl+Alt+N". Empty gesture = disabled.</summary>
    /// <remarks>
    /// Only the toggle is on by default: global Ctrl+Alt shortcuts are AltGr on many keyboard layouts (Polish
    /// AltGr+A = ą…) and would swallow those letters everywhere; Ctrl+Alt+V is also Word's Paste Special.
    /// </remarks>
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["toggle"] = "Ctrl+Alt+N",
        ["ask"] = "",
        ["clipboard"] = "",
        ["teleprompter"] = "",
    };

    // Teleprompter
    public double PrompterSpeed { get; set; } = 0.35;    // 0..1
    public double PrompterOpacity { get; set; } = 1.0;   // background opacity 0.2..1
    public double PrompterFontSize { get; set; } = 20;
    public double PrompterHeight { get; set; } = 203;
    public bool PrompterMirror { get; set; }

    // Ask AI (API keys are stored separately, encrypted — see AiService)
    public string AiProvider { get; set; } = "anthropic"; // "anthropic" or "gemini"
    public string AiModel { get; set; } = "claude-opus-5-5";
    public double AskAiHeight { get; set; } = 220;

    // Quick Note
    public double NoteHeight { get; set; } = 220;
    public double NotificationsHeight { get; set; } = 220;
    public string? LastNoteId { get; set; }

    // AI usage limits used to compute "% used" (tokens; 0 = no limit shown)
    public long ClaudeSessionTokenLimit { get; set; }
    public long ClaudeWeeklyTokenLimit { get; set; }
}

public static class SettingsService
{
    private const string FileName = "settings.json";

    /// <summary>No settings saved yet: this is the first launch (read before anything saves).</summary>
    public static bool FirstRun { get; } = !System.IO.File.Exists(Storage.PathOf(FileName));

    public static AppSettings Current { get; } = Storage.Load<AppSettings>(FileName);

    /// <summary>
    /// Defaults that depend on this PC, set once on the first launch (saved values win afterwards): the weather
    /// city from the Windows time zone and region, °F where the region isn't metric, and a shorter tab bar. Niche
    /// tools start hidden (one switch away in Settings → Tabs), and so does AI Usage unless Claude Code or Codex is here.
    /// </summary>
    public static void ApplyFirstRunDefaults()
    {
        if (!FirstRun) return;
        var settings = Current;
        var region = System.Globalization.RegionInfo.CurrentRegion;
        if (CityFromTimeZone(TimeZoneInfo.Local.Id, region.TwoLetterISORegionName) is { } city) settings.WeatherCity = city;
        settings.UseFahrenheit = !region.IsMetric;

        settings.HiddenTools.AddRange(["teleprompter", "stocks", "converter", "system", "shortcuts"]);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!System.IO.Directory.Exists(System.IO.Path.Combine(home, ".claude")) && !System.IO.Directory.Exists(System.IO.Path.Combine(home, ".codex")))
            settings.HiddenTools.Add("aiusage");
        Save(notify: false);
    }

    /// <summary>
    /// The city a Windows time zone is named after, using the region to pick between zones' cities
    /// ("W. Europe Standard Time" in IT → "Rome", in DE → "Berlin"). Null for UTC-style zones.
    /// </summary>
    internal static string? CityFromTimeZone(string windowsId, string region)
    {
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, region, out var iana) || iana.StartsWith("Etc/")) return null;
        var slash = iana.LastIndexOf('/');
        return slash < 0 ? null : iana[(slash + 1)..].Replace('_', ' '); // "America/New_York" → "New York"
    }

    /// <summary>Raised after settings that affect layout or behaviour were saved.</summary>
    public static event Action? Changed;

    public static void Save(bool notify = true)
    {
        Storage.Save(FileName, Current);
        if (notify) Changed?.Invoke();
    }
}
