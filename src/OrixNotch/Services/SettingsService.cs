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
    public int MonitorIndex { get; set; } = -1; // -1 = primary display
    public bool LaunchAtStartup { get; set; }
    public bool HideInFullscreen { get; set; } = true;

    public bool ClipboardEnabled { get; set; } = true;
    public bool ClipboardSkipSensitive { get; set; } = true;
    public int ClipboardMaxItems { get; set; } = 200;

    public int ShelfRetentionDays { get; set; } = 7; // 0 = keep forever

    public string WeatherCity { get; set; } = "London";
    public bool UseFahrenheit { get; set; }

    public List<string> StockTickers { get; set; } = new() { "AAPL", "MSFT", "NVDA", "BTC-USD" };

    public int PomodoroWorkMin { get; set; } = 25;
    public int PomodoroBreakMin { get; set; } = 5;
    public int TimerMinutes { get; set; } = 10;
    public string PomodoroSound { get; set; } = "No Sound";
    public string TimerSound { get; set; } = "No Sound";

    public string LastTool { get; set; } = "nowplaying";

    public string ThemeScheme { get; set; } = "midnight";
    public string ThemeAccent { get; set; } = "orange";

    /// <summary>Small, Default, Large or ExtraLarge — size of the closed notch.</summary>
    public string NotchSize { get; set; } = "Default";

    /// <summary>Name shown in the Home greeting; empty = Windows user name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Time-synced lyrics on Now Playing (looked up on LRCLIB).</summary>
    public bool LyricsEnabled { get; set; } = true;

    /// <summary>Action id → gesture such as "Ctrl+Alt+N". Empty gesture = disabled.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["toggle"] = "Ctrl+Alt+N",
        ["ask"] = "Ctrl+Alt+A",
        ["clipboard"] = "Ctrl+Alt+V",
        ["teleprompter"] = "Ctrl+Alt+T",
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

    // AI usage limits used to compute "% used" (tokens; 0 = no limit shown)
    public long ClaudeSessionTokenLimit { get; set; }
    public long ClaudeWeeklyTokenLimit { get; set; }
}

public static class SettingsService
{
    private const string FileName = "settings.json";

    public static AppSettings Current { get; } = Storage.Load<AppSettings>(FileName);

    /// <summary>Raised after settings that affect layout or behaviour were saved.</summary>
    public static event Action? Changed;

    public static void Save(bool notify = true)
    {
        Storage.Save(FileName, Current);
        if (notify) Changed?.Invoke();
    }
}

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OrixNotch";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
