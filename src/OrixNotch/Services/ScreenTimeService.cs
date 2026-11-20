using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <summary>
/// Screen Time: how long each app is in front, per day. Off until the user turns it on; stored only on this PC
/// (screentime\yyyy-MM-dd.json, app → seconds). Time doesn't count while the PC is locked or nobody has touched
/// the keyboard or mouse for 2 minutes (unless a video or game is full screen: watching counts).
/// </summary>
public sealed class ScreenTimeService
{
    public static ScreenTimeService Instance { get; } = new();

    private const int TickSeconds = 5;
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);
    private const int KeepDays = 60;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(TickSeconds) };
    private readonly Dictionary<string, string> _names = new(); // process name → friendly name, cached
    private Dictionary<string, int> _today = new();
    private DateTime _day;
    private bool _locked;
    private int _unsaved;

    /// <summary>Raised (UI thread) after time is added, at most every few seconds.</summary>
    public event Action? Changed;

    private ScreenTimeService()
    {
        _timer.Tick += (_, _) => Tick();
        SystemEvents.SessionSwitch += (_, e) => _locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect
            || (_locked && e.Reason is not (SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect));
    }

    public bool Enabled => App.Settings.ScreenTimeEnabled;

    public void Start()
    {
        if (!Enabled) return;
        LoadDay(DateTime.Today);
        _timer.Start();
    }

    public void SetEnabled(bool on)
    {
        App.Settings.ScreenTimeEnabled = on;
        SettingsService.Save(notify: false);
        if (on) Start();
        else
        {
            Save();
            _timer.Stop();
        }
        Changed?.Invoke();
    }

    /// <summary>Saves what's counted so far (on exit, and every minute while running).</summary>
    public void Save()
    {
        if (_unsaved == 0) return;
        Storage.Save(PathFor(_day), _today);
        _unsaved = 0;
    }

    /// <summary>Seconds per app on a day, most used first.</summary>
    public List<(string App, int Seconds)> Day(DateTime day)
    {
        var data = day.Date == _day ? _today : Storage.Load<Dictionary<string, int>>(PathFor(day));
        return data.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public int Total(DateTime day) => Day(day).Sum(x => x.Seconds);

    /// <summary>
    /// Each app's program file, remembered the first time it's in front (screentime-apps.json), so names and icons
    /// still show once the app has closed.
    /// </summary>
    private const string PathsFile = "screentime-apps.json";
    private readonly Dictionary<string, string> _paths = Storage.Load<Dictionary<string, string>>(PathsFile);
    private readonly Dictionary<string, System.Windows.Media.ImageSource?> _icons = new();
    private readonly HashSet<string> _unreadable = new(); // tried this session and couldn't read: don't look again every tick

    private string? PathOf(string app)
    {
        if (_paths.TryGetValue(app, out var known) && File.Exists(known)) return known;
        try
        {
            using var process = Process.GetProcessesByName(app).FirstOrDefault();
            if (process?.MainModule?.FileName is not { Length: > 0 } path) return null;
            _paths[app] = path;
            Storage.Save(PathsFile, _paths);
            return path;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // elevated apps don't let us read their file
        }
    }

    /// <summary>"Google Chrome" for "chrome", from the app's file description when it can be read.</summary>
    public string DisplayName(string app)
    {
        if (_names.TryGetValue(app, out var cached)) return cached;
        var name = app;
        try
        {
            if (PathOf(app) is { } path && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) name = description.Trim();
        }
        catch (FileNotFoundException)
        {
            // Uninstalled since: keep the process name.
        }
        if (name == app && app.Length > 0) name = char.ToUpper(app[0], CultureInfo.CurrentCulture) + app[1..];
        return _names[app] = name;
    }

    /// <summary>The app's own icon, from its program file; null when it can't be found.</summary>
    public System.Windows.Media.ImageSource? IconFor(string app)
    {
        if (_icons.TryGetValue(app, out var cached)) return cached;
        System.Windows.Media.ImageSource? image = null;
        try
        {
            if (PathOf(app) is { } path)
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // No icon: the view shows the app's initial instead.
        }
        return _icons[app] = image;
    }

    /// <summary>Remembers the app's file while it's in front (cheap: once per app).</summary>
    private void Remember(string app)
    {
        if (!_paths.ContainsKey(app) && !_unreadable.Contains(app) && PathOf(app) is null) _unreadable.Add(app);
    }

    public static string Format(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m" : seconds > 0 ? "<1m" : "0m";
    }

    private void Tick()
    {
        if (DateTime.Today != _day)
        {
            Save();
            LoadDay(DateTime.Today);
            Prune();
        }
        if (_locked || ForegroundAppWatcher.Instance.Current is not { } app) return;
        if (Win32.IdleTime() > IdleAfter && Win32.FullscreenKind() == 0) return; // away, and not watching something
        _today[app] = _today.GetValueOrDefault(app) + TickSeconds;
        Remember(app);
        if (++_unsaved >= 60 / TickSeconds) Save();
        Changed?.Invoke();
        CheckSummary();
        CheckGoal(app);
    }

    /// <summary>An app passed its daily goal: (display name, goal minutes). Once per app per day.</summary>
    public event Action<string, int>? GoalPassed;

    private void CheckGoal(string app)
    {
        var s = App.Settings;
        if (!s.ScreenTimeGoals.TryGetValue(app, out var minutes) || _today.GetValueOrDefault(app) < minutes * 60) return;
        var key = $"{DateTime.Today:yyyy-MM-dd}|{app}";
        if (s.ScreenTimeGoalsHit.Contains(key)) return;
        s.ScreenTimeGoalsHit.RemoveAll(k => !k.StartsWith($"{DateTime.Today:yyyy-MM-dd}|")); // only today's matter
        s.ScreenTimeGoalsHit.Add(key);
        SettingsService.Save(notify: false);
        GoalPassed?.Invoke(DisplayName(app), minutes);
    }

    /// <summary>The evening summary: today's total and the most used app (seconds, app), once a day.</summary>
    public event Action<int, string?>? Summary;

    private void CheckSummary()
    {
        var s = App.Settings;
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!s.ScreenTimeSummary || DateTime.Now.Hour < s.ScreenTimeSummaryHour || s.ScreenTimeSummaryShown == today) return;
        s.ScreenTimeSummaryShown = today;
        SettingsService.Save(notify: false);
        var day = Day(DateTime.Today);
        if (day.Count > 0) Summary?.Invoke(day.Sum(x => x.Seconds), DisplayName(day[0].App));
    }

    private void LoadDay(DateTime day)
    {
        _day = day.Date;
        Storage.Dir("screentime"); // Save writes into it
        _today = Storage.Load<Dictionary<string, int>>(PathFor(_day));
    }

    private static string PathFor(DateTime day) =>
        Path.Combine("screentime", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    private static void Prune()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Storage.Dir("screentime"), "*.json"))
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d) && d < DateTime.Today.AddDays(-KeepDays))
                    File.Delete(file);
        }
        catch (IOException ex)
        {
            App.Log(ex);
        }
    }
}
