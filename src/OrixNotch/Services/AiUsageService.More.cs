using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>A labelled bar on a usage card: "Label … 42% used", bar, then left/right captions.</summary>
public sealed record UsageMeter(string Label, double? Percent, string Value, string Left, string Right);

/// <summary>
/// Usage for AI tools beyond Claude Code and Codex. Each loader returns null when the tool isn't on
/// this PC and never throws. Nothing here sends data anywhere except each tool's own service, using
/// the sign-in the tool itself already saved (Cursor → cursor.com, DeepSeek → api.deepseek.com).
/// </summary>
public static partial class AiUsageService
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // ---------------------------------------------------------------- Cursor

    /// <summary>
    /// Cursor: sign-in token from its local state DB, then the dashboard's usage summary
    /// (undocumented <c>GET cursor.com/api/usage-summary</c>, cookie auth).
    /// </summary>
    private static async Task<ProviderUsage?> LoadCursorAsync()
    {
        try
        {
            // ORIXNOTCH_CURSOR_DB: test hook pointing at a stand-in state DB.
            var db = Environment.GetEnvironmentVariable("ORIXNOTCH_CURSOR_DB") is { Length: > 0 } testDb
                ? testDb
                : Path.Combine(AppData, "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db)) return null;
            var token = ReadVsCodeState(db, "cursorAuth/accessToken");
            var membership = ReadVsCodeState(db, "cursorAuth/stripeMembershipType");
            var usage = new ProviderUsage
            {
                Id = "cursor",
                Name = "Cursor",
                Logo = BrandLogo.Cursor,
                Badge = string.IsNullOrEmpty(membership) ? "Signed out" : Title(membership),
            };
            if (string.IsNullOrEmpty(token) || JwtClaims(token) is not { } claims)
            {
                usage.Note = "Sign in to Cursor to see usage.";
                return usage;
            }
            if (claims.Expires is { } exp && exp < DateTimeOffset.UtcNow)
            {
                // Cursor refreshes its token when it runs; we never refresh it ourselves.
                usage.Note = $"Cursor's sign-in expired {exp.LocalDateTime:MMM d}. Open Cursor once to see usage.";
                return usage;
            }
            var userId = claims.UserId;

            using var request = new HttpRequestMessage(HttpMethod.Get, CursorBase + "/api/usage-summary");
            request.Headers.Add("Cookie", $"WorkosCursorSessionToken={userId}%3A%3A{token}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await Http.Client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                usage.Note = "Usage unavailable — open Cursor once to refresh its sign-in.";
                return usage;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("membershipType", out var mt) && mt.ValueKind == JsonValueKind.String)
                usage.Badge = Title(mt.GetString()!);
            var cycleEnd = root.TryGetProperty("billingCycleEnd", out var end) && end.ValueKind == JsonValueKind.String &&
                           DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)
                ? e.LocalDateTime : (DateTime?)null;
            var resets = cycleEnd is { } c ? $"Resets {c:MMM d}" : "This billing cycle";

            if (root.TryGetProperty("individualUsage", out var individual))
            {
                if (individual.TryGetProperty("plan", out var plan) && Num(plan, "limit") is { } limit && limit > 0)
                {
                    var used = Num(plan, "used") ?? 0;
                    var pct = Math.Clamp(100 * used / limit, 0, 100);
                    usage.Meters.Add(new UsageMeter("Included usage", pct, $"{pct:0}% used", resets, $"{100 - pct:0}% left"));
                }
                if (individual.TryGetProperty("onDemand", out var onDemand) && Num(onDemand, "used") is { } spent && spent > 0)
                {
                    var cap = Num(onDemand, "limit");
                    usage.Meters.Add(new UsageMeter("On-demand", cap is > 0 ? Math.Clamp(100 * spent / cap.Value, 0, 100) : null,
                        Dollars(spent / 100), resets, cap is > 0 ? $"of {Dollars(cap.Value / 100)}" : "No limit"));
                }
            }
            if (root.TryGetProperty("isUnlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True && usage.Meters.Count == 0)
                usage.Note = "Unlimited plan.";
            if (usage.Meters.Count == 0 && usage.Note is null) usage.Note = "No usage this billing cycle.";
            return usage;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    // Test hook: point at a local mock instead of cursor.com.
    private static string CursorBase =>
        Environment.GetEnvironmentVariable("ORIXNOTCH_CURSOR_BASE") is { Length: > 0 } b ? b : "https://cursor.com";

    /// <summary>User id ("auth0|user_123" → "user_123") and expiry from a JWT's payload; null if unreadable.</summary>
    private static (string UserId, DateTimeOffset? Expires)? JwtClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var sub = doc.RootElement.TryGetProperty("sub", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(sub)) return null;
            DateTimeOffset? exp = doc.RootElement.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()) : null;
            return (sub.Split('|').Last(), exp);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Antigravity

    /// <summary>
    /// Antigravity keeps the per-model quota it last fetched in its state DB
    /// (<c>antigravityUnifiedStateSync.userStatus</c>, base64 protobuf): model name + remaining fraction + reset time.
    /// </summary>
    private static ProviderUsage? LoadAntigravity()
    {
        try
        {
            var db = Path.Combine(AppData, "Antigravity", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db)) return null;
            var usage = new ProviderUsage { Id = "antigravity", Name = "Antigravity", Logo = BrandLogo.Antigravity, Badge = "Local" };
            var raw = ReadVsCodeState(db, "antigravityUnifiedStateSync.userStatus");
            if (string.IsNullOrEmpty(raw))
            {
                usage.Note = "Open Antigravity and sign in to see quota.";
                return usage;
            }

            var models = AntigravityQuota(Convert.FromBase64String(raw));
            if (models.Count == 0)
            {
                usage.Note = "No quota information yet.";
                return usage;
            }

            var now = DateTime.Now;
            var lastReset = models.Max(m => m.Reset);
            // A reset time already in the past means the window has refilled since Antigravity last synced.
            var stale = lastReset is { } r && r < now;
            if (stale) usage.Badge = "Cached";
            // The card fits three rows: three models, or two models plus a note.
            var shown = stale || models.Count > 3 ? 2 : 3;
            foreach (var m in models.Select(m => (m.Name, Remaining: m.Reset is { } t && t < now ? 1.0 : m.Remaining, m.Reset))
                         .OrderBy(m => m.Remaining).ThenBy(m => m.Name).Take(shown))
            {
                var used = Math.Clamp((1 - m.Remaining) * 100, 0, 100);
                var left = m.Reset is { } t && t > now ? $"Resets {t:ddd h:mm tt}" : "Full";
                usage.Meters.Add(new UsageMeter(m.Name, used, $"{used:0}% used", left, $"{100 - used:0}% left"));
            }
            var more = models.Count > shown ? $"+{models.Count - shown} more models." : "";
            usage.Note = stale
                ? $"{more} As of Antigravity's last sync (before {lastReset:MMM d}); quotas have reset since.".Trim()
                : more.Length > 0 ? more : null;
            return usage;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>
    /// Walks the userStatus protobuf: outer {1: {2: {1: base64(status)}}}; status field 33 lists models,
    /// each {1: name, 15: {1: remainingFraction (float), 2: {1: resetTime (unix s)}}}.
    /// </summary>
    internal static List<(string Name, double Remaining, DateTime? Reset)> AntigravityQuota(byte[] blob)
    {
        var result = new List<(string, double, DateTime?)>();
        var outer = Proto.Bytes(blob, 1);
        var holder = outer is null ? null : Proto.Bytes(outer, 2);
        var encoded = holder is null ? null : Proto.Bytes(holder, 1);
        if (encoded is null) return result;
        var status = Convert.FromBase64String(Encoding.ASCII.GetString(encoded));
        var plan = Proto.Bytes(status, 33);
        if (plan is null) return result;
        foreach (var model in Proto.All(plan, 1))
        {
            var name = Proto.Bytes(model, 1) is { } n ? Encoding.UTF8.GetString(n) : null;
            var quota = Proto.Bytes(model, 15);
            if (name is null || quota is null) continue;
            var remaining = Proto.Fixed32(quota, 1) ?? 1f;
            var resetMsg = Proto.Bytes(quota, 2);
            DateTime? reset = resetMsg is not null && Proto.Varint(resetMsg, 1) is { } secs and > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)secs).LocalDateTime : null;
            result.Add((name, remaining, reset));
        }
        return result;
    }

    /// <summary>Just enough protobuf wire-format reading for the fields above.</summary>
    private static class Proto
    {
        private static IEnumerable<(int Field, int Wire, ulong Value, byte[]? Bytes)> Fields(byte[] b)
        {
            var i = 0;
            while (i < b.Length)
            {
                var key = ReadVarint(b, ref i);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                switch (wire)
                {
                    case 0: yield return (field, wire, ReadVarint(b, ref i), null); break;
                    case 1: i += 8; break;
                    case 2:
                        var len = (int)ReadVarint(b, ref i);
                        if (len < 0 || i + len > b.Length) yield break;
                        yield return (field, wire, 0, b[i..(i + len)]);
                        i += len;
                        break;
                    case 5: yield return (field, wire, BitConverter.ToUInt32(b, i), null); i += 4; break;
                    default: yield break;
                }
            }
        }

        private static ulong ReadVarint(byte[] b, ref int i)
        {
            ulong result = 0;
            for (var shift = 0; i < b.Length && shift < 64; shift += 7)
            {
                var x = b[i++];
                result |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
            }
            return result;
        }

        public static byte[]? Bytes(byte[] b, int field) => Fields(b).FirstOrDefault(f => f.Field == field && f.Wire == 2).Bytes;
        public static IEnumerable<byte[]> All(byte[] b, int field) => Fields(b).Where(f => f.Field == field && f.Wire == 2).Select(f => f.Bytes!);
        public static ulong? Varint(byte[] b, int field) => Fields(b).Where(f => f.Field == field && f.Wire == 0).Select(f => (ulong?)f.Value).FirstOrDefault();

        public static float? Fixed32(byte[] b, int field) => Fields(b).Where(f => f.Field == field && f.Wire == 5)
            .Select(f => (float?)BitConverter.Int32BitsToSingle((int)f.Value)).FirstOrDefault();
    }

    // ---------------------------------------------------------------- DeepSeek API balance

    private static async Task<ProviderUsage?> LoadDeepSeekAsync()
    {
        if (AiService.LoadKeyFor(AiService.ProviderDeepSeek) is not { } key) return null;
        var usage = new ProviderUsage { Id = "deepseek", Name = "DeepSeek API", Logo = BrandLogo.DeepSeek, Badge = "API" };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AiService.BaseUrlFor(AiService.ProviderDeepSeek) + "/user/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await Http.Client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                usage.Note = "Couldn't read your balance. Check the key in Settings → Ask AI.";
                return usage;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var infos = doc.RootElement.TryGetProperty("balance_infos", out var b) && b.ValueKind == JsonValueKind.Array ? b.EnumerateArray().ToList() : [];
            var info = infos.FirstOrDefault(i => i.TryGetProperty("currency", out var c) && c.GetString() == "USD");
            if (info.ValueKind != JsonValueKind.Object && infos.Count > 0) info = infos[0];
            if (info.ValueKind != JsonValueKind.Object)
            {
                usage.Note = "No balance yet.";
                return usage;
            }
            var currency = info.TryGetProperty("currency", out var cur) ? cur.GetString() : "USD";
            string Amount(string name) => info.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                ? (currency == "USD" ? Dollars((double)d) : $"{d:0.00} {currency}") : "–";
            usage.Headline = Amount("total_balance");
            usage.HeadlineSub = $"Balance · granted {Amount("granted_balance")} · topped up {Amount("topped_up_balance")}";
            var available = doc.RootElement.TryGetProperty("is_available", out var a) && a.ValueKind == JsonValueKind.True;
            if (!available) usage.Note = "Balance too low to make requests.";
            return usage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            usage.Note = "Couldn't reach DeepSeek.";
            return usage;
        }
    }

    // ---------------------------------------------------------------- Perplexity (link only)

    /// <summary>Perplexity keeps no usage on the PC and has no public usage API: just a shortcut when the app is installed.</summary>
    private static ProviderUsage? LoadPerplexity()
    {
        var installed = Directory.Exists(Path.Combine(LocalAppData, "Programs", "Perplexity")) ||
                        Directory.Exists(Path.Combine(AppData, "Perplexity")) ||
                        (Directory.Exists(Path.Combine(LocalAppData, "Packages")) &&
                         Directory.EnumerateDirectories(Path.Combine(LocalAppData, "Packages"), "*Perplexity*").Any());
        if (!installed) return null;
        return new ProviderUsage
        {
            Id = "perplexity",
            Name = "Perplexity",
            Logo = BrandLogo.Perplexity,
            Badge = "App",
            Note = "Perplexity doesn't share usage with other apps.",
            LinkText = "Open account settings",
            LinkUrl = "https://www.perplexity.ai/settings/account",
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Reads one value from a VS Code-style state DB (Cursor, Antigravity), read-only.</summary>
    private static string? ReadVsCodeState(string dbPath, string key)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() switch
        {
            string s => s,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => null,
        };
    }

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Dollars(double value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].Replace('_', ' ');
}
