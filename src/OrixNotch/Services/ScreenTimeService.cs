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

    /// <summary>"Google Chrome" for "chrome", from the running app's file description when it can be read.</summary>
    public string DisplayName(string app)
    {
        if (_names.TryGetValue(app, out var cached)) return cached;
        var name = app;
        try
        {
            var process = Process.GetProcessesByName(app).FirstOrDefault();
            using (process)
            {
                if (process?.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description) name = description;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Elevated apps don't let us read their file: keep the process name.
        }
        if (name == app && app.Length > 0) name = char.ToUpper(app[0], CultureInfo.CurrentCulture) + app[1..];
        return _names[app] = name;
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
        if (++_unsaved >= 60 / TickSeconds) Save();
        Changed?.Invoke();
        CheckSummary();
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
