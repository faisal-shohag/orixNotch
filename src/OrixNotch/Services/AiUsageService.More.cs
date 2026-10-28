using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>A labelled bar on a usage card: "Label … 42% used", bar, then left/right captions.</summary>
public sealed record UsageMeter(string Label, double? Percent, string Value, string Left, string Right);

/// <summary>
/// Usage for AI tools beyond Claude Code and Codex. Each loader returns null when the tool isn't on
/// this PC and never throws. Nothing here sends data anywhere except each tool's own service, using
/// the sign-in the tool itself already saved (Cursor → cursor.com, DeepSeek → api.deepseek.com).
/// OpenCode, Antigravity and Perplexity are read from this PC only.
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
            // Uninstalling Cursor leaves its data folder behind, with a sign-in that only Cursor itself can refresh.
            var installed = db != Path.Combine(AppData, "Cursor", "User", "globalStorage", "state.vscdb") ||
                            Directory.Exists(Path.Combine(LocalAppData, "Programs", "cursor")) ||
                            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cursor"));
            if (!installed) return null;
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
    /// Antigravity quota, live when the app is running: its <c>language_server.exe</c> answers <c>GetUserStatus</c>
    /// on localhost (port and CSRF token are on its command line). Otherwise the per-model quota an older,
    /// VS Code-based Antigravity last saved in its state DB (<c>antigravityUnifiedStateSync.userStatus</c>);
    /// the current app no longer saves it there.
    /// </summary>
    private static async Task<ProviderUsage?> LoadAntigravityAsync()
    {
        try
        {
            var usage = new ProviderUsage { Id = "antigravity", Name = "Antigravity", Logo = BrandLogo.Antigravity, Badge = "Live" };
            if (await AntigravityLiveAsync() is { } live)
            {
                if (live.Models.Count == 0) usage.Note = "No quota information yet.";
                else FillAntigravityMeters(usage, live.Models, stale: false, live.Tier);
                return usage;
            }

            // Not running: the newest state DB, from the old app or the interim "Antigravity IDE".
            var db = new[] { "Antigravity", "Antigravity IDE" }
                .Select(name => new FileInfo(Path.Combine(AppData, name, "User", "globalStorage", "state.vscdb")))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => (f.FullName, Raw: ReadVsCodeState(f.FullName, "antigravityUnifiedStateSync.userStatus")))
                .FirstOrDefault(d => !string.IsNullOrEmpty(d.Raw));
            var installed = Directory.Exists(Path.Combine(LocalAppData, "Programs", "Antigravity")) ||
                            Directory.Exists(Path.Combine(AppData, "Antigravity"));
            usage.Badge = "Local";
            if (db.Raw is null)
            {
                if (!installed) return null;
                usage.Note = "Open Antigravity and sign in to see quota.";
                return usage;
            }

            var models = AntigravityQuota(Convert.FromBase64String(db.Raw));
            if (models.Count == 0)
            {
                usage.Note = "No quota information yet.";
                return usage;
            }
            // A reset time already in the past means the window has refilled since Antigravity last synced.
            var stale = models.Max(m => m.Reset) is { } r && r < DateTime.Now;
            if (stale) usage.Badge = "Cached";
            FillAntigravityMeters(usage, models, stale, tier: null);
            return usage;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>The lowest-remaining models as meters; the card fits three rows: three models, or two plus a note.</summary>
    private static void FillAntigravityMeters(ProviderUsage usage, List<(string Name, double Remaining, DateTime? Reset)> models, bool stale, string? tier)
    {
        var now = DateTime.Now;
        var lastReset = models.Max(m => m.Reset);
        var hasNote = stale || models.Count > 3 || tier is not null;
        var shown = hasNote ? 2 : 3;
        foreach (var m in models.Select(m => (m.Name, Remaining: m.Reset is { } t && t < now ? 1.0 : m.Remaining, m.Reset))
                     .OrderBy(m => m.Remaining).ThenBy(m => m.Name).Take(shown))
        {
            var used = Math.Clamp((1 - m.Remaining) * 100, 0, 100);
            var left = m.Reset is { } t && t > now ? $"Resets {t:ddd h:mm tt}" : "Full";
            usage.Meters.Add(new UsageMeter(m.Name, used, $"{used:0}% used", left, $"{100 - used:0}% left"));
        }
        var more = models.Count > shown ? $"+{models.Count - shown} more models." : "";
        var note = stale
            ? $"{more} As of Antigravity's last sync (before {lastReset:MMM d}); quotas have reset since."
            : tier is not null ? $"{tier}. {more}" : more;
        usage.Note = note.Trim() is { Length: > 0 } n ? n : null;
    }

    /// <summary>Accepts the language server's self-signed certificate, for 127.0.0.1 only.</summary>
    private static readonly Lazy<HttpClient> LocalHttp = new(() => new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true,
    }) { Timeout = TimeSpan.FromSeconds(4) });

    /// <summary>
    /// Asks each running Antigravity language server for the signed-in user's status (undocumented Connect RPC
    /// <c>LanguageServerService/GetUserStatus</c>). Null when none is running or none answers.
    /// </summary>
    private static async Task<(List<(string Name, double Remaining, DateTime? Reset)> Models, string? Tier)?> AntigravityLiveAsync()
    {
        foreach (var (port, csrf) in AntigravityServers())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus")
                {
                    Content = new StringContent("""{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}""", Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Codeium-Csrf-Token", csrf);
                request.Headers.Add("Connect-Protocol-Version", "1");
                using var response = await LocalHttp.Value.SendAsync(request);
                if (!response.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("userStatus", out var status)) continue;
                var tier = status.TryGetProperty("userTier", out var t) && t.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                return (AntigravityLiveQuota(status), tier);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// <c>cascadeModelConfigData.clientModelConfigs[]</c>: {label, quotaInfo: {remainingFraction, resetTime}}.
    /// A missing remainingFraction is zero (proto3 default); models without quotaInfo aren't metered.
    /// </summary>
    internal static List<(string Name, double Remaining, DateTime? Reset)> AntigravityLiveQuota(JsonElement status)
    {
        var result = new List<(string, double, DateTime?)>();
        if (!status.TryGetProperty("cascadeModelConfigData", out var data) ||
            !data.TryGetProperty("clientModelConfigs", out var configs) || configs.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var model in configs.EnumerateArray())
        {
            if (!model.TryGetProperty("label", out var label) || label.GetString() is not { } name) continue;
            if (!model.TryGetProperty("quotaInfo", out var quota) || quota.ValueKind != JsonValueKind.Object ||
                !quota.EnumerateObject().Any()) continue;
            var remaining = Num(quota, "remainingFraction") ?? 0;
            DateTime? reset = quota.TryGetProperty("resetTime", out var rt) &&
                              DateTimeOffset.TryParse(rt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var r)
                ? r.LocalDateTime : null;
            result.Add((name, remaining, reset));
        }
        return result;
    }

    /// <summary>
    /// Port and CSRF token of each running Antigravity <c>language_server.exe</c>. The token is on its command line;
    /// the port usually isn't (<c>--https_server_port 0</c> picks a random one), so every port it listens on is a candidate.
    /// </summary>
    private static List<(int Port, string Csrf)> AntigravityServers()
    {
        var result = new List<(int, string)>();
        using var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'");
        using var processes = search.Get();
        foreach (var process in processes)
        {
            using (process)
            {
                if (process["CommandLine"] is not string line || !line.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)) continue;
                var csrf = Regex.Match(line, @"--csrf_token[= ]""?([^\s""]+)");
                if (!csrf.Success) continue;
                var port = Regex.Match(line, @"--https_server_port[= ](\d+)");
                var ports = port.Success && int.TryParse(port.Groups[1].Value, out var p) && p > 0
                    ? [p]
                    : ListeningPorts(Convert.ToInt32(process["ProcessId"]));
                result.AddRange(ports.Select(x => (x, csrf.Groups[1].Value)));
            }
        }
        return result;
    }

    /// <summary>Local TCP (IPv4) ports a process is listening on, lowest first.</summary>
    private static List<int> ListeningPorts(int pid)
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3, RowSize = 24;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return [];
            var ports = new List<int>();
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort (network byte order), remoteAddr, remotePort, owningPid.
                var row = buffer + 4 + i * RowSize;
                if (Marshal.ReadInt32(row, 20) != pid) continue;
                var raw = (ushort)Marshal.ReadInt32(row, 8);
                ports.Add((raw >> 8) | ((raw & 0xFF) << 8));
            }
            ports.Sort();
            return ports;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int ipVersion, int tableClass, uint reserved);

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

    // ---------------------------------------------------------------- OpenCode

    /// <summary>
    /// OpenCode logs every assistant reply, with its tokens and cost, in its local SQLite DB
    /// (<c>~/.local/share/opencode/opencode.db</c>, table <c>message</c>). Read-only; no plan limits.
    /// </summary>
    private static ProviderUsage? LoadOpenCode()
    {
        try
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Home, ".local", "share");
            var dbPath = Path.Combine(dataHome, "opencode", "opencode.db");
            if (!File.Exists(dbPath)) return null;

            var usage = new ProviderUsage { Id = "opencode", Name = "OpenCode", HasCost = true, Logo = BrandLogo.OpenCode };
            var now = DateTime.Now;
            var sessionStart = DateTime.MaxValue;

            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            // Newer OpenCode moved messages to session_message (type column, model.id) and migrated the old
            // rows across, but left the message table behind and stopped writing to it.
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_message'";
            cmd.CommandText = cmd.ExecuteScalar() is not null
                ? """
                  SELECT time_created,
                         json_extract(data, '$.model.id'),
                         json_extract(data, '$.cost'),
                         json_extract(data, '$.tokens.input'),
                         json_extract(data, '$.tokens.output'),
                         json_extract(data, '$.tokens.reasoning'),
                         json_extract(data, '$.tokens.cache.write')
                  FROM session_message
                  WHERE time_created >= $cutoff AND type = 'assistant'
                  """
                : """
                  SELECT time_created,
                         json_extract(data, '$.modelID'),
                         json_extract(data, '$.cost'),
                         json_extract(data, '$.tokens.input'),
                         json_extract(data, '$.tokens.output'),
                         json_extract(data, '$.tokens.reasoning'),
                         json_extract(data, '$.tokens.cache.write')
                  FROM message
                  WHERE time_created >= $cutoff AND json_extract(data, '$.role') = 'assistant'
                  """;
            cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long Int(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt64(i);
                var time = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).LocalDateTime;
                var model = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var cost = reader.IsDBNull(2) ? 0m : (decimal)reader.GetDouble(2);
                // Same as Claude: cache reads aren't counted as tokens used.
                var tokens = Int(3) + Int(4) + Int(5) + Int(6);

                Add(usage, time, now, tokens, cost);
                if (now - time <= TimeSpan.FromHours(5))
                {
                    if (time < sessionStart) sessionStart = time;
                    var label = ModelLabel(model);
                    usage.SessionModels[label] = usage.SessionModels.TryGetValue(label, out var prior) ? prior + tokens : tokens;
                }
            }
            usage.SessionStarted = sessionStart == DateTime.MaxValue ? null : sessionStart;
            return usage;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
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
