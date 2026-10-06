using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OrixNotch.Services;

/// <summary>Simple countdown timer (the "Timer" tab).</summary>
public sealed partial class CountdownService : ObservableObject
{
    public static CountdownService Instance { get; } = new();

    public static readonly int[] Presets = [1, 5, 10, 15, 25, 30, 45, 60];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _endsAt;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private TimeSpan _remaining = TimeSpan.FromMinutes(10);

    public string RemainingText
    {
        get
        {
            var t = Remaining;
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{(int)t.Minutes:00}:{(int)t.Seconds:00}";
            return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }
    }

    public double Progress
    {
        get
        {
            var total = TimeSpan.FromMinutes(App.Settings.TimerMinutes).TotalSeconds;
            if (total <= 0) return 0;
            return 1 - Remaining.TotalSeconds / total;
        }
    }

    public string SelectedSound
    {
        get => App.Settings.TimerSound;
        set
        {
            if (App.Settings.TimerSound == value) return;
            App.Settings.TimerSound = value;
            SettingsService.Save(notify: false);
            OnPropertyChanged();
        }
    }

    private CountdownService()
    {
        Remaining = TimeSpan.FromMinutes(App.Settings.TimerMinutes);
        _timer.Tick += (_, _) => Tick();
    }

    partial void OnRemainingChanged(TimeSpan value)
    {
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
        Remaining = TimeSpan.Zero;
        if (SelectedSound is not null and not "No Sound")
        {
            try { SystemSounds.Exclamation.Play(); } catch { }
        }
        TrayService.Instance?.Notify("Timer done", "Your countdown has finished.");
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
            if (Remaining <= TimeSpan.Zero) Remaining = TimeSpan.FromMinutes(App.Settings.TimerMinutes);
            if (Remaining <= TimeSpan.Zero) Remaining = TimeSpan.FromMinutes(10);
            _endsAt = DateTime.Now + Remaining;
            _timer.Start();
            IsRunning = true;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        Remaining = TimeSpan.FromMinutes(App.Settings.TimerMinutes);
    }

    [RelayCommand]
    private void AddMinute() => Remaining += TimeSpan.FromMinutes(1);

    public void SetMinutes(int minutes)
    {
        App.Settings.TimerMinutes = minutes;
        SettingsService.Save(notify: false);
        _timer.Stop();
        IsRunning = false;
        Remaining = TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>Count-up stopwatch (the "Stopwatch" tab).</summary>
public sealed partial class StopwatchService : ObservableObject
{
    public static StopwatchService Instance { get; } = new();

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _startedAt;
    private TimeSpan _base;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private int _lapCount;

    public string ElapsedText
    {
        get
        {
            var t = Elapsed;
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{(int)t.Minutes:00}:{(int)t.Seconds:00}";
            return $"{(int)t.Minutes:00}:{t.Seconds:00}";
        }
    }

    public string LapsText => LapCount == 0 ? "No laps yet" : $"{LapCount} lap{(LapCount == 1 ? "" : "s")}";

    private StopwatchService() => _timer.Tick += (_, _) => Elapsed = _base + (DateTime.Now - _startedAt);

    partial void OnElapsedChanged(TimeSpan value) => OnPropertyChanged(nameof(ElapsedText));

    partial void OnLapCountChanged(int value) => OnPropertyChanged(nameof(LapsText));

    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning)
        {
            Elapsed = _base + (DateTime.Now - _startedAt);
            _base = Elapsed;
            _timer.Stop();
            IsRunning = false;
        }
        else
        {
            _startedAt = DateTime.Now;
            _timer.Start();
            IsRunning = true;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        _base = TimeSpan.Zero;
        Elapsed = TimeSpan.Zero;
        LapCount = 0;
    }

    [RelayCommand]
    private void Lap()
    {
        if (!IsRunning && Elapsed == TimeSpan.Zero) return;
        LapCount++;
    }
}
