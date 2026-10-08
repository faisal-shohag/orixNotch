using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using OrixNotch.Interop;
using OrixNotch.Shell;
using OrixNotch.Tools;
using Forms = System.Windows.Forms;

namespace OrixNotch.Services;

/// <summary>One piece of idle-notch information: a glyph (or the avatar) and a short value.</summary>
/// <param name="Logo">A brand mark drawn instead of <paramref name="Glyph"/> (AI usage).</param>
public sealed record IdleItem(string Id, AppIcon? Glyph, string Value, string ValueBrush = "TextBrush", bool Avatar = false, BrandLogo? Logo = null);

/// <summary>
/// Supplies the closed notch with the items the user picked in Settings → Display → Idle notch,
/// polling only what is selected: local values every second, files every minute, web data every few minutes.
/// </summary>
public sealed class IdleInfoService
{
    /// <summary>Every item the user can pick, in display order: (id, settings label).</summary>
    public static readonly (string Id, string Name)[] Catalog =
    [
        ("clock", "Clock"),
        ("date", "Date"),
        ("weather", "Weather"),
        ("nextEvent", "Next event"),
        ("todos", "Open to-dos"),
        ("battery", "Battery"),
        ("cpu", "CPU & memory"),
        ("network", "Network speed"),
        ("stock", "Stock ticker"),
        ("aiUsage", "AI usage"),
        ("clipboard", "Clipboard"),
        ("shelf", "Shelf"),
        ("streak", "Focus sessions today"),
        ("avatar", "Profile picture"),
    ];

    public static readonly string[] Layouts = ["Rotate", "Split", "Row"];

    // Declared after Catalog/Layouts: static initialisers run in order and the constructor reads them.
    public static IdleInfoService Instance { get; } = new();

    /// <summary>Raised when any value changes (at most once a second).</summary>
    public event Action? Changed;

    public IReadOnlyList<IdleItem> Items { get; private set; } = [];

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastFiles = DateTime.MinValue;
    private DateTime _lastWeb = DateTime.MinValue;
    private DateTime _lastAi = DateTime.MinValue;

    // Sampled / fetched state
    private long _lastIdle, _lastKernel, _lastUser;
    private double _cpu;
    private long _lastRx = -1;
    private DateTime _lastRxAt;
    private double _rxPerSec;
    private int _openTodos;
    private CalendarEvent? _nextEvent;
    private TimeSpan _nextAt;
    private (AppIcon Icon, string Temp)? _weather;
    private readonly Dictionary<string, (double Price, double ChangePct)> _quotes = new();
    private int _tickerIndex;
    /// <summary>AI tools that report a usage percentage, in AI Usage order.</summary>
    private List<(string Id, string Name, BrandLogo Logo, double Percent)> _ai = new();

    /// <summary>AI tools that can appear on the idle notch (id, short name, logo) — for the Settings checklist.</summary>
    public IReadOnlyList<(string Id, string Name, BrandLogo Logo)> DetectedAiTools => _ai.Select(a => (a.Id, a.Name, a.Logo)).ToList();

    /// <summary>Raised when the list of detected AI tools changes (Settings refreshes its checklist).</summary>
    public event Action? AiToolsChanged;

    private IdleInfoService()
    {
        _tick.Tick += (_, _) => Update();
        Reconfigure();
    }

    private static HashSet<string> Selected => App.Settings.IdleItems.ToHashSet();

    /// <summary>Call after the selection changes in Settings.</summary>
    public void Reconfigure()
    {
        _lastFiles = _lastWeb = _lastAi = DateTime.MinValue; // refetch what's newly selected
        if (App.Settings.IdleItems.Count > 0) _tick.Start();
        else _tick.Stop();
        Update();
    }

    private void Update()
    {
        var sel = Selected;
        var now = DateTime.Now;

        if (sel.Contains("cpu")) SampleCpu();
        if (sel.Contains("network")) SampleNetwork();
        if ((sel.Contains("todos") || sel.Contains("nextEvent")) && now - _lastFiles > TimeSpan.FromSeconds(30))
        {
            _lastFiles = now;
            LoadFiles();
        }
        if ((sel.Contains("weather") || sel.Contains("stock")) && now - _lastWeb > TimeSpan.FromMinutes(10))
        {
            _lastWeb = now;
            if (sel.Contains("weather")) _ = FetchWeatherAsync();
            if (sel.Contains("stock")) _ = FetchStocksAsync();
        }
        if (sel.Contains("aiUsage") && now - _lastAi > TimeSpan.FromMinutes(5))
        {
            _lastAi = now;
            _ = FetchAiAsync();
        }
        if (sel.Contains("stock") && now.Second % 6 == 0) _tickerIndex++; // cycle tickers

        Items = Catalog.Select(c => c.Id).Where(sel.Contains)
            .SelectMany(id => id == "aiUsage" ? BuildAi() : [Build(id)])
            .OfType<IdleItem>()
            .ToList();
        Changed?.Invoke();
    }

