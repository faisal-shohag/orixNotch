using System.Windows.Threading;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>One "you're close to your limit" alert for an AI tool's session or weekly window.</summary>
public sealed record AiLimitAlert(string ToolId, string ToolName, BrandLogo Logo, string Window, double Percent, int Threshold, DateTime? Resets);

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
                    fired.Add(new AiLimitAlert(u.Id, name, u.Logo, window, pct, threshold, resets));
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
