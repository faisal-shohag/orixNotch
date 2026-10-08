using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OrixNotch.Shell;

namespace OrixNotch.Services;

public sealed class ProviderUsage
{
    /// <summary>Stable id ("claude", "codex", "cursor", …) used by settings.</summary>
    public string Id { get; init; } = "";
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

    // Plan usage limits as reported by the provider (null when unavailable).
    public double? SessionPercent { get; set; }
    public DateTime? SessionResets { get; set; }
    public double? WeeklyPercent { get; set; }
    public DateTime? WeeklyResets { get; set; }

    // Generic card parts, for tools that don't fit the session / weekly layout.
    public BrandLogo Logo { get; init; } = BrandLogo.OpenAi;
    /// <summary>Small badge next to the name; null = "Plan" / "Local" from the limits.</summary>
    public string? Badge { get; set; }
    public List<UsageMeter> Meters { get; } = new();
    public string? Headline { get; set; }
    public string? HeadlineSub { get; set; }
    public string? Note { get; set; }
    public string? LinkText { get; init; }
    public string? LinkUrl { get; init; }
    /// <summary>True for cards built from <see cref="Meters"/> / headline instead of the session + weekly layout.</summary>
    public bool IsGeneric => Meters.Count > 0 || Headline is not null || Note is not null || LinkUrl is not null;
}

/// <summary>
/// Token usage of local AI coding tools, computed only from their own session logs on this PC:
/// Claude Code (~/.claude/projects/**/*.jsonl) and Codex (~/.codex/sessions/**/*.jsonl).
/// Cursor, Antigravity, DeepSeek and Perplexity live in AiUsageService.More.cs.
/// Costs are estimates at list prices. Plan limit percentages come from Codex's own logs and,
/// for Claude, from Anthropic's usage endpoint using Claude Code's sign-in token
/// (~/.claude/.credentials.json) — the same data Claude Code's /usage shows. The token is
/// only ever sent to api.anthropic.com.
/// </summary>
public static partial class AiUsageService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static async Task<List<ProviderUsage>> LoadAsync()
    {
        // Every tool loads in parallel; ones that aren't on this PC come back null.
        var claudeTask = Task.Run(async () =>
        {
            var claude = LoadClaude();
            if (claude is not null) await LoadClaudeLimitsAsync(claude);
            return claude;
        });
        var codexTask = Task.Run(LoadCodex);
        var cursorTask = Task.Run(LoadCursorAsync);
        var antigravityTask = Task.Run(LoadAntigravity);
        var deepSeekTask = Task.Run(LoadDeepSeekAsync);
        var perplexityTask = Task.Run(LoadPerplexity);
        await Task.WhenAll(claudeTask, codexTask, cursorTask, antigravityTask, deepSeekTask, perplexityTask);
        return new[] { claudeTask.Result, codexTask.Result, cursorTask.Result, antigravityTask.Result, deepSeekTask.Result, perplexityTask.Result }
            .OfType<ProviderUsage>()
            .ToList();
    }

    /// <summary>Fills Claude's 5-hour and weekly plan utilization; leaves them null on any failure.</summary>
    private static async Task LoadClaudeLimitsAsync(ProviderUsage usage)
    {
        try
        {
            var credsPath = Path.Combine(Home, ".claude", ".credentials.json");
            if (!File.Exists(credsPath)) return;
            string? token;
            using (var creds = JsonDocument.Parse(await File.ReadAllTextAsync(credsPath)))
            {
                if (!creds.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return;
                token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
                // Expired: Claude Code refreshes it on its next run; don't try to refresh ourselves.
                if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number &&
                    DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) <= DateTimeOffset.UtcNow) return;
            }
            if (string.IsNullOrEmpty(token)) return;

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await Http.Client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            (usage.SessionPercent, usage.SessionResets) = Window(doc.RootElement, "five_hour");
            (usage.WeeklyPercent, usage.WeeklyResets) = Window(doc.RootElement, "seven_day");
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        static (double?, DateTime?) Window(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object ||
                !w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return (null, null);
            DateTime? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
                               DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.LocalDateTime
                : null;
            return (u.GetDouble(), resets);
        }
    }

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

        var usage = new ProviderUsage { Id = "claude", Name = "Claude Code", HasCost = true, Logo = BrandLogo.Claude };
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

        var usage = new ProviderUsage { Id = "codex", Name = "Codex · ChatGPT", HasCost = false, Logo = BrandLogo.OpenAi };
        var now = DateTime.Now;
        var sessionStart = DateTime.MaxValue;
        var limitsAt = DateTime.MinValue;
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
                    if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("timestamp", out var ts) ||
                        !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)) continue;
                    var time = utc.ToLocalTime();

                    // Newest rate-limit snapshot wins: primary = 5-hour window, secondary = weekly.
                    if (time > limitsAt && payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                    {
                        limitsAt = time;
                        (usage.SessionPercent, usage.SessionResets) = CodexWindow(limits, "primary", time);
                        (usage.WeeklyPercent, usage.WeeklyResets) = CodexWindow(limits, "secondary", time);
                    }

                    if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
                        !info.TryGetProperty("last_token_usage", out var last)) continue;
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
        // A snapshot whose window has already reset says nothing about current usage.
        if (usage.SessionResets is { } sr && sr <= now) (usage.SessionPercent, usage.SessionResets) = (0, null);
        if (usage.WeeklyResets is { } wr && wr <= now) (usage.WeeklyPercent, usage.WeeklyResets) = (0, null);
        return usage;
    }

    /// <summary>Reads one Codex rate-limit window; resets is given as epoch seconds or seconds from the log time.</summary>
    private static (double?, DateTime?) CodexWindow(JsonElement limits, string name, DateTime loggedAt)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object ||
            !w.TryGetProperty("used_percent", out var u) || u.ValueKind != JsonValueKind.Number) return (null, null);
        DateTime? resets = null;
        if (w.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.Number)
            resets = DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()).LocalDateTime;
        else if (w.TryGetProperty("resets_in_seconds", out var inSec) && inSec.ValueKind == JsonValueKind.Number)
            resets = loggedAt.AddSeconds(inSec.GetDouble());
        return (u.GetDouble(), resets);
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
