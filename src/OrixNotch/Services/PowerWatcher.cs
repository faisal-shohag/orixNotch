using Forms = System.Windows.Forms;

namespace OrixNotch.Services;

/// <summary>
/// Charger plugged in or pulled, and low battery (20% and 10%, once each per discharge).
/// Polled by the notch's guard timer every couple of seconds; reading the power status is cheap.
/// </summary>
public sealed class PowerWatcher
{
    public static PowerWatcher Instance { get; } = new();

    /// <summary>Title, detail, and whether the PC is now charging.</summary>
    public event Action<string, string, bool>? Alert;

    private static readonly int[] LowThresholds = [10, 20]; // lowest first
    private Forms.PowerLineStatus? _line;
    private int _lowShown = 101; // lowest threshold already announced on this discharge

    public void Poll()
    {
        var ps = Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || ps.BatteryLifePercent > 1) return;
        if (ps.PowerLineStatus == Forms.PowerLineStatus.Unknown) return;
        var pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        var charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
        var was = _line;
        _line = ps.PowerLineStatus;

        if (charging)
        {
            _lowShown = 101;
        }
        else if (was != _line)
        {
            // Just unplugged: thresholds already passed count as told, the "On battery" alert says the level.
            _lowShown = LowThresholds.FirstOrDefault(t => pct <= t, 101);
        }

        if (was is null || !App.Settings.PowerAlerts) return; // first reading: nothing changed yet
        if (was != _line)
        {
            Alert?.Invoke(charging ? "Charging" : "On battery", $"{pct}%", charging);
            return;
        }
        if (charging) return;
        foreach (var t in LowThresholds)
        {
            if (pct > t || _lowShown <= t) continue;
            _lowShown = t;
            Alert?.Invoke("Battery low", $"{pct}% left · plug in soon", false);
            return;
        }
    }
}