    /// <summary>Forces a refresh of the file-backed items (to-dos / events) on the next tick.</summary>
    public void InvalidateFiles() => _lastFiles = DateTime.MinValue;

    private IdleItem? Build(string id)
    {
        var culture = CultureInfo.CurrentCulture;
        var now = DateTime.Now;
        switch (id)
        {
            case "clock":
                return new IdleItem(id, AppIcon.Clock, now.ToString("t", culture));
            case "date":
                return new IdleItem(id, AppIcon.CalendarDays, now.ToString("ddd, MMM d", culture));
            case "weather":
                return _weather is { } w ? new IdleItem(id, w.Icon, w.Temp) : null;
            case "nextEvent":
                return _nextEvent is { } e
                    ? new IdleItem(id, AppIcon.CalendarDays, $"{e.Title} {(DateTime.Today + _nextAt).ToString("t", culture)}")
                    : new IdleItem(id, AppIcon.CalendarDays, "No more events", "SubTextBrush");
            case "todos":
                return new IdleItem(id, AppIcon.ListBullet, _openTodos == 0 ? "All done" : $"{_openTodos} open",
                    _openTodos == 0 ? "SubTextBrush" : "TextBrush");
            case "battery":
            {
                var ps = Forms.SystemInformation.PowerStatus;
                if (ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || ps.BatteryLifePercent > 1) return null;
                var pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                var charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
                return new IdleItem(id, charging ? AppIcon.Bolt : AppIcon.BatteryFull, $"{pct}%",
                    charging ? "GoodBrush" : pct < 20 ? "AccentBrush" : "TextBrush");
            }
            case "cpu":
            {
                var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
                var ram = Win32.GlobalMemoryStatusEx(ref mem) ? mem.dwMemoryLoad : 0;
                return new IdleItem(id, AppIcon.Cpu, $"{_cpu:0}% · {ram}%");
            }
            case "network":
                return new IdleItem(id, AppIcon.ArrowDown, Rate(_rxPerSec));
            case "stock":
            {
                var tickers = App.Settings.StockTickers.Where(_quotes.ContainsKey).ToList();
                if (tickers.Count == 0) return null;
                var t = tickers[_tickerIndex % tickers.Count];
                var (price, change) = _quotes[t];
                var arrow = change >= 0 ? "▲" : "▼";
                return new IdleItem(id, AppIcon.ChartBar,
                    $"{t} {price.ToString(price >= 1000 ? "0" : "0.00", culture)} {arrow}{Math.Abs(change):0.0}%",
                    change >= 0 ? "GoodBrush" : "BadBrush");
            }
            case "clipboard":
                var clips = ClipboardService.Instance.Items.Count;
                return new IdleItem(id, AppIcon.ClipboardList, clips == 1 ? "1 clip" : $"{clips} clips");
            case "shelf":
                var files = ShelfService.Instance.Items.Count;
                return files == 0 ? null : new IdleItem(id, AppIcon.ArchiveBox, files == 1 ? "1 file" : $"{files} files");
            case "streak":
                var done = PomodoroService.Instance.Completed;
                return new IdleItem(id, AppIcon.Fire, done == 1 ? "1 today" : $"{done} today");
            case "avatar":
                return new IdleItem(id, null, ProfileService.FirstName, Avatar: true);
        }
        return null;
    }

    /// <summary>At most "00.0 MB/s" wide, so the notch slot reserved for it never overflows.</summary>
    /// <summary>
    /// AI usage items: Auto = the one tool closest to its limit; Choose = one item per picked tool
    /// (ids "aiUsage:&lt;tool&gt;" so layouts rotate and size them separately).
    /// </summary>
    private IEnumerable<IdleItem?> BuildAi()
    {
        static IdleItem Item(string id, (string Id, string Name, BrandLogo Logo, double Percent) t) =>
            new(id, AppIcon.Sparkles, $"{t.Name} {t.Percent:0}%", t.Percent >= 80 ? "AccentBrush" : "TextBrush", Logo: t.Logo);

        if (_ai.Count == 0) return [];
        if (App.Settings.IdleAiMode != "Choose")
            return [Item("aiUsage", _ai.MaxBy(t => t.Percent))];
        var chosen = App.Settings.IdleAiTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _ai.Where(t => chosen.Contains(t.Id)).Select(t => Item("aiUsage:" + t.Id, t));
    }

