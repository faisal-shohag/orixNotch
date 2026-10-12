using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace OrixNotch.Services;

/// <summary>Sending to a LocalSend device: prepare-upload (the phone user accepts), then one upload per file.</summary>
public sealed partial class PhoneTransferService
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    internal static string ContentTypeOf(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";

    /// <summary>Sends files (folders are sent with their tree) to a device. UI thread.</summary>
    public async Task SendAsync(PhoneDevice device, IReadOnlyList<string> paths)
    {
        var entries = Expand(paths);
        if (entries.Count == 0) return;
        Queued = null;
        S.PhoneLastDevice = device.Fingerprint;
        SettingsService.Save(false);

        var title = entries.Count == 1 ? Path.GetFileName(entries[0].Path) : $"{entries.Count} files";
        var transfer = new PhoneTransfer(title, device.Alias, incoming: false, entries.Sum(e => e.Size))
        {
            State = $"Waiting for {device.Alias} to accept…",
        };
        Track(transfer);

        var api = $"{device.Protocol}://{device.Ip}:{device.Port}/api/localsend/v2";
        var files = entries.Select(e => (Id: Guid.NewGuid().ToString("N"), Entry: e)).ToList();
        var ct = transfer.Cts.Token;
        try
        {
            var prepare = new LocalSendPrepare
            {
                Info = OwnInfo(),
                Files = files.ToDictionary(f => f.Id, f => new LocalSendFile
                {
                    Id = f.Id,
                    FileName = f.Entry.Name,
                    Size = f.Entry.Size,
                    FileType = ContentTypeOf(f.Entry.Path),
                    Metadata = new LocalSendFileMetadata
                    {
                        Modified = File.GetLastWriteTimeUtc(f.Entry.Path).ToString("O"),
                        Accessed = File.GetLastAccessTimeUtc(f.Entry.Path).ToString("O"),
                    },
                }),
            };

            LocalSendSession? session;
            using (var response = await PeerClient.PostAsync(api + "/prepare-upload", JsonBody(prepare), ct))
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.NoContent:
                        transfer.Finish("Done");
                        return;
                    case HttpStatusCode.Forbidden:
                        transfer.Finish("Declined", failed: true);
                        return;
                    case HttpStatusCode.Unauthorized:
                        transfer.Finish("Turn off the PIN in LocalSend", failed: true);
                        return;
                    case HttpStatusCode.Conflict:
                    case HttpStatusCode.TooManyRequests:
                        transfer.Finish($"{device.Alias} is busy", failed: true);
                        return;
                }
                response.EnsureSuccessStatusCode();
                session = await response.Content.ReadFromJsonAsync<LocalSendSession>(Json, ct);
            }
            if (session is null) throw new HttpRequestException("Empty answer from the phone.");

            transfer.SessionId = session.SessionId;
            transfer.State = "Sending";
            foreach (var (id, entry) in files)
            {
                if (!session.Files.TryGetValue(id, out var token))
                {
                    transfer.Add(entry.Size); // the phone already has it
                    continue;
                }
                var url = $"{api}/upload?sessionId={Uri.EscapeDataString(session.SessionId)}&fileId={id}&token={Uri.EscapeDataString(token)}";
                await using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                using var content = new ProgressContent(stream, entry.Size, transfer);
                using var response = await PeerClient.PostAsync(url, content, ct);
                response.EnsureSuccessStatusCode();
            }
            transfer.Finish("Sent");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            transfer.Finish("Canceled", failed: true);
            if (transfer.SessionId is { } id) _ = PeerClient.PostAsync($"{api}/cancel?sessionId={Uri.EscapeDataString(id)}", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            App.Log(ex);
            transfer.Finish(transfer.SessionId is null ? $"Couldn't reach {device.Alias}" : "Stopped", failed: true);
        }
    }

    /// <summary>Sends to the last-used device when it's around; otherwise asks the user to pick one.</summary>
    public bool TrySendToLast(IReadOnlyList<string> paths)
    {
        var device = Devices.FirstOrDefault(d => d.Fingerprint == S.PhoneLastDevice)
                     ?? (Devices.Count == 1 ? Devices[0] : null);
        if (device is null)
        {
            Queued = paths;
            return false;
        }
        _ = SendAsync(device, paths);
        return true;
    }

    private sealed record SendEntry(string Path, string Name, long Size);

    /// <summary>Files as they are; folders become their files, named "Folder/sub/file" like LocalSend does.</summary>
    private static List<SendEntry> Expand(IEnumerable<string> paths)
    {
        var list = new List<SendEntry>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    list.Add(new SendEntry(path, Path.GetFileName(path), new FileInfo(path).Length));
                }
                else if (Directory.Exists(path))
                {
                    var root = Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? path;
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        list.Add(new SendEntry(file, Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log(ex);
            }
        }
        return list;
    }

    /// <summary>Streams a file as the request body and counts what went out.</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _length;
        private readonly PhoneTransfer _transfer;

        public ProgressContent(Stream source, long length, PhoneTransfer transfer)
        {
            _source = source;
            _length = length;
            _transfer = transfer;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await _source.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                _transfer.Add(read);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
