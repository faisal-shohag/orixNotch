using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OrixNotch.Services;

/// <summary>One upload from or download to a phone, shown with progress in the Phone tool.</summary>
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
    public long Total { get; }
    public CancellationTokenSource Cts { get; } = new();

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _failed;
    /// <summary>Text the phone sent (instead of a file); clicking the row copies it.</summary>
    public string? Text { get; init; }

    public DateTime At { get; } = DateTime.Now;

    /// <summary>Where the received file was saved, once it's done.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanOpen))] private string? _savedPath;

    /// <summary>Clickable: opens the saved file, or copies the text.</summary>
    public bool CanOpen => SavedPath is not null || Text is not null;

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

public enum PhoneOutboxKind
{
    File,
    Folder,
    Text,
}

/// <summary>Something the PC offers to the phone; the page lists it under "From PC".</summary>
public sealed partial class PhoneOutboxItem : ObservableObject
{
    public required string Id { get; init; }
    public required PhoneOutboxKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string? Text { get; init; }
    public long Size { get; init; }
    public DateTime SentAt { get; } = DateTime.Now;

    [ObservableProperty] private string _state = "Waiting for phone";

    public string Detail => Kind switch
    {
        PhoneOutboxKind.Text => "Text",
        PhoneOutboxKind.Folder => "Folder · .zip",
        _ => PhoneTransferService.FormatSize(Size),
    };
}

/// <summary>
/// Phone ↔ PC transfer over the local network: a small browser page the phone opens by scanning a
/// QR code on the notch. Uploads land in the receive folder and on the Shelf; the PC sends files,
/// folders and text through an outbox the page shows live. Nothing leaves the LAN. Off until turned on.
/// </summary>
public sealed partial class PhoneTransferService : ObservableObject
{
    private static readonly int[] PortChoices = [53318, 53328];

    public static PhoneTransferService Instance { get; } = new();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static AppSettings S => App.Settings;

    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private WebApplication? _server;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStarting;
    /// <summary>Last error or warning, shown in the Phone tool.</summary>
    [ObservableProperty] private string _status = "";
    /// <summary>Browser page address for the QR code (with its access key); null when off or offline.</summary>
    [ObservableProperty] private string? _webUrl;
    /// <summary>A phone has the page open (it holds the live-update connection).</summary>
    [ObservableProperty] private bool _phoneConnected;

    public ObservableCollection<PhoneTransfer> Transfers { get; } = new();

    /// <summary>Files or text arrived (peer name, item count, whether it was text).</summary>
    public event Action<string, int, bool>? Received;

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
    }

    // ---------------------------------------------------------------- start / stop

    /// <summary>Starts the server, or turns it off (retiring the link) to match the setting.</summary>
    public Task SyncAsync() => S.PhoneEnabled ? StartAsync() : TurnOffAsync();

    /// <summary>The user turned Phone off: stop, and forget the key so the old link stops working for good.</summary>
    public async Task TurnOffAsync()
    {
        await StopAsync();
        ForgetWebKey();
    }

    private async Task StartAsync()
    {
        if (_server is not null || IsStarting) return;
        IsStarting = true;
        Status = "";
        try
        {
            LoadOrCreateWebKey();
            var port = 0;
            Exception? last = null;
            // The saved port first, so the phone's link keeps working across restarts.
            foreach (var choice in PortChoices.Prepend(S.PhonePort).Where(p => p > 0).Distinct())
            {
                var server = BuildServer(choice);
                try
                {
                    await server.StartAsync();
                    _server = server;
                    port = choice;
                    break;
                }
                catch (IOException ex) // port taken by another app
                {
                    last = ex;
                    await server.DisposeAsync();
                }
            }
            if (_server is null) throw last ?? new IOException("No free port.");
            if (S.PhonePort != port)
            {
                S.PhonePort = port;
                SettingsService.Save(notify: false);
            }

            IsRunning = true;
            WatchShelf();
            var ip = BestLocalAddress()?.ToString();
            WebUrl = ip is null ? null : $"http://{ip}:{port}/?k={_webKey}";
            if (ip is null) Status = "Not connected to a network. Join the same Wi-Fi as your phone.";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Status = ex is IOException ? "Couldn't open a network port." : "Couldn't start: " + ex.Message;
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
        var server = _server;
        _server = null;
        IsRunning = false;
        WebUrl = null;
        _webKey = "";
        foreach (var t in Transfers.Where(t => t.IsActive)) t.Cts.Cancel();
        CloseEvents();
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

    private WebApplication BuildServer(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;    // phone videos can be gigabytes
            k.Limits.MinRequestBodyDataRate = null; // and phones pause when the screen locks
            k.ListenAnyIP(port);
        });
        var app = builder.Build();
        MapWeb(app);
        return app;
    }

    public void CancelTransfer(PhoneTransfer transfer)
    {
        if (transfer.IsActive) transfer.Cts.Cancel();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>As a <see cref="Delegate"/>, so minimal APIs write the returned result (a method group
    /// would bind as a RequestDelegate and drop it).</summary>
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;

    private void Track(PhoneTransfer transfer)
    {
        Transfers.Insert(0, transfer);
        while (Transfers.Count > 20 && Transfers.LastOrDefault(t => !t.IsActive) is { } old) Transfers.Remove(old);
        _progressTimer.Start();
    }

    /// <summary>Streams a request body to <paramref name="path"/> via a .part file, counting bytes on the transfer.</summary>
    private static async Task SaveBodyAsync(HttpContext ctx, string path, PhoneTransfer transfer, CancellationToken ct)
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
    /// Where a received file goes: the name is cleaned of path tricks and gets "name (2).ext" when taken.
    /// </summary>
    internal static string TargetPath(string root, string name)
    {
        var file = SafeName(Path.GetFileName(name.Replace('\\', '/')));
        if (file.Length == 0 || file is "." or "..") file = "file";
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, file);
        for (var n = 2; File.Exists(path) || File.Exists(path + ".part"); n++)
            path = Path.Combine(root, $"{Path.GetFileNameWithoutExtension(file)} ({n}){Path.GetExtension(file)}");
        return path;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 120 ? clean[..100] + Path.GetExtension(clean) : clean;
    }

    private static void OnUi(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    private static T OnUi<T>(Func<T> func) => System.Windows.Application.Current.Dispatcher.Invoke(func);

    /// <summary>The address a phone on the same Wi-Fi most likely reaches: a real adapter with a gateway.</summary>
    private static System.Net.IPAddress? BestLocalAddress()
    {
        var list = new List<(System.Net.IPAddress Address, NetworkInterface Nic)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = a.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local
                list.Add((a.Address, nic));
            }
        }
        return list
            .OrderByDescending(x => x.Nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(x => !IsVirtual(x.Nic))
            .ThenByDescending(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Select(x => x.Address)
            .FirstOrDefault();
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        var d = nic.Description + " " + nic.Name;
        return new[] { "Virtual", "Hyper-V", "VMware", "VirtualBox", "WSL", "vEthernet", "TAP", "VPN", "Tailscale", "ZeroTier" }
            .Any(v => d.Contains(v, StringComparison.OrdinalIgnoreCase));
    }
}