    private static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 100 * 1024 * 1024 => $"{bytesPerSec / 1024 / 1024:0} MB/s",
        >= 1000 * 1024 => $"{bytesPerSec / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bytesPerSec / 1024:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };

    private void SampleCpu()
    {
        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user)) return;
        if (_lastKernel != 0)
        {
            var idleD = idle - _lastIdle;
            var total = (kernel - _lastKernel) + (user - _lastUser);
            if (total > 0) _cpu = Math.Clamp(100.0 * (total - idleD) / total, 0, 100);
        }
        (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);
    }

    private void SampleNetwork()
    {
        long rx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                rx += nic.GetIPv4Statistics().BytesReceived;
            }
        }
        catch
        {
            return;
        }
        var now = DateTime.Now;
        if (_lastRx >= 0)
        {
            var secs = (now - _lastRxAt).TotalSeconds;
            if (secs > 0) _rxPerSec = Math.Max(0, (rx - _lastRx) / secs);
        }
        _lastRx = rx;
        _lastRxAt = now;
    }

    private void LoadFiles()
    {
        _openTodos = Storage.Load<List<TodoItem>>("todos.json").Count(t => !t.Done);
        var now = DateTime.Now.TimeOfDay;
        _nextEvent = null;
        foreach (var e in Storage.Load<List<CalendarEvent>>("events.json")
                     .Where(e => e.Date.Date == DateTime.Today)
                     .OrderBy(e => e.Time))
        {
            if (CalendarView.TryParseTime(e.Time, out var t) && t >= now)
            {
                _nextEvent = e;
                _nextAt = t;
                break;
            }
        }
    }

    private async Task FetchWeatherAsync()
    {
        try
        {
            var city = App.Settings.WeatherCity;
            using var geo = JsonDocument.Parse(await Http.Client.GetStringAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name={Uri.EscapeDataString(city)}"));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return;
            var lat = results[0].GetProperty("latitude").GetDouble();
            var lon = results[0].GetProperty("longitude").GetDouble();
            var unit = App.Settings.UseFahrenheit ? "&temperature_unit=fahrenheit" : "";
            using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,is_day&timezone=auto{unit}")));
            var cur = doc.RootElement.GetProperty("current");
            _weather = (WeatherIcon(cur.GetProperty("weather_code").GetInt32(), cur.GetProperty("is_day").GetInt32() == 1),
                $"{Math.Round(cur.GetProperty("temperature_2m").GetDouble()):0}°");
        }
        catch
        {
            // Offline: keep the last value (or skip the item).
        }
    }

    private async Task FetchStocksAsync()
    {
        foreach (var symbol in App.Settings.StockTickers.Take(6))
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(
                    $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=1d"));
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                var price = meta.GetProperty("regularMarketPrice").GetDouble();
                var prev = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble() : price;
                _quotes[symbol] = (price, prev == 0 ? 0 : (price - prev) / prev * 100);
            }
            catch
            {
                // Skip unavailable tickers.
            }
        }
    }

    private async Task FetchAiAsync()
    {
        try
        {
            var usage = await AiUsageService.LoadAsync();
            // Each tool's live plan percentage: its session window, else its first metered bar (Cursor, Antigravity).
            // Tools without a percentage (DeepSeek balance, Perplexity) can't be shown as "N%".
            var before = string.Join(",", _ai.Select(a => a.Id));
            _ai = usage
                .Select(u => (u.Id, Name: u.Name.Split(' ', '·')[0], u.Logo, Percent: u.SessionPercent ?? u.Meters.FirstOrDefault(m => m.Percent is not null)?.Percent))
                .Where(x => x.Percent is not null && x.Id.Length > 0)
                .Select(x => (x.Id, x.Name, x.Logo, x.Percent!.Value))
                .ToList();
            if (string.Join(",", _ai.Select(a => a.Id)) != before) AiToolsChanged?.Invoke();
        }
        catch
        {
            _ai = new();
        }
    }

    /// <summary>WMO weather codes → app icon: clear sky shows sun/moon, everything else a cloud.</summary>
    private static AppIcon WeatherIcon(int code, bool day) => code switch
    {
        0 or 1 => day ? AppIcon.Sun : AppIcon.Moon,
        _ => AppIcon.Cloud,
    };
}
