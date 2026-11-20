using System.Diagnostics;

namespace OrixNotch.Services;

/// <summary>
/// CPU use per app (processes grouped by name, so all of Chrome counts as one), from the change in
/// processor time between two samples. Sampled only while the System tool is open.
/// </summary>
public sealed class ProcessSampler
{
    private Dictionary<int, TimeSpan> _last = new();
    private DateTime _lastAt;

    /// <returns>The busiest apps, highest first: name, CPU % of the whole machine, process count.</returns>
    public List<(string Name, double Cpu, int Count)> Sample(int top)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastAt).TotalMilliseconds * Environment.ProcessorCount;
        var current = new Dictionary<int, TimeSpan>();
        var byName = new Dictionary<string, (double Ms, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0) continue; // System Idle Process
                    var cpu = p.TotalProcessorTime;
                    current[p.Id] = cpu;
                    var delta = _last.TryGetValue(p.Id, out var before) ? (cpu - before).TotalMilliseconds : 0;
                    var entry = byName.GetValueOrDefault(p.ProcessName);
                    byName[p.ProcessName] = (entry.Ms + Math.Max(0, delta), entry.Count + 1);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected system processes and ones that just exited.
                }
            }
        }
        var first = _last.Count == 0;
        _last = current;
        _lastAt = now;
        if (first || elapsed <= 0) return [];
        return byName.Select(kv => (kv.Key, Cpu: 100 * kv.Value.Ms / elapsed, kv.Value.Count))
            .Where(x => x.Cpu >= 0.1)
            .OrderByDescending(x => x.Cpu)
            .Take(top)
            .ToList();
    }

    /// <summary>Ends every process with this name. Returns how many could be ended.</summary>
    public static int EndApp(string name)
    {
        var ended = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    p.Kill();
                    ended++;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Elevated or protected: Task Manager as admin can.
                }
            }
        }
        return ended;
    }
}

/// <summary>
/// Free space on the system drive, one reading per day (disk-history.json), so the System tool can say
/// roughly when the drive fills up at the recent pace.
/// </summary>
public static class DiskHistory
{
    private const string FileName = "disk-history.json";
    private static DateTime _recorded;

    private static DateTime Day(string key) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MinValue;

    /// <summary>Saves today's reading once (cheap to call often).</summary>
    public static void Record(long freeBytes)
    {
        if (_recorded == DateTime.Today) return;
        _recorded = DateTime.Today;
        var history = Storage.Load<Dictionary<string, long>>(FileName);
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (history.ContainsKey(today)) return;
        history[today] = freeBytes;
        foreach (var old in history.Keys.Where(k => Day(k) < DateTime.Today.AddDays(-60)).ToList()) history.Remove(old);
        Storage.Save(FileName, history);
    }

    /// <summary>Days until the drive is full at the pace of the last month, or null when it isn't filling up.</summary>
    public static int? DaysUntilFull(long freeNow)
    {
        var history = Storage.Load<Dictionary<string, long>>(FileName)
            .Select(kv => (Day: Day(kv.Key), Free: kv.Value))
            .Where(x => x.Day >= DateTime.Today.AddDays(-30))
            .OrderBy(x => x.Day)
            .ToList();
        if (history.Count < 2) return null;
        var days = (DateTime.Today - history[0].Day).TotalDays;
        if (days < 3) return null; // too little history to call it a trend
        var perDay = (history[0].Free - freeNow) / days;
        if (perDay < 100L * 1024 * 1024) return null; // under 100 MB a day: not worth a warning
        var left = freeNow / perDay;
        return left < 365 ? (int)Math.Ceiling(left) : null;
    }
}
