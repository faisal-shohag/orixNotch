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
    /// <summary>Session-window (5h) tokens per model label. Claude and OpenCode — Codex logs carry no model.</summary>
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
/// OpenCode, Cursor, Antigravity, DeepSeek and Perplexity live in AiUsageService.More.cs.
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
        if (Environment.GetEnvironmentVariable("ORIXNOTCH_AIUSAGE_DEMO") == "1") return Demo(); // screenshots

        // Every tool loads in parallel; ones that aren't on this PC come back null.
        var claudeTask = Task.Run(async () =>
        {
            var claude = LoadClaude();
            if (claude is not null) await LoadClaudeLimitsAsync(claude);
            return claude;
        });
        var codexTask = Task.Run(LoadCodex);
        var openCodeTask = Task.Run(LoadOpenCode);
        var cursorTask = Task.Run(LoadCursorAsync);
        var antigravityTask = Task.Run(LoadAntigravityAsync);
        var deepSeekTask = Task.Run(LoadDeepSeekAsync);
        var perplexityTask = Task.Run(LoadPerplexity);
        await Task.WhenAll(claudeTask, codexTask, openCodeTask, cursorTask, antigravityTask, deepSeekTask, perplexityTask);
        return new[] { claudeTask.Result, codexTask.Result, openCodeTask.Result, cursorTask.Result, antigravityTask.Result, deepSeekTask.Result, perplexityTask.Result }
            .OfType<ProviderUsage>()
            .ToList();
    }

    /// <summary>Made-up usage for Store screenshots (ORIXNOTCH_AIUSAGE_DEMO=1); nothing is read from the PC.</summary>
    private static List<ProviderUsage> Demo()
    {
        var now = DateTime.Now;
        var claude = new ProviderUsage
        {
            Id = "claude", Name = "Claude Code", HasCost = true, Logo = BrandLogo.Claude,
            SessionTokens = 1_840_000, WeeklyTokens = 21_600_000, TodayTokens = 3_120_000, TodayCost = 18.42m, MonthCost = 412.75m,
            SessionStarted = now.AddHours(-2.2), SessionPercent = 42, SessionResets = now.AddHours(2.8),
            WeeklyPercent = 31, WeeklyResets = now.Date.AddDays(4).AddHours(15),
        };
        claude.SessionModels["Opus"] = 1_210_000;
        claude.SessionModels["Sonnet"] = 630_000;
        var codex = new ProviderUsage
        {
            Id = "codex", Name = "Codex · ChatGPT", Logo = BrandLogo.OpenAi,
            SessionTokens = 920_000, WeeklyTokens = 8_400_000, TodayTokens = 1_050_000,
            SessionStarted = now.AddHours(-1.1), SessionPercent = 18, SessionResets = now.AddHours(3.9),
            WeeklyPercent = 24, WeeklyResets = now.Date.AddDays(3).AddHours(9),
        };
        var cursor = new ProviderUsage { Id = "cursor", Name = "Cursor", Logo = BrandLogo.Cursor, Badge = "Pro" };
        cursor.Meters.Add(new UsageMeter("Included usage", 57, "57% used", $"Resets {now.Date.AddDays(12):MMM d}", "43% left"));
        cursor.Meters.Add(new UsageMeter("On-demand", 12, "$6.20", $"Resets {now.Date.AddDays(12):MMM d}", "of $50.00"));
        var deepSeek = new ProviderUsage
        {
            Id = "deepseek", Name = "DeepSeek API", Logo = BrandLogo.DeepSeek, Badge = "API",
            Headline = "$24.80", HeadlineSub = "Balance · granted $0.00 · topped up $24.80",
        };
        var shape = new[] { 0.35, 0.5, 0.42, 0.7, 0.62, 0.3, 0.25, 0.55, 0.8, 0.68, 0.74, 0.9, 0.6, 0.85 };
        for (var i = 0; i < 14; i++)
        {
            claude.Daily[i] = (long)(3_600_000 * shape[i]);
            codex.Daily[i] = (long)(1_400_000 * shape[(i + 5) % 14]);
        }
        return [claude, codex, cursor, deepSeek];
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

    /// <summary>
    /// Parsed entries per log file, kept between reads. The limit watcher reads every 5 minutes, and a month of logs can be
    /// hundreds of MB: an unchanged file isn't read again, and a growing one (the live session) only from where the last
    /// read stopped. A file that shrank was rewritten and is read from the start.
    /// </summary>
    private sealed class LogCache<T>(Func<string, T?> parse) where T : struct
    {
        private readonly Dictionary<string, (long Offset, List<T> Items)> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The entries of every file, in order; files no longer listed are forgotten.</summary>
        public List<List<T>> ReadAll(List<string> paths)
        {
            lock (_files)
            {
                foreach (var gone in _files.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToList()) _files.Remove(gone);
                var all = new List<List<T>>(paths.Count);
                foreach (var path in paths)
                {
                    try
                    {
                        all.Add(Read(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // deleted or locked while we looked: skip it this time
                    }
                }
                return all;
            }
        }

        private List<T> Read(string path)
        {
            _files.TryGetValue(path, out var cached);
            var (offset, items) = (cached.Offset, cached.Items ?? []);
            // Logs are being appended to while we read, so share the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset) (offset, items) = (0, []);
            if (stream.Length == offset) return items;
            stream.Position = offset;
            // Whole lines only: a partly written last line is left for the next read.
            var buffer = new byte[1 << 20];
            var filled = 0;
            int read;
            while ((read = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                filled += read;
                var newline = Array.LastIndexOf(buffer, (byte)'\n', filled - 1);
                if (newline < 0)
                {
                    if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // one very long line
                    continue;
                }
                foreach (var line in System.Text.Encoding.UTF8.GetString(buffer, 0, newline).Split('\n'))
                    if (parse(line) is { } item) items.Add(item);
                offset += newline + 1;
                filled -= newline + 1;
                Buffer.BlockCopy(buffer, newline + 1, buffer, 0, filled);
            }
            _files[path] = (offset, items);
            return items;
        }
    }

    /// <summary>One Claude Code response: <see cref="Id"/> is a hash of its ids (0 when it has none), for counting it once.</summary>
    private readonly record struct ClaudeEntry(long Id, DateTime Time, string Model, long Tokens, decimal Cost);

    private static readonly LogCache<ClaudeEntry> ClaudeLogs = new(ParseClaudeLine);

    private static ClaudeEntry? ParseClaudeLine(string line)
    {
        if (!line.Contains("\"usage\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;
            var model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            if (model.StartsWith('<')) return null; // synthetic entries

            var id = (msg.TryGetProperty("id", out var mid) ? mid.GetString() : null) +
                     (root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null);

            if (!root.TryGetProperty("timestamp", out var ts) ||
                !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)) return null;

            long Get(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            var input = Get("input_tokens");
            var output = Get("output_tokens");
            var cacheWrite = Get("cache_creation_input_tokens");
            var cacheRead = Get("cache_read_input_tokens");
            return new ClaudeEntry(id.Length > 0 ? Fnv64(id) : 0, utc.ToLocalTime(), string.Intern(model),
                input + output + cacheWrite, Cost(model, input, output, cacheWrite, cacheRead));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null; // partially written or odd line
        }
    }

    /// <summary>64-bit FNV-1a: stands in for the id string so a month of entries doesn't keep every id in memory.</summary>
    private static long Fnv64(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text) hash = (hash ^ c) * 1099511628211UL;
        return (long)hash;
    }

    private static ProviderUsage? LoadClaude()
    {
        var files = RecentFiles(Path.Combine(Home, ".claude", "projects"), 31).ToList();
        if (files.Count == 0) return null;

        var usage = new ProviderUsage { Id = "claude", Name = "Claude Code", HasCost = true, Logo = BrandLogo.Claude };
        var seen = new HashSet<long>();
        var now = DateTime.Now;
        var sessionStart = DateTime.MaxValue;

        foreach (var entries in ClaudeLogs.ReadAll(files))
            foreach (var e in entries)
            {
                // Each response can be logged more than once; count it once.
                if (e.Id != 0 && !seen.Add(e.Id)) continue;
                Add(usage, e.Time, now, e.Tokens, e.Cost);
                if (now - e.Time <= TimeSpan.FromHours(5))
                {
                    if (e.Time < sessionStart) sessionStart = e.Time;
                    var label = ModelLabel(e.Model);
                    usage.SessionModels[label] = usage.SessionModels.TryGetValue(label, out var prior) ? prior + e.Tokens : e.Tokens;
                }
            }

        usage.SessionStarted = sessionStart == DateTime.MaxValue ? null : sessionStart;
        return usage;
    }

    /// <summary>One Codex token_count line: its tokens (null without usage info) and the rate-limit snapshot it carried.</summary>
    private readonly record struct CodexEntry(DateTime Time, long? Tokens, bool HasLimits,
        (double?, DateTime?) Session, (double?, DateTime?) Weekly);

    private static readonly LogCache<CodexEntry> CodexLogs = new(ParseCodexLine);

    private static CodexEntry? ParseCodexLine(string line)
    {
        if (!line.Contains("token_count", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("timestamp", out var ts) ||
                !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)) return null;
            var time = utc.ToLocalTime();

            var hasLimits = payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object;
            var session = hasLimits ? CodexWindow(limits, "primary", time) : default;
            var weekly = hasLimits ? CodexWindow(limits, "secondary", time) : default;

            long? tokens = null;
            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("last_token_usage", out var last))
                tokens = (last.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0) +
                         (last.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0);
            return new CodexEntry(time, tokens, hasLimits, session, weekly);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static ProviderUsage? LoadCodex()
    {
        var files = RecentFiles(Path.Combine(Home, ".codex", "sessions"), 31).ToList();
        if (files.Count == 0) return null;

        var usage = new ProviderUsage { Id = "codex", Name = "Codex · ChatGPT", HasCost = false, Logo = BrandLogo.OpenAi };
        var now = DateTime.Now;
        var sessionStart = DateTime.MaxValue;
        var limitsAt = DateTime.MinValue;
        foreach (var entries in CodexLogs.ReadAll(files))
            foreach (var e in entries)
            {
                // Newest rate-limit snapshot wins: primary = 5-hour window, secondary = weekly.
                if (e.HasLimits && e.Time > limitsAt)
                {
                    limitsAt = e.Time;
                    (usage.SessionPercent, usage.SessionResets) = e.Session;
                    (usage.WeeklyPercent, usage.WeeklyResets) = e.Weekly;
                }
                if (e.Tokens is not { } tokens) continue;
                Add(usage, e.Time, now, tokens, 0);
                if (now - e.Time <= TimeSpan.FromHours(5) && e.Time < sessionStart) sessionStart = e.Time;
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
