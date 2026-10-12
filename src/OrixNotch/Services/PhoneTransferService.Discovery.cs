using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;

namespace OrixNotch.Services;

/// <summary>
/// LocalSend discovery: a JSON announce on multicast 224.0.0.167:53317. Devices that hear an announce
/// answer with POST /register (or a multicast reply), so announcing is also how the list refreshes.
/// </summary>
public sealed partial class PhoneTransferService
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.167");

    /// <summary>Talks to phones' LocalSend servers. Their certificates are self-signed, like ours, so any is
    /// accepted — the same trust model as LocalSend itself; transfers still need the phone user's OK.</summary>
    private static readonly HttpClient PeerClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly DispatcherTimer _announceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private UdpClient? _udp;
    private CancellationTokenSource? _discoveryCts;

    private void StartDiscovery()
    {
        _discoveryCts = new CancellationTokenSource();
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LocalSendPort));
            foreach (var (address, _) in LocalAddresses())
            {
                try
                {
                    udp.JoinMulticastGroup(MulticastGroup, address);
                }
                catch (SocketException)
                {
                    // Adapter without multicast; the others still work.
                }
            }
            _udp = udp;
            _ = ListenAsync(udp, _discoveryCts.Token);
        }
        catch (SocketException ex)
        {
            // Phones can still find us by scanning the subnet (LocalSend does that when multicast fails).
            App.Log(ex);
        }
        _announceTimer.Start();
        Refresh();
    }

    private void StopDiscovery()
    {
        _announceTimer.Stop();
        _discoveryCts?.Cancel();
        _discoveryCts = null;
        _udp?.Dispose();
        _udp = null;
    }

    /// <summary>Announces this PC (devices reply) and forgets devices that went quiet.</summary>
    public void Refresh()
    {
        if (!IsRunning) return;
        foreach (var gone in Devices.Where(d => DateTime.Now - d.LastSeen > TimeSpan.FromSeconds(95)).ToList())
            Devices.Remove(gone);
        _ = SendMulticastAsync(OwnInfo(announce: true));
    }

    private static async Task SendMulticastAsync(LocalSendInfo info)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(info, Json);
        foreach (var (address, _) in LocalAddresses())
        {
            try
            {
                // One socket per adapter, so the announce reaches every network this PC is on.
                using var udp = new UdpClient(new IPEndPoint(address, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                await udp.SendAsync(bytes, new IPEndPoint(MulticastGroup, LocalSendPort));
            }
            catch (SocketException)
            {
                // Adapter went away or blocks multicast.
            }
        }
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            LocalSendInfo? info;
            try
            {
                info = JsonSerializer.Deserialize<LocalSendInfo>(packet.Buffer, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (info is null || info.Alias.Length == 0 || info.Fingerprint == _fingerprint) continue;

            var ip = packet.RemoteEndPoint.Address.ToString();
            OnUi(() => Upsert(info, ip));
            if (info.Announce == true || info.Announcement == true) _ = AnswerAnnounceAsync(info, ip);
        }
    }

    /// <summary>Introduces this PC to a device that just announced itself.</summary>
    private async Task AnswerAnnounceAsync(LocalSendInfo peer, string ip)
    {
        try
        {
            var url = $"{peer.Protocol ?? "https"}://{ip}:{peer.Port ?? LocalSendPort}/api/localsend/v2/register";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await PeerClient.PostAsync(url, JsonBody(OwnInfo()), cts.Token);
            if (response.IsSuccessStatusCode) return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Fall back to answering on multicast.
        }
        await SendMulticastAsync(OwnInfo());
    }

    /// <summary>JSON with a Content-Length (JsonContent streams chunked, which some phone servers don't read).</summary>
    private static ByteArrayContent JsonBody<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Adds or updates a device. UI thread.</summary>
    private void Upsert(LocalSendInfo info, string ip)
    {
        if (!IsRunning || info.Fingerprint == _fingerprint || ip.Length == 0) return;
        var key = info.Fingerprint is { Length: > 0 } fp ? fp : ip;
        var device = Devices.FirstOrDefault(d => d.Fingerprint == key);
        if (device is null)
        {
            device = new PhoneDevice { Fingerprint = key };
            Devices.Add(device);
        }
        device.Alias = info.Alias.Length > 0 ? info.Alias : ip;
        device.Model = info.DeviceModel ?? "";
        device.Ip = ip;
        device.Port = info.Port ?? LocalSendPort;
        device.Protocol = info.Protocol is "http" ? "http" : "https";
        device.IsMobile = info.DeviceType is null or "mobile";
        device.LastSeen = DateTime.Now;
    }
}
