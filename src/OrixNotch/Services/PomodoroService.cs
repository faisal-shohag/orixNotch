using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OrixNotch.Services;

/// <summary>One day in the focus log.</summary>
public sealed class FocusDay
{
    public int Sessions { get; set; }
    public int Minutes { get; set; }
}

public sealed partial class PomodoroService : ObservableObject
{
    private const string LogFile = "focus-log.json";
    private readonly Dictionary<string, FocusDay> _log = Storage.Load<Dictionary<string, FocusDay>>(LogFile);

    public static PomodoroService Instance { get; } = new();

    public static readonly (int Work, int BreakMin, string Label)[] DurationOptions =
    [
        (15, 3, "15 / 3 min"),
        (25, 5, "25 / 5 min"),
        (45, 10, "45 / 10 min"),
        (60, 15, "60 / 15 min"),
    ];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _endsAt;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBreak;
    [ObservableProperty] private TimeSpan _remaining;
    [ObservableProperty] private int _completed;

    public string RemainingText => $"{(int)Remaining.TotalMinutes:00}:{Remaining.Seconds:00}";
    public string ModeText => IsBreak ? "Break" : "Focus";
    public double Progress => Total.TotalSeconds <= 0 ? 0 : 1 - Remaining.TotalSeconds / Total.TotalSeconds;

    public int SessionNumber => Completed % 4 + 1;
    public string SessionText => $"Session {SessionNumber} of 4";
    public string SessionsTodayText => Completed == 0 ? "No sessions yet today" : $"{Completed} session{(Completed == 1 ? "" : "s")} today";

    public string DurationLabel => $"{App.Settings.PomodoroWorkMin} / {App.Settings.PomodoroBreakMin} min";
    public string SelectedSound
    {
        get => App.Settings.PomodoroSound;
        set
        {
            if (App.Settings.PomodoroSound == value) return;
            App.Settings.PomodoroSound = value;
            SettingsService.Save(notify: false);
            OnPropertyChanged();
        }
    }

    private TimeSpan Total => TimeSpan.FromMinutes(IsBreak ? App.Settings.PomodoroBreakMin : App.Settings.PomodoroWorkMin);

    private PomodoroService()
    {
        Remaining = Total;
        Completed = Today().Sessions; // "N today" survives a restart
        _timer.Tick += (_, _) => Tick();
    }

    partial void OnRemainingChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
    }

    partial void OnIsBreakChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(Total));
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(Focusing));

    partial void OnCompletedChanged(int value)
    {
        OnPropertyChanged(nameof(SessionNumber));
        OnPropertyChanged(nameof(SessionText));
        OnPropertyChanged(nameof(SessionsTodayText));
    }

    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(SelectedSound));
        if (!IsRunning) Remaining = Total;
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
    }

    private void Tick()
    {
        var left = _endsAt - DateTime.Now;
        if (left > TimeSpan.Zero)
        {
            Remaining = TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
            return;
        }

        _timer.Stop();
        IsRunning = false;
        var finishedFocus = !IsBreak;
        if (finishedFocus) LogSession();
        IsBreak = !IsBreak;
        Remaining = Total;
        SoundService.Play(SelectedSound);
        TrayService.Instance?.Notify(
            finishedFocus ? "Focus session done" : "Break is over",
            finishedFocus ? $"Take a {App.Settings.PomodoroBreakMin} minute break." : "Ready for the next focus session?");
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning)
        {
            _timer.Stop();
            IsRunning = false;
        }
        else
        {
            if (Remaining <= TimeSpan.Zero) Remaining = Total;
            _endsAt = DateTime.Now + Remaining;
            _timer.Start();
            IsRunning = true;
        }
    }

    /// <summary>Focus mode in one step (tray, hotkey, palette): a focus session now, optionally of a set length.</summary>
    public void StartFocus(int? minutes = null)
    {
        if (minutes is { } m && m != App.Settings.PomodoroWorkMin) SetFocusMinutes(m);
        if (IsBreak) Skip();
        if (!IsRunning) Toggle();
    }

    /// <summary>A focus session is counting down (quiet hours treat it as quiet when "Quiet during focus" is on).</summary>
    public bool Focusing => IsRunning && !IsBreak;

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd");

    private FocusDay Today() => _log.TryGetValue(Key(DateTime.Today), out var day) ? day : new FocusDay();

    private void LogSession()
    {
        var day = _log.TryGetValue(Key(DateTime.Today), out var d) ? d : _log[Key(DateTime.Today)] = new FocusDay();
        day.Sessions++;
        day.Minutes += App.Settings.PomodoroWorkMin;
        // A year is plenty for streaks.
        foreach (var old in _log.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddDays(-366))) < 0).ToList()) _log.Remove(old);
        Storage.Save(LogFile, _log);
        Completed = day.Sessions;
    }

    public int MinutesToday => Today().Minutes;

    /// <summary>Days in a row with at least one focus session, up to today (or yesterday, if none yet today).</summary>
    public int Streak => StreakOf(_log.Where(kv => kv.Value.Sessions > 0).Select(kv => kv.Key).ToHashSet(), DateTime.Today);

    public static int StreakOf(IReadOnlySet<string> days, DateTime today)
    {
        var day = days.Contains(Key(today)) ? today : today.AddDays(-1);
        var n = 0;
        while (days.Contains(Key(day)))
        {
            n++;
            day = day.AddDays(-1);
        }
        return n;
    }

#if DEBUG
    static PomodoroService()
    {
        var t = new DateTime(2026, 3, 10);
        System.Diagnostics.Debug.Assert(StreakOf(new HashSet<string> { "2026-03-10", "2026-03-09", "2026-03-07" }, t) == 2);
        System.Diagnostics.Debug.Assert(StreakOf(new HashSet<string> { "2026-03-09", "2026-03-08" }, t) == 2); // none yet today
        System.Diagnostics.Debug.Assert(StreakOf(new HashSet<string> { "2026-03-08" }, t) == 0);
    }
#endif

    [RelayCommand]
    private void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        Remaining = Total;
    }

    [RelayCommand]
    private void Skip()
    {
        _timer.Stop();
        IsRunning = false;
        IsBreak = !IsBreak;
        Remaining = Total;
    }

    public void SetFocusMinutes(int minutes)
    {
        App.Settings.PomodoroWorkMin = minutes;
        SettingsService.Save(notify: false);
        _timer.Stop();
        IsRunning = false;
        IsBreak = false;
        Remaining = Total;
        RefreshSettings();
    }

    public void SetDurations(int work, int rest)
    {
        App.Settings.PomodoroWorkMin = work;
        App.Settings.PomodoroBreakMin = rest;
        SettingsService.Save(notify: false);
        _timer.Stop();
        IsRunning = false;
        IsBreak = false;
        Remaining = Total;
        RefreshSettings();
    }
}
