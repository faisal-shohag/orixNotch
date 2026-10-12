using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OrixNotch.Services;

// ---------------------------------------------------------------- LocalSend v2 wire format

/// <summary>Device info as LocalSend sends it (multicast announce, /register, /info, prepare-upload "info").</summary>
public sealed class LocalSendInfo
{
    public string Alias { get; set; } = "";
    public string? Version { get; set; }
    public string? DeviceModel { get; set; }
    public string? DeviceType { get; set; }
    public string? Fingerprint { get; set; }
    public int? Port { get; set; }
    public string? Protocol { get; set; }
    public bool? Download { get; set; }
    public bool? Announce { get; set; }
    /// <summary>Protocol v1 spelling of <see cref="Announce"/>; still sent for older peers.</summary>
    public bool? Announcement { get; set; }
}

public sealed class LocalSendFile
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string? FileType { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Text messages carry their text here and upload nothing.</summary>
    public string? Preview { get; set; }
    public LocalSendFileMetadata? Metadata { get; set; }
}

public sealed class LocalSendFileMetadata
{
    public string? Modified { get; set; }
    public string? Accessed { get; set; }
}

public sealed class LocalSendPrepare
{
    public LocalSendInfo? Info { get; set; }
    public Dictionary<string, LocalSendFile>? Files { get; set; }
}

public sealed class LocalSendSession
{
    public string SessionId { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new();
}

// ---------------------------------------------------------------- UI models

/// <summary>A LocalSend device seen on the network.</summary>
public sealed partial class PhoneDevice : ObservableObject
{
    public string Fingerprint { get; init; } = "";
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private int _port = PhoneTransferService.LocalSendPort;
    [ObservableProperty] private string _protocol = "https";
    [ObservableProperty] private bool _isMobile = true;
    public DateTime LastSeen { get; set; } = DateTime.Now;

    public string Detail => Model.Length > 0 ? $"{Model} · {Ip}" : Ip;

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(Detail));
    partial void OnIpChanged(string value) => OnPropertyChanged(nameof(Detail));
}

/// <summary>One send or receive, shown with progress in the Phone tool.</summary>
public sealed partial class PhoneTransfer : ObservableObject
{
    private long _bytes;

    public PhoneTransfer(string title, string peer, bool incoming, long total)
    {
        _title = title;
        Peer = peer;
        Incoming = incoming;
        Total = total;
    }

    public string Peer { get; }
    public bool Incoming { get; }
    public long Total { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public string? SessionId { get; set; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _failed;

    public string Direction => Incoming ? $"From {Peer}" : $"To {Peer}";

    /// <summary>Counts bytes from any thread; <see cref="Tick"/> moves them to the UI.</summary>
    public void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

    public void Tick()
    {
        if (!IsActive || Total <= 0) return;
        Progress = Math.Clamp(Interlocked.Read(ref _bytes) / (double)Total, 0, 1);
    }

    public void Finish(string state, bool failed = false)
    {
        IsActive = false;
        Failed = failed;
        State = state;
        if (!failed) Progress = 1;
    }
}

/// <summary>A LocalSend device asking to send us files; the Phone tool shows Accept / Decline.</summary>
public sealed class IncomingRequest
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Sender { get; init; }
    public required string Fingerprint { get; init; }
    public required IReadOnlyList<LocalSendFile> Files { get; init; }
    public bool IsText => Files.All(f => f.Preview is not null && (f.FileType ?? "").StartsWith("text/", StringComparison.Ordinal));

    public string Summary
    {
        get
        {
            if (IsText) return Files.Count == 1 ? "a message" : $"{Files.Count} messages";
            var size = PhoneTransferService.FormatSize(Files.Sum(f => f.Size));
            return Files.Count == 1 ? $"{Files[0].FileName} · {size}" : $"{Files.Count} files · {size}";
        }
    }

    public Task<bool> Answer => _answer.Task;
    public void Resolve(bool accept) => _answer.TrySetResult(accept);
}

/// <summary>
/// Phone ↔ PC transfer over the local network. Speaks the LocalSend v2 protocol (so the free
/// LocalSend app on a phone finds the PC and can send/receive through the share sheet), and hosts a
/// small browser page for phones without the app. Nothing leaves the LAN. Off until the user turns it on.
/// </summary>
public sealed partial class PhoneTransferService : ObservableObject
{
    public const int LocalSendPort = 53317;
    private static readonly (int Api, int Web)[] PortChoices = [(53317, 53318), (53327, 53328)];

