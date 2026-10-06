using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OrixNotch.Services;

public sealed partial class PomodoroService : ObservableObject
{
    public static PomodoroService Instance { get; } = new();

    public static readonly string[] SoundOptions = ["No Sound", "Soft Chime", "Bell", "Ping"];
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
        if (finishedFocus) Completed++;
        IsBreak = !IsBreak;
        Remaining = Total;
        PlaySound();
        TrayService.Instance?.Notify(
            finishedFocus ? "Focus session done" : "Break is over",
            finishedFocus ? $"Take a {App.Settings.PomodoroBreakMin} minute break." : "Ready for the next focus session?");
    }

    private void PlaySound()
    {
        if (SelectedSound is null or "No Sound") return;
        try
        {
            switch (SelectedSound)
            {
                case "Soft Chime": SystemSounds.Asterisk.Play(); break;
                case "Bell": SystemSounds.Exclamation.Play(); break;
                default: SystemSounds.Beep.Play(); break;
            }
        }
        catch { }
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
