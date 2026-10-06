using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OrixNotch.Services;

public sealed class ProviderUsage
{
    public string Name { get; init; } = "";
    public long SessionTokens { get; set; }     // rolling 5 hours
    public long WeeklyTokens { get; set; }      // rolling 7 days
    public long TodayTokens { get; set; }
    public decimal TodayCost { get; set; }
    public decimal MonthCost { get; set; }
    public bool HasCost { get; init; }
    public DateTime? SessionStarted { get; set; }
    public long[] Daily { get; } = new long[14]; // index 13 = today
    /// <summary>Session-window (5h) tokens per model label. Claude only — Codex logs carry no model.</summary>
    public Dictionary<string, long> SessionModels { get; } = new();
}

/// <summary>
/// Token usage of local AI coding tools, computed only from their own session logs on this PC:
/// Claude Code (~/.claude/projects/**/*.jsonl) and Codex (~/.codex/sessions/**/*.jsonl).
/// No credentials are read and nothing is sent anywhere. Costs are estimates at list prices.
/// </summary>
public static class AiUsageService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static Task<List<ProviderUsage>> LoadAsync() => Task.Run(() =>
    {
        var result = new List<ProviderUsage>();
        var claude = LoadClaude();
        if (claude is not null) result.Add(claude);
        var codex = LoadCodex();
        if (codex is not null) result.Add(codex);
        return result;
    });

    private static IEnumerable<string> RecentFiles(string root, int days)
    {
        if (!Directory.Exists(root)) return [];
        var cutoff = DateTime.Now.AddDays(-days);
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Where(f => File.GetLastWriteTime(f) >= cutoff)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        // Logs are being appended to while we read, so share the file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static ProviderUsage? LoadClaude()
    {
        var files = RecentFiles(Path.Combine(Home, ".claude", "projects"), 31);
        if (!files.Any()) return null;

        var usage = new ProviderUsage { Name = "Claude Code", HasCost = true };
        var seen = new HashSet<string>();
        var now = DateTime.Now;
        var sessionStart = DateTime.MaxValue;

        foreach (var file in files)
        {
            IEnumerable<string> lines;
            try
            {
                lines = ReadLines(file).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                    var model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    if (model.StartsWith('<')) continue; // synthetic entries

                    // Each response can be logged more than once; count it once.
                    var id = (msg.TryGetProperty("id", out var mid) ? mid.GetString() : null) +
                             (root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null);
                    if (id.Length > 0 && !seen.Add(id)) continue;

                    if (!root.TryGetProperty("timestamp", out var ts) ||
                        !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)) continue;
                    var time = utc.ToLocalTime();

                    long Get(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                    var input = Get("input_tokens");
                    var output = Get("output_tokens");
                    var cacheWrite = Get("cache_creation_input_tokens");
                    var cacheRead = Get("cache_read_input_tokens");
                    var tokens = input + output + cacheWrite;
                    var cost = Cost(model, input, output, cacheWrite, cacheRead);

                    Add(usage, time, now, tokens, cost);
                    if (now - time <= TimeSpan.FromHours(5))
                    {
                        if (time < sessionStart) sessionStart = time;
                        var label = ModelLabel(model);
                        usage.SessionModels[label] = usage.SessionModels.TryGetValue(label, out var prior) ? prior + tokens : tokens;
                    }
                }
                catch (JsonException)
                {
                    // partially written line
                }
            }
        }

        usage.SessionStarted = sessionStart == DateTime.MaxValue ? null : sessionStart;
        return usage;
    }

    private static ProviderUsage? LoadCodex()
    {
        var files = RecentFiles(Path.Combine(Home, ".codex", "sessions"), 31);
        if (!files.Any()) return null;

        var usage = new ProviderUsage { Name = "Codex", HasCost = false };
        var now = DateTime.Now;
        var sessionStart = DateTime.MaxValue;
        foreach (var file in files)
        {
            IEnumerable<string> lines;
            try
            {
                lines = ReadLines(file).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (!line.Contains("token_count", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("payload", out var payload) ||
                        !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
                        !info.TryGetProperty("last_token_usage", out var last)) continue;
                    if (!root.TryGetProperty("timestamp", out var ts) ||
                        !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)) continue;
                    var time = utc.ToLocalTime();
                    var tokens = (last.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0) +
                                 (last.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0);
                    Add(usage, time, now, tokens, 0);
                    if (now - time <= TimeSpan.FromHours(5) && time < sessionStart) sessionStart = time;
                }
                catch (JsonException)
                {
                }
            }
        }
        usage.SessionStarted = sessionStart == DateTime.MaxValue ? null : sessionStart;
        return usage;
    }

    private static void Add(ProviderUsage usage, DateTime time, DateTime now, long tokens, decimal cost)
    {
        var age = now - time;
        if (age <= TimeSpan.FromHours(5)) usage.SessionTokens += tokens;
        if (age <= TimeSpan.FromDays(7)) usage.WeeklyTokens += tokens;
        if (age <= TimeSpan.FromDays(30)) usage.MonthCost += cost;
        if (time.Date == now.Date)
        {
            usage.TodayTokens += tokens;
            usage.TodayCost += cost;
        }
        var dayIndex = 13 - (int)(now.Date - time.Date).TotalDays;
        if (dayIndex is >= 0 and < 14) usage.Daily[dayIndex] += tokens;
    }

    /// <summary>Short display name for a Claude model id ("Fable", "Opus", ...).</summary>
    public static string ModelLabel(string model) => model switch
    {
        _ when model.Contains("fable") || model.Contains("mythos") => "Fable",
        _ when model.Contains("opus") => "Opus",
        _ when model.Contains("sonnet") => "Sonnet",
        _ when model.Contains("haiku") => "Haiku",
        _ => model.Length > 18 ? model[..18] : model,
    };

    /// <summary>Estimated USD at Anthropic list prices (per million tokens).</summary>
    private static decimal Cost(string model, long input, long output, long cacheWrite, long cacheRead)
    {
        var (inPrice, outPrice) = model switch
        {
            _ when model.Contains("fable") || model.Contains("mythos") => (10m, 50m),
            _ when model.Contains("opus-5-5") => (4m, 20m),
            _ when model.Contains("opus") => (5m, 25m),
            _ when model.Contains("sonnet-5") => (2m, 10m),
            _ when model.Contains("sonnet") => (3m, 15m),
            _ when model.Contains("haiku") => (1m, 5m),
            _ => (3m, 15m),
        };
        return (input * inPrice + output * outPrice + cacheWrite * inPrice * 1.25m + cacheRead * inPrice * 0.1m) / 1_000_000m;
    }
}