    public static PhoneTransferService Instance { get; } = new();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AppSettings S => App.Settings;

    private readonly object _lock = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private WebApplication? _server;
    private X509Certificate2? _cert;
    private string _fingerprint = "";
    private int _port;
    private int _webPort;
    private ReceiveSession? _session;
    private bool _prompting;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStarting;
    /// <summary>Last error or warning, shown in the Phone tool.</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _localIp = "";
    /// <summary>Browser page address for the QR code (with its access key); null when the page is off.</summary>
    [ObservableProperty] private string? _webUrl;
    [ObservableProperty] private IncomingRequest? _pending;
    /// <summary>Files waiting for the user to pick a device (from the Shelf's "Send to phone").</summary>
    [ObservableProperty] private IReadOnlyList<string>? _queued;

    public ObservableCollection<PhoneDevice> Devices { get; } = new();
    public ObservableCollection<PhoneTransfer> Transfers { get; } = new();

    /// <summary>A device wants to send something and is waiting for an answer.</summary>
    public event Action<IncomingRequest>? Incoming;

    /// <summary>Files or text arrived (peer name, item count); they are already on the Shelf.</summary>
    public event Action<string, int>? Received;

    public string Alias => S.PhoneAlias is { Length: > 0 } alias ? alias : Environment.MachineName;

    public string ReceiveFolder => S.PhoneReceiveFolder is { Length: > 0 } folder
        ? Path.GetFullPath(folder)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OrixNotch");

    private PhoneTransferService()
    {
        _progressTimer.Tick += (_, _) =>
        {
            var any = false;
            foreach (var t in Transfers.Where(t => t.IsActive))
            {
                t.Tick();
                any = true;
            }
            if (!any) _progressTimer.Stop();
        };
        _announceTimer.Tick += (_, _) => Refresh();
    }

    // ---------------------------------------------------------------- start / stop

    /// <summary>Starts or stops the server to match the setting.</summary>
    public Task SyncAsync() => S.PhoneEnabled ? StartAsync() : StopAsync();

    public async Task RestartAsync()
    {
        await StopAsync();
        await SyncAsync();
    }

    private async Task StartAsync()
    {
        if (_server is not null || IsStarting) return;
        IsStarting = true;
        Status = "";
        try
        {
            _cert ??= await Task.Run(LoadCertificate);
            _fingerprint = Convert.ToHexString(SHA256.HashData(_cert.RawData)).ToLowerInvariant();
            NewWebKey();

            Exception? last = null;
            foreach (var (api, web) in PortChoices)
            {
                var server = BuildServer(api, web);
                try
                {
                    await server.StartAsync();
                    _server = server;
                    _port = api;
                    _webPort = web;
                    break;
                }
                catch (IOException ex) // port taken (LocalSend for Windows, another app)
                {
                    last = ex;
                    await server.DisposeAsync();
                }
            }
            if (_server is null) throw last ?? new IOException("No free port.");

            IsRunning = true;
            LocalIp = BestLocalAddress()?.ToString() ?? "";
            WebUrl = S.PhoneBrowserPage && LocalIp.Length > 0 ? $"http://{LocalIp}:{_webPort}/?k={_webKey}" : null;
            if (LocalIp.Length == 0) Status = "Not connected to a network. Join the same Wi-Fi as your phone.";
            StartDiscovery();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Status = ex is IOException ? "Couldn't open a network port. Is LocalSend running on this PC?" : "Couldn't start: " + ex.Message;
            await StopAsync(keepStatus: true);
        }
        finally
        {
            IsStarting = false;
        }
    }

    public async Task StopAsync() => await StopAsync(keepStatus: false);

