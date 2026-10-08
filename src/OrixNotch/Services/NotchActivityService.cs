using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrixNotch.Services;

public enum NotchActivityKind
{
    None,
    Music,
    Timer,
    Stopwatch,
    Focus,
    Break,
    Reminder,
}

/// <summary>Single-line text (plus kind) for the closed notch. Event reminders win, then timers, then music.</summary>
public sealed partial class NotchActivityService : ObservableObject
{
    public static NotchActivityService Instance { get; } = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private NotchActivityKind _kind = NotchActivityKind.None;
    [ObservableProperty] private double _progress;

    public bool HasActivity => Kind != NotchActivityKind.None;

    private NotchActivityService()
    {
        NowPlayingService.Instance.PropertyChanged += OnChanged;
        PomodoroService.Instance.PropertyChanged += OnChanged;
        CountdownService.Instance.PropertyChanged += OnChanged;
        StopwatchService.Instance.PropertyChanged += OnChanged;
        EventReminderService.Instance.PropertyChanged += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    partial void OnKindChanged(NotchActivityKind value) => OnPropertyChanged(nameof(HasActivity));

    public void Refresh()
    {
        var pom = PomodoroService.Instance;
        var ctd = CountdownService.Instance;
        var sw = StopwatchService.Instance;
        var np = NowPlayingService.Instance;

        NotchActivityKind kind;
        string text;
        double progress = 0;
        var reminder = EventReminderService.Instance;
        if (reminder.IsActive)
        {
            kind = NotchActivityKind.Reminder;
            text = reminder.Text;
            progress = reminder.Progress;
        }
        else if (pom.IsRunning)
        {
            kind = pom.IsBreak ? NotchActivityKind.Break : NotchActivityKind.Focus;
            text = pom.RemainingText; // mode shows as glyph + colour
            progress = pom.Progress;
        }
        else if (ctd.IsRunning)
        {
            kind = NotchActivityKind.Timer;
            text = ctd.RemainingText;
            progress = ctd.Progress;
        }
        else if (sw.IsRunning)
        {
            kind = NotchActivityKind.Stopwatch;
            text = sw.ElapsedText;
            progress = 0; // no total: track ring only
        }
        else if (np.HasSession && np.IsPlaying)
        {
            kind = NotchActivityKind.Music;
            text = string.IsNullOrEmpty(np.Artist) ? np.Title : $"{np.Title} – {np.Artist}";
        }
        else
        {
            kind = NotchActivityKind.None;
            text = "";
        }

        Kind = kind;
        Text = text;
        Progress = progress;
    }
}
