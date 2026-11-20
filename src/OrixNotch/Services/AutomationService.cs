using System.Windows.Threading;
using OrixNotch.Tools;

namespace OrixNotch.Services;

/// <summary>
/// Settings › Automations, the timed rules: pause music shortly before a meeting, and start a focus session when
/// an event called "Focus" begins. (Quiet during calls lives with quiet hours; Join links with the reminder.)
/// </summary>
public sealed class AutomationService
{
    public static AutomationService Instance { get; } = new();

    /// <summary>Something was done for the user: title and detail for an alert.</summary>
    public event Action<string, string>? Acted;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly HashSet<string> _done = new();
    private List<CalendarEvent> _events = new();
    private DateTime _loaded;
    private DateTime _birthdaysChecked;

    private AutomationService() => _timer.Tick += (_, _) => Check();

    public void Start() => _timer.Start();

    /// <summary>A calendar event today with "birthday" in its name (title). Once per event per day.</summary>
    public event Action<string>? Birthday;

    private void CheckBirthdays()
    {
        var s = App.Settings;
        if (!s.Celebrations || DateTime.Now.Hour < 8) return; // not before the day starts
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in CalendarEvent.LoadAll().Where(e => e.Date.Date == DateTime.Today && e.Title.Contains("birthday", StringComparison.OrdinalIgnoreCase)))
        {
            var key = $"{today}|birthday|{e.Title}";
            if (s.Celebrated.Contains(key)) continue;
            s.Celebrated.RemoveAll(k => !k.StartsWith(today + "|")); // only today's matter
            s.Celebrated.Add(key);
            SettingsService.Save(notify: false);
            Birthday?.Invoke(e.Title);
        }
    }

    private void Check()
    {
        if (DateTime.Now - _birthdaysChecked > TimeSpan.FromMinutes(5))
        {
            _birthdaysChecked = DateTime.Now;
            CheckBirthdays();
        }
        var s = App.Settings;
        if (!s.AutoPauseMusic && !s.AutoFocusEvents) return;
        var now = DateTime.Now;
        if (now - _loaded > TimeSpan.FromMinutes(1))
        {
            if (now.Date != _loaded.Date) _done.Clear(); // keys are for today's events only
            _events = EventReminderService.Meetings().Where(e => e.Date.Date == now.Date).ToList();
            _loaded = now;
        }
        foreach (var e in _events)
        {
            if (!CalendarView.TryParseTime(e.Time, out var time)) continue;
            var start = e.Date.Date + time;
            var key = $"{start:yyyy-MM-dd HH:mm}|{e.Title}";

            var lead = TimeSpan.FromMinutes(Math.Max(0, s.AutoPauseLeadMin));
            if (s.AutoPauseMusic && now >= start - lead && now < start + TimeSpan.FromMinutes(2)
                && NowPlayingService.Instance.IsPlaying && _done.Add("pause|" + key))
            {
                NowPlayingService.Instance.PlayPauseCommand.Execute(null);
                Acted?.Invoke("Paused your music", $"{e.Title} starts {(now >= start ? "now" : $"at {start:t}")}");
            }

            var pomodoro = PomodoroService.Instance;
            if (s.AutoFocusEvents && e.Title.Contains("focus", StringComparison.OrdinalIgnoreCase)
                && now >= start && now < start + TimeSpan.FromMinutes(2) && !pomodoro.IsRunning && !pomodoro.IsBreak
                && _done.Add("focus|" + key))
            {
                pomodoro.ToggleCommand.Execute(null);
                Acted?.Invoke("Focus session started", $"{App.Settings.PomodoroWorkMin} minutes for {e.Title}");
            }
        }
    }
}