    private async Task StopAsync(bool keepStatus)
    {
        StopDiscovery();
        var server = _server;
        _server = null;
        IsRunning = false;
        WebUrl = null;
        Pending?.Resolve(false);
        Pending = null;
        lock (_lock)
        {
            _session?.Cts.Cancel();
            _session = null;
        }
        Devices.Clear();
        if (!keepStatus) Status = "";
        if (server is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.StopAsync(timeout.Token);
            await server.DisposeAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private WebApplication BuildServer(int apiPort, int webPort)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;    // phone videos can be gigabytes
            k.Limits.MinRequestBodyDataRate = null; // and phones pause when the screen locks
            k.ListenAnyIP(apiPort, listen => listen.UseHttps(_cert!));
            if (S.PhoneBrowserPage) k.ListenAnyIP(webPort);
        });
        var app = builder.Build();
        MapLocalSend(app, apiPort);
        if (S.PhoneBrowserPage) MapWeb(app, webPort);
        return app;
    }

    /// <summary>Self-signed TLS certificate for the LocalSend endpoint, made once and kept (DPAPI-protected).</summary>
    private static X509Certificate2 LoadCertificate()
    {
        var path = Storage.PathOf("phone-cert.bin");
        try
        {
            if (File.Exists(path))
                return new X509Certificate2(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=OrixNotch", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        // Round-trip through PFX: Windows' TLS stack can't use the in-memory key CreateSelfSigned returns.
        var pfx = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
        return new X509Certificate2(pfx);
    }

    // ---------------------------------------------------------------- receiving (LocalSend endpoints)

    private sealed class ReceiveFile
    {
        public required LocalSendFile Info { get; init; }
        public required string Token { get; init; }
        public bool Done { get; set; }
    }

    private sealed class ReceiveSession
    {
        public required string Id { get; init; }
        public required string Peer { get; init; }
        public required string PeerIp { get; init; }
        public required Dictionary<string, ReceiveFile> Files { get; init; }
        public required PhoneTransfer Transfer { get; init; }
        public CancellationTokenSource Cts => Transfer.Cts;
        public List<string> Saved { get; } = new();
        public DateTime LastActivity { get; set; } = DateTime.Now;
    }

    private LocalSendInfo OwnInfo(bool announce = false) => new()
    {
        Alias = Alias,
        Version = "2.0",
        DeviceModel = "Windows",
        DeviceType = "desktop",
        Fingerprint = _fingerprint,
        Port = _port,
        Protocol = "https",
        Download = false,
        Announce = announce ? true : null,
        Announcement = announce ? true : null,
    };

    private void MapLocalSend(WebApplication app, int port)
    {
        const string Api = "/api/localsend/v2";
        var host = $"*:{port}";
        app.MapGet(Api + "/info", () => Results.Json(OwnInfo(), Json)).RequireHost(host);
        app.MapPost(Api + "/register", Handler(RegisterAsync)).RequireHost(host);
        app.MapPost(Api + "/prepare-upload", Handler(PrepareUploadAsync)).RequireHost(host);
        app.MapPost(Api + "/upload", Handler(UploadAsync)).RequireHost(host);
        app.MapPost(Api + "/cancel", (HttpContext ctx) => Cancel(ctx)).RequireHost(host);
    }

    /// <summary>As a <see cref="Delegate"/>, so minimal APIs write the returned result (a method group
    /// would bind as a RequestDelegate and drop it).</summary>
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;

    private static string RemoteIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";

    /// <summary>A device answering our announce (or scanning the subnet) introduces itself.</summary>
    private async Task<IResult> RegisterAsync(HttpContext ctx)
    {
        var info = await ReadJsonAsync<LocalSendInfo>(ctx);
        if (info is not null) OnUi(() => Upsert(info, RemoteIp(ctx)));
        return Results.Json(OwnInfo(), Json);
    }

    private async Task<IResult> PrepareUploadAsync(HttpContext ctx)
    {
        var body = await ReadJsonAsync<LocalSendPrepare>(ctx);
        if (body?.Info is not { } info || body.Files is not { Count: > 0 } files) return Results.BadRequest();
        var ip = RemoteIp(ctx);

        lock (_lock)
        {
            // A sender that vanished mid-transfer must not block everyone else forever.
            if (_session is { } stale && DateTime.Now - stale.LastActivity > TimeSpan.FromMinutes(1)) DropSession(stale, "Stopped");
            if (_session is not null || _prompting) return Results.StatusCode(409);
            _prompting = true;
        }

        try
        {
            OnUi(() => Upsert(info, ip));
            var request = new IncomingRequest
            {
                Sender = info.Alias.Length > 0 ? info.Alias : ip,
                Fingerprint = info.Fingerprint ?? "",
                Files = files.Values.ToList(),
            };

            bool accepted;
            if (request.Fingerprint.Length > 0 && S.PhoneTrusted.Contains(request.Fingerprint))
            {
                accepted = true;
            }
            else
            {
                OnUi(() =>
                {
                    Pending = request;
                    Incoming?.Invoke(request);
                });
                using var gone = ctx.RequestAborted.Register(() => request.Resolve(false));
                try
                {
                    accepted = await request.Answer.WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch (TimeoutException)
                {
                    accepted = false;
                }
                OnUi(() =>
                {
                    if (Pending == request) Pending = null;
                });
            }
            if (!accepted) return Results.StatusCode(403);

            if (request.IsText)
            {
                OnUi(() =>
                {
                    foreach (var f in request.Files) ShelfService.Instance.AddText(f.Preview!);
                    Received?.Invoke(request.Sender, request.Files.Count);
                });
                return Results.NoContent();
            }

            var total = files.Values.Sum(f => f.Size);
            var title = files.Count == 1 ? files.Values.First().FileName : $"{files.Count} files";
            var transfer = new PhoneTransfer(title, request.Sender, incoming: true, total) { State = "Receiving" };
            var session = new ReceiveSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Peer = request.Sender,
                PeerIp = ip,
                Transfer = transfer,
                Files = files.ToDictionary(p => p.Key, p => new ReceiveFile { Info = p.Value, Token = Guid.NewGuid().ToString("N") }),
            };
            lock (_lock) _session = session;
            OnUi(() => Track(transfer));
            return Results.Json(new LocalSendSession
            {
                SessionId = session.Id,
                Files = session.Files.ToDictionary(p => p.Key, p => p.Value.Token),
            }, Json);
        }
        finally
        {
            lock (_lock) _prompting = false;
        }
    }

    private async Task<IResult> UploadAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        ReceiveSession? session;
        ReceiveFile? file;
        lock (_lock)
        {
            session = _session;
            if (session is null || session.Id != q["sessionId"] || session.PeerIp != RemoteIp(ctx) ||
                !session.Files.TryGetValue(q["fileId"].ToString(), out file) || file.Token != q["token"] || file.Done)
                return Results.StatusCode(403);
            session.LastActivity = DateTime.Now;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, session.Cts.Token);
        try
        {
            var (path, shelfPath) = TargetPath(ReceiveFolder, file.Info.FileName);
            await SaveBodyAsync(ctx, path, session.Transfer, cts.Token, () => session.LastActivity = DateTime.Now);
            lock (_lock)
            {
                file.Done = true;
                if (!session.Saved.Contains(shelfPath, StringComparer.OrdinalIgnoreCase)) session.Saved.Add(shelfPath);
                if (session.Files.Values.All(f => f.Done)) CompleteSession(session);
            }
            return Results.Ok();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            if (session.Cts.IsCancellationRequested) return Results.StatusCode(500);
            lock (_lock) DropSession(session, ctx.RequestAborted.IsCancellationRequested ? "Stopped by sender" : "Couldn't save");
            if (ex is IOException) App.Log(ex);
            return Results.StatusCode(500);
        }
    }

    private IResult Cancel(HttpContext ctx)
    {
        lock (_lock)
        {
            if (_session is { } s && s.Id == ctx.Request.Query["sessionId"]) DropSession(s, "Canceled by sender");
        }
        return Results.Ok();
    }

    /// <summary>Called under <see cref="_lock"/>.</summary>
    private void CompleteSession(ReceiveSession session)
    {
        _session = null;
        var saved = session.Saved.ToList();
        OnUi(() =>
        {
            session.Transfer.Finish($"Saved to {Path.GetFileName(ReceiveFolder.TrimEnd('\\'))}");
            ShelfService.Instance.AddFiles(saved);
            Received?.Invoke(session.Peer, session.Files.Count);
        });
    }

    /// <summary>Called under <see cref="_lock"/>.</summary>
    private void DropSession(ReceiveSession session, string state)
    {
        if (_session == session) _session = null;
        session.Cts.Cancel();
        var saved = session.Saved.ToList();
        OnUi(() =>
        {
            session.Transfer.Finish(state, failed: true);
            if (saved.Count > 0) ShelfService.Instance.AddFiles(saved); // keep whatever finished
        });
    }

    /// <summary>The user answers the request shown in the Phone tool.</summary>
    public void Answer(bool accept, bool trust = false)
    {
        if (Pending is not { } request) return;
        if (accept && trust && request.Fingerprint.Length > 0 && !S.PhoneTrusted.Contains(request.Fingerprint))
        {
            S.PhoneTrusted.Add(request.Fingerprint);
            SettingsService.Save(false);
        }
        Pending = null;
        request.Resolve(accept);
    }

    public void CancelTransfer(PhoneTransfer transfer)
    {
        if (!transfer.IsActive) return;
        transfer.Cts.Cancel();
        if (!transfer.Incoming) return;
        lock (_lock)
        {
            if (_session is { } s && s.Transfer == transfer) DropSession(s, "Canceled");
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Track(PhoneTransfer transfer)
    {
        Transfers.Insert(0, transfer);
        while (Transfers.Count > 20 && Transfers.LastOrDefault(t => !t.IsActive) is { } old) Transfers.Remove(old);
        _progressTimer.Start();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Json, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Streams a request body to <paramref name="path"/> via a .part file, counting bytes on the transfer.</summary>
    private static async Task SaveBodyAsync(HttpContext ctx, string path, PhoneTransfer transfer, CancellationToken ct, Action? onChunk = null)
    {
        var part = path + ".part";
        try
        {
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    transfer.Add(read);
                    onChunk?.Invoke();
                }
            }
            File.Move(part, path);
        }
        catch
        {
            try
            {
                File.Delete(part);
            }
            catch
            {
                // Locked by antivirus; harmless leftover.
            }
            throw;
        }
    }

    /// <summary>
    /// Where a received file goes. LocalSend sends folders as "Folder/sub/file.jpg": the tree is kept,
    /// every part is cleaned of path tricks, and the file gets "name (2).ext" when the name is taken.
    /// Returns the file path and the item to put on the Shelf (the top folder for files inside one).
    /// </summary>
    internal static (string Path, string ShelfPath) TargetPath(string root, string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SafeName).Where(p => p.Length > 0 && p != "." && p != "..").ToList();
        if (parts.Count == 0) parts.Add("file");

        var dir = Path.Combine([root, .. parts.SkipLast(1)]);
        Directory.CreateDirectory(dir);
        var file = parts[^1];
        var path = Path.Combine(dir, file);
        for (var n = 2; File.Exists(path) || File.Exists(path + ".part"); n++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)} ({n}){Path.GetExtension(file)}");
        var shelf = parts.Count > 1 ? Path.Combine(root, parts[0]) : path;
        return (path, shelf);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 120 ? clean[..100] + Path.GetExtension(clean) : clean;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static void OnUi(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    private static T OnUi<T>(Func<T> func) => System.Windows.Application.Current.Dispatcher.Invoke(func);

    /// <summary>LAN IPv4 addresses on adapters that are up (no loopback, tunnels or link-local).</summary>
    private static List<(System.Net.IPAddress Address, NetworkInterface Nic)> LocalAddresses()
    {
        var list = new List<(System.Net.IPAddress, NetworkInterface)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = a.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                list.Add((a.Address, nic));
            }
        }
        return list;
    }

    /// <summary>The address a phone on the same Wi-Fi most likely reaches: a real adapter with a gateway.</summary>
    private static System.Net.IPAddress? BestLocalAddress() => LocalAddresses()
        .OrderByDescending(x => x.Nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
        .ThenByDescending(x => !IsVirtual(x.Nic))
        .ThenByDescending(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        .Select(x => x.Address)
        .FirstOrDefault();

    private static bool IsVirtual(NetworkInterface nic)
    {
        var d = nic.Description + " " + nic.Name;
        return new[] { "Virtual", "Hyper-V", "VMware", "VirtualBox", "WSL", "vEthernet", "TAP", "VPN", "Tailscale", "ZeroTier" }
            .Any(v => d.Contains(v, StringComparison.OrdinalIgnoreCase));
    }
}
