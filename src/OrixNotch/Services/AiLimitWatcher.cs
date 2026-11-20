using System.Windows.Threading;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>One "you're close to your limit" alert for an AI tool's session or weekly window.</summary>
/// <param name="Full">When it will be full at the pace of the last hour; null when it lasts until it resets (or pace unknown).</param>
public sealed record AiLimitAlert(string ToolId, string ToolName, BrandLogo Logo, string Window, double Percent, int Threshold, DateTime? Resets,
    DateTime? Full = null);

/// <summary>
/// Checks AI tool usage every few minutes and raises <see cref="Alert"/> once when a tool's session or
/// weekly window crosses 80% and again at 95%. Each alert fires once per window: the key includes the
/// window's reset time, and fired keys are saved so a restart doesn't repeat them.
/// </summary>
public sealed class AiLimitWatcher
{
    public static AiLimitWatcher Instance { get; } = new();

    public static readonly int[] Thresholds = [80, 95];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _checking;

    /// <summary>Latest usage read, shared with the idle notch so logs aren't read twice.</summary>
    public (DateTime At, List<ProviderUsage> Usage)? Latest { get; private set; }

    public event Action<AiLimitAlert>? Alert;

    // Recent readings per "tool|window", for the pace in the alert. In memory: a restart just starts over.
    private readonly Dictionary<string, List<(DateTime At, double Percent)>> _history = new();

    private AiLimitWatcher()
    {
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    /// <summary>Starts (or stops) checking to match the setting; the first check runs shortly after launch.</summary>
    public void Reconfigure()
    {
        if (!App.Settings.AiLimitAlerts)
        {
            _timer.Stop();
            return;
        }
        if (_timer.IsEnabled) return;
        _timer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TestMode ? 5 : 30) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            _ = CheckAsync();
        };
        first.Start();
    }

    // ORIXNOTCH_AILIMIT_TEST: alert at 1% / 2% so the peek can be tried without a near-full plan.
    private static bool TestMode => Environment.GetEnvironmentVariable("ORIXNOTCH_AILIMIT_TEST") == "1";

    private async Task CheckAsync()
    {
        if (_checking || !App.Settings.AiLimitAlerts) return;
        _checking = true;
        try
        {
            var usage = await AiUsageService.LoadAsync();
            Latest = (DateTime.Now, usage);
            var now = DateTime.Now;
            var alerted = App.Settings.AiLimitAlerted;
            // Forget keys whose window has reset (the reset stamp is the key's last part).
            var pruned = alerted.RemoveAll(k => k.Split('|') is { Length: 4 } p &&
                                                long.TryParse(p[3], out var ticks) && ticks > 0 && new DateTime(ticks) < now);
            var fired = new List<AiLimitAlert>();
            foreach (var u in usage)
            {
                if (u.Id.Length == 0) continue;
                var name = u.Name.Split(" · ")[0];
                foreach (var (window, percent, resets) in Windows(u))
                {
                    if (percent is not { } pct) continue;
                    var readings = Record($"{u.Id}|{window}", now, pct);
                    var threshold = Thresholds.Select(t => TestMode ? (t == 80 ? 1 : 2) : t).Where(t => pct >= t).DefaultIfEmpty(0).Max();
                    if (threshold == 0) continue;
                    // Without a reset time, a window is identified by the day (session) or week it was seen in.
                    var stamp = resets is { } r ? new DateTime(r.Ticks - r.Ticks % TimeSpan.TicksPerMinute).Ticks
                        : (window == "session" ? now.Date.AddDays(1) : now.Date.AddDays(7)).Ticks;
                    var key = $"{u.Id}|{window}|{threshold}|{stamp}";
                    if (alerted.Contains(key)) continue;
                    alerted.Add(key);
                    // A higher threshold already covers the lower one for this window.
                    fired.RemoveAll(a => a.ToolId == u.Id && a.Window == window);
                    var full = Project(readings, now);
                    fired.Add(new AiLimitAlert(u.Id, name, u.Logo, window, pct, threshold, resets,
                        full is { } f && (resets is not { } rs || f < rs) ? f : null));
                }
            }
            if (fired.Count > 0 || pruned > 0) SettingsService.Save(notify: false);
            foreach (var alert in fired) Alert?.Invoke(alert);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _checking = false;
        }
    }

    private List<(DateTime At, double Percent)> Record(string key, DateTime now, double percent)
    {
        if (!_history.TryGetValue(key, out var list)) _history[key] = list = new();
        if (list.Count > 0 && percent < list[^1].Percent) list.Clear(); // the window reset
        list.Add((now, percent));
        list.RemoveAll(r => now - r.At > TimeSpan.FromHours(1));
        return list;
    }

    /// <summary>
    /// When the window reaches 100% at the pace of these readings (a least-squares line through them), or null when
    /// there's too little to go on (under 10 minutes of readings) or usage isn't rising.
    /// </summary>
    public static DateTime? Project(IReadOnlyList<(DateTime At, double Percent)> readings, DateTime now)
    {
        if (readings.Count < 2 || readings[^1].At - readings[0].At < TimeSpan.FromMinutes(10)) return null;
        var t0 = readings[0].At;
        var xs = readings.Select(r => (r.At - t0).TotalMinutes).ToArray();
        var ys = readings.Select(r => r.Percent).ToArray();
        double mx = xs.Average(), my = ys.Average();
        var den = xs.Sum(x => (x - mx) * (x - mx));
        var slope = den == 0 ? 0 : xs.Zip(ys).Sum(p => (p.First - mx) * (p.Second - my)) / den; // % per minute
        if (slope <= 0.001) return null;
        var minutes = (100 - readings[^1].Percent) / slope;
        return minutes > 7 * 24 * 60 ? null : now.AddMinutes(Math.Max(0, minutes));
    }

#if DEBUG
    static AiLimitWatcher()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        // 60% → 80% over 20 minutes: 1%/min, so 20 more minutes to 100%.
        var rising = new List<(DateTime, double)> { (t, 60), (t.AddMinutes(10), 70), (t.AddMinutes(20), 80) };
        System.Diagnostics.Debug.Assert(Project(rising, t.AddMinutes(20)) == t.AddMinutes(40));
        System.Diagnostics.Debug.Assert(Project([(t, 80), (t.AddMinutes(20), 80)], t.AddMinutes(20)) is null); // flat
        System.Diagnostics.Debug.Assert(Project([(t, 70), (t.AddMinutes(5), 80)], t.AddMinutes(5)) is null);   // too short
    }
#endif

    /// <summary>The limited windows a tool reports: session and weekly, or the first metered bar (Cursor, Antigravity).</summary>
    private static IEnumerable<(string Window, double? Percent, DateTime? Resets)> Windows(ProviderUsage u)
    {
        if (u.SessionPercent is not null || u.WeeklyPercent is not null)
        {
            yield return ("session", u.SessionPercent, u.SessionResets);
            yield return ("weekly", u.WeeklyPercent, u.WeeklyResets);
            yield break;
        }
        if (u.Meters.FirstOrDefault(m => m.Percent is not null) is { } meter)
            yield return ("plan", meter.Percent, null);
    }
}
