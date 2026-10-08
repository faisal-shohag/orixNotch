using System.IO;
using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrixNotch.Tools;

namespace OrixNotch.Services;

/// <summary>
/// Watches today's timed calendar events and raises a reminder for the closed notch from
/// <see cref="Lead"/> before an event until <see cref="Linger"/> after it starts.
/// Chimes when a reminder first appears and again when the event starts; can be dismissed.
/// </summary>
public sealed partial class EventReminderService : ObservableObject
{
    public static EventReminderService Instance { get; } = new();

    public static readonly string[] SoundNames = ["Calendar", "Chime", "Bell", "No Sound"];

    public static TimeSpan Lead => TimeSpan.FromMinutes(Math.Max(1, App.Settings.ReminderLeadMin));
    public static TimeSpan Linger => TimeSpan.FromMinutes(Math.Max(1, App.Settings.ReminderLingerMin));

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isNow;
    [ObservableProperty] private string _text = "";
    /// <summary>0 at the start of the lead window, 1 when the event starts.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _title = "";
    /// <summary>"Starts in 10 min · 10:30" / "Starting now · 10:30" for the alert.</summary>
    [ObservableProperty] private string _subText = "";
    /// <summary>"10m" / "Now" for the compact notch.</summary>
    [ObservableProperty] private string _shortWhen = "";

    /// <summary>Raised when a reminder first appears (false) and when its event starts (true).</summary>
    public event Action<bool>? Alert;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private List<CalendarEvent> _events = new();
    private string? _currentKey;
    private readonly HashSet<string> _dismissed = new();
    private readonly HashSet<string> _chimedLead = new();
    private readonly HashSet<string> _chimedStart = new();

    private EventReminderService()
    {
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Reload();
    }

    /// <summary>Call after events.json changes (the Calendar does on add/delete).</summary>
    public void Reload()
    {
        _events = Storage.Load<List<CalendarEvent>>("events.json");
        Check();
    }

    /// <summary>Re-evaluate now (e.g. after the lead/linger settings change).</summary>
    public void Recheck() => Check();

    /// <summary>Hides the current reminder; it won't come back (or chime) for that event.</summary>
    public void Dismiss()
    {
        if (_currentKey is null) return;
        _dismissed.Add(_currentKey);
        Check();
    }

    private static string Key(CalendarEvent e) => $"{e.Date:yyyy-MM-dd}|{e.Time}|{e.Title}";

    private void Check()
    {
        var now = DateTime.Now;
        var due = App.Settings.RemindersEnabled
            ? _events
                .Where(e => e.Date.Date == now.Date && !_dismissed.Contains(Key(e)) && CalendarView.TryParseTime(e.Time, out _))
                .Select(e =>
                {
                    CalendarView.TryParseTime(e.Time, out var t);
                    return (Event: e, Start: e.Date.Date + t);
                })
                .Where(x => now >= x.Start - Lead && now <= x.Start + Linger)
                .OrderBy(x => x.Start)
                .ToList()
            : [];

        // Prefer the next one still to come; otherwise the one that just started.
        var pick = due.Where(x => x.Start >= now).Select(x => (x.Event, x.Start)).FirstOrDefault();
        if (pick.Event is null && due.Count > 0) pick = (due[^1].Event, due[^1].Start);

        if (pick.Event is null)
        {
            _currentKey = null;
            IsActive = false;
            IsNow = false;
            Text = "";
            Title = "";
            SubText = "";
            ShortWhen = "";
            Progress = 0;
            return;
        }

        var key = Key(pick.Event);
        var until = pick.Start - now;
        _currentKey = key;
        IsNow = until <= TimeSpan.Zero;
        var when = IsNow ? "now" : until.TotalMinutes < 1 ? "in <1m" : $"in {(int)Math.Ceiling(until.TotalMinutes)}m";
        Text = $"{pick.Event.Title} · {when}";
        Title = pick.Event.Title;
        var minutes = (int)Math.Ceiling(until.TotalMinutes);
        var clock = pick.Start.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        SubText = IsNow ? $"Starting now · {clock}" : minutes <= 1 ? $"Starts in 1 min · {clock}" : $"Starts in {minutes} min · {clock}";
        ShortWhen = IsNow ? "Now" : $"{Math.Max(1, minutes)}m";
        Progress = Math.Clamp(1 - until / Lead, 0, 1);
        IsActive = true;

        // One chime + alert when the reminder first shows, one when the event starts.
        if (IsNow ? _chimedStart.Add(key) : _chimedLead.Add(key))
        {
            PlaySound(App.Settings.ReminderSound);
            Alert?.Invoke(IsNow);
        }
        if (IsNow) _chimedLead.Add(key); // reminder appeared at/after start: don't chime twice
    }

    /// <summary>Plays one of <see cref="SoundNames"/> (also used to preview from Settings).</summary>
    public static void PlaySound(string? name)
    {
        try
        {
            switch (name)
            {
                case "Calendar":
                    var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav");
                    if (File.Exists(wav)) new SoundPlayer(wav).Play();
                    else SystemSounds.Asterisk.Play();
                    break;
                case "Chime": SystemSounds.Asterisk.Play(); break;
                case "Bell": SystemSounds.Exclamation.Play(); break;
            }
        }
        catch
        {
            // Sound is a nicety; never let it break the reminder.
        }
    }
}
