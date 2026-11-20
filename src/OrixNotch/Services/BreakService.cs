using System.Windows.Threading;
using OrixNotch.Interop;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>One kind of break reminder: its interval of active use and what the alert says.</summary>
public sealed record BreakKind(string Id, int Minutes, AppIcon Icon, string Title, string Sub);

/// <summary>
/// Break reminders (20-20-20 eyes, stand up, water), each on its own switch. Only active use counts: time away
/// (no input for 5 minutes) is a break and resets them. A due reminder waits out full screen, quiet hours and focus
/// sessions instead of being counted as a missed alert.
/// </summary>
public sealed class BreakService
{
    public static BreakService Instance { get; } = new();

    public static readonly BreakKind[] Kinds =
    [
        new("eyes", 20, AppIcon.Monitor, "Rest your eyes", "Look at something 20 feet (6 m) away for 20 seconds."),
        new("stand", 60, AppIcon.User, "Time to stand up", "An hour at the screen. Stretch for a minute."),
        new("water", 90, AppIcon.Drop, "Drink some water", "A glass now keeps the afternoon slump away."),
    ];

    // ORIXNOTCH_BREAK_TEST=1: minutes count as seconds, checked every 5 s, so the alerts can be tried quickly.
    private static bool TestMode => Environment.GetEnvironmentVariable("ORIXNOTCH_BREAK_TEST") == "1"; // properties: Instance is built first
    private static int TickSeconds => TestMode ? 5 : 30;
    private static int UnitSeconds => TestMode ? 1 : 60;
    private static readonly TimeSpan AwayResets = TimeSpan.FromMinutes(5);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(TickSeconds) };
    private readonly Dictionary<string, int> _active = new(); // seconds of active use since the last break, per kind

    public event Action<BreakKind>? Due;

    private BreakService() => _timer.Tick += (_, _) => Tick();

    public static bool IsOn(string id) => App.Settings.BreakReminders.Contains(id);

    /// <summary>Starts or stops to match the switches.</summary>
    public void Reconfigure()
    {
        if (App.Settings.BreakReminders.Count > 0) _timer.Start();
        else _timer.Stop();
        // A kind switched off forgets its count, so switching it back on starts fresh instead of firing at once.
        foreach (var id in _active.Keys.Where(id => !IsOn(id)).ToList()) _active.Remove(id);
    }

    private void Tick()
    {
        if (Win32.IdleTime() >= AwayResets)
        {
            _active.Clear(); // they were away: that was the break
            return;
        }
        var blocked = Win32.FullscreenKind() != 0 || NotchWindow.QuietActive() || PomodoroService.Instance.Focusing;
        foreach (var kind in Kinds.Where(k => IsOn(k.Id)))
        {
            var seconds = _active[kind.Id] = _active.GetValueOrDefault(kind.Id) + TickSeconds;
            if (seconds < kind.Minutes * UnitSeconds || blocked) continue;
            _active[kind.Id] = 0;
            Due?.Invoke(kind);
        }
    }
}
