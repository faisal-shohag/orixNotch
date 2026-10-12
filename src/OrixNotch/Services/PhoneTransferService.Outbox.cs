using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace OrixNotch.Services;

/// <summary>
/// PC → phone: files, folders and text put in the outbox show up on the phone page right away
/// (Server-Sent Events tell it to reload). Files stream with progress; folders, and "Download all",
/// stream as a .zip built on the fly.
/// </summary>
public sealed partial class PhoneTransferService
{
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20);

    private readonly object _eventsLock = new();
    private readonly List<Channel<string>> _events = new();
    private bool _shelfWatched;

    /// <summary>What the phone page lists under "From PC", newest first. Kept while the server is off.</summary>
    public ObservableCollection<PhoneOutboxItem> Outbox { get; } = new();

    // ---------------------------------------------------------------- PC side (UI thread)

    /// <summary>Offers files and folders to the phone (already offered paths are skipped).</summary>
    public void Send(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var raw in paths)
        {
            string path;
            try
            {
                path = Path.GetFullPath(raw);
            }
            catch (Exception)
            {
                continue;
            }
            if (Outbox.Any(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            PhoneOutboxItem item;
            if (File.Exists(path))
            {
                item = new PhoneOutboxItem
                {
                    Id = RandomId(), Kind = PhoneOutboxKind.File, Name = Path.GetFileName(path), Path = path, Size = new FileInfo(path).Length,
                };
            }
            else if (Directory.Exists(path))
            {
                var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                item = new PhoneOutboxItem
                {
                    Id = RandomId(), Kind = PhoneOutboxKind.Folder, Name = name.Length > 0 ? name : "Folder", Path = path,
                };
            }
            else
            {
                continue;
            }
            Outbox.Insert(0, item);
            added = true;
        }
        if (added) Push("outbox");
    }

    public void SendText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var line = text.Trim().Split('\n')[0].Trim();
        Outbox.Insert(0, new PhoneOutboxItem
        {
            Id = RandomId(), Kind = PhoneOutboxKind.Text, Name = line.Length > 60 ? line[..60] + "…" : line, Text = text,
        });
        Push("outbox");
    }

    /// <summary>Sends whatever is on the clipboard: files, text or an image (saved as a PNG). False when there's nothing.</summary>
    public bool SendClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                Send(Clipboard.GetFileDropList().Cast<string>());
                return true;
            }
            if (Clipboard.ContainsText())
            {
                SendText(Clipboard.GetText());
                return true;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var path = Path.Combine(Storage.Dir("phone-out"), $"Clipboard {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                using (var file = File.Create(path))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(file);
                }
                Send([path]);
                return true;
            }
        }
        catch (Exception ex) // clipboard held by another app
        {
            App.Log(ex);
        }
        return false;
    }

    public void RemoveFromOutbox(PhoneOutboxItem item)
    {
        if (Outbox.Remove(item)) Push("outbox");
    }

    public void ClearOutbox()
    {
        if (Outbox.Count == 0) return;
        Outbox.Clear();
        Push("outbox");
    }

    // ---------------------------------------------------------------- endpoints

    private void MapOutbox(WebApplication app)
    {
        app.MapGet("/web/outbox", (HttpContext ctx) => Allowed(ctx)
            ? Results.Json(OnUi(() =>
            {
                // Text has nothing to download: the page showing it is delivery.
                foreach (var o in Outbox.Where(o => o.Kind == PhoneOutboxKind.Text)) o.State = "Shown on phone";
                return Outbox.Select(o => new
                {
                    id = o.Id,
                    kind = o.Kind.ToString().ToLowerInvariant(),
                    name = o.Name,
                    size = o.Size,
                    text = o.Text,
                }).ToList();
            }), Json)
            : Results.StatusCode(403));
        app.MapGet("/web/send", Handler(SendItemAsync));
        app.MapGet("/web/send-all", Handler(SendAllAsync));
        app.MapGet("/web/events", Handler(EventsAsync));
    }

    private async Task<IResult> SendItemAsync(HttpContext ctx)
    {
        if (!Allowed(ctx)) return Results.StatusCode(403);
        var id = ctx.Request.Query["id"].ToString();
        var item = OnUi(() => Outbox.FirstOrDefault(o => o.Id == id));
        switch (item)
        {
            case null:
                return Results.NotFound();
            case { Kind: PhoneOutboxKind.Text }:
                return Results.Text(item.Text, "text/plain; charset=utf-8");
            case { Kind: PhoneOutboxKind.Folder }:
                if (!Directory.Exists(item.Path)) return Results.NotFound();
                await ZipAsync(ctx, item.Name + ".zip", [item]);
                return Results.Empty;
        }

        if (!File.Exists(item.Path)) return Results.NotFound();
        // A resumed download: let Kestrel serve the range (no progress for it).
        if (ctx.Request.Headers.Range.Count > 0)
            return Results.File(item.Path, ContentTypeOf(item.Path), item.Name, enableRangeProcessing: true);

        var path = item.Path;
        var length = new FileInfo(path).Length;
        ctx.Response.ContentType = ContentTypeOf(path);
        ctx.Response.ContentLength = length;
        ctx.Response.Headers.AcceptRanges = "bytes";
        ctx.Response.Headers.ContentDisposition = Attachment(item.Name);
        await OutgoingAsync(ctx, item.Name, length, async (body, transfer, ct) =>
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await body.WriteAsync(buffer.AsMemory(0, read), ct);
                transfer.Add(read);
            }
        }, [item]);
        return Results.Empty;
    }

    private async Task<IResult> SendAllAsync(HttpContext ctx)
    {
        if (!Allowed(ctx)) return Results.StatusCode(403);
        var items = OnUi(() => Outbox.Where(o => o.Kind != PhoneOutboxKind.Text).Reverse().ToList()); // oldest first
        if (items.Count == 0) return Results.NotFound();
        await ZipAsync(ctx, $"From {Environment.MachineName}.zip", items);
        return Results.Empty;
    }

    /// <summary>Streams files and folders as one .zip. Its size isn't known up front, so progress counts source bytes.</summary>
    private async Task ZipAsync(HttpContext ctx, string zipName, IReadOnlyList<PhoneOutboxItem> items)
    {
        var entries = new List<(string File, string Entry)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var item in items)
        {
            if (item.Kind == PhoneOutboxKind.File && File.Exists(item.Path))
            {
                entries.Add((item.Path, UniqueEntry(names, item.Name)));
            }
            else if (item.Kind == PhoneOutboxKind.Folder && Directory.Exists(item.Path))
            {
                var root = UniqueEntry(names, item.Name);
                foreach (var file in Directory.EnumerateFiles(item.Path, "*", walk))
                    entries.Add((file, root + "/" + Path.GetRelativePath(item.Path, file).Replace('\\', '/')));
            }
        }
        long total = 0;
        foreach (var (file, _) in entries)
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Gone since listing; skipped below.
            }
        }

        // ZipArchive only writes synchronously; this request is allowed to, on a pool thread.
        ctx.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = Attachment(zipName);
        await OutgoingAsync(ctx, zipName, total, (body, transfer, ct) => Task.Run(() =>
        {
            using var zip = new ZipArchive(body, ZipArchiveMode.Create, leaveOpen: true);
            var buffer = new byte[1 << 16];
            foreach (var (file, entry) in entries)
            {
                ct.ThrowIfCancellationRequested();
                FileStream input;
                try
                {
                    input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // locked or gone: leave it out rather than fail the whole zip
                }
                using (input)
                using (var output = zip.CreateEntry(entry, CompressionLevel.Fastest).Open())
                {
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        transfer.Add(read);
                    }
                }
            }
        }, ct), items);
    }

    private static string UniqueEntry(HashSet<string> names, string name)
    {
        var unique = name;
        for (var n = 2; !names.Add(unique); n++)
            unique = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
        return unique;
    }

    /// <summary>Runs a download as a tracked transfer (progress, cancel from the PC); marks the items downloaded.</summary>
    private async Task OutgoingAsync(HttpContext ctx, string title, long total,
        Func<Stream, PhoneTransfer, CancellationToken, Task> write, IReadOnlyList<PhoneOutboxItem> items)
    {
        var transfer = new PhoneTransfer(title, PeerName(ctx), incoming: false, total) { State = "Sending" };
        OnUi(() => Track(transfer));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, transfer.Cts.Token);
        try
        {
            await write(ctx.Response.Body, transfer, cts.Token);
            OnUi(() =>
            {
                transfer.Finish("Downloaded");
                foreach (var item in items) item.State = "Downloaded";
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            OnUi(() => transfer.Finish(transfer.Cts.IsCancellationRequested ? "Canceled" : "Stopped", failed: true));
            ctx.Abort(); // a cut-short body must not look like a complete file
        }
    }

    private static string Attachment(string name)
    {
        var header = new ContentDispositionHeaderValue("attachment");
        header.SetHttpFileName(name);
        return header.ToString();
    }

    // ---------------------------------------------------------------- live updates (Server-Sent Events)

    /// <summary>Tells every open page that something changed ("outbox" or "shelf"). Any thread.</summary>
    private void Push(string name)
    {
        lock (_eventsLock)
        {
            foreach (var channel in _events) channel.Writer.TryWrite(name);
        }
    }

    private async Task<IResult> EventsAsync(HttpContext ctx)
    {
        if (!Allowed(ctx)) return Results.StatusCode(403);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_eventsLock) _events.Add(channel);
        OnUi(UpdateConnected);

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var aborted = ctx.RequestAborted;
        try
        {
            await WriteAsync(": hello\n\n");
            var reader = channel.Reader;
            while (!aborted.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                wait.CancelAfter(PingEvery);
                bool open;
                try
                {
                    open = await reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    await WriteAsync(": ping\n\n"); // keeps phones and routers from dropping an idle connection
                    continue;
                }
                if (!open) break; // server stopping
                while (reader.TryRead(out var name)) await WriteAsync($"event: {name}\ndata: 1\n\n");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Page closed or phone went to sleep; it reconnects by itself.
        }
        finally
        {
            lock (_eventsLock) _events.Remove(channel);
            OnUi(UpdateConnected);
        }
        return Results.Empty;

        async Task WriteAsync(string text)
        {
            await ctx.Response.WriteAsync(text, aborted);
            await ctx.Response.Body.FlushAsync(aborted);
        }
    }

    private void UpdateConnected()
    {
        lock (_eventsLock) PhoneConnected = IsRunning && _events.Count > 0;
    }

    /// <summary>Ends every live connection so the server can stop promptly.</summary>
    private void CloseEvents()
    {
        lock (_eventsLock)
        {
            foreach (var channel in _events) channel.Writer.TryComplete();
        }
        PhoneConnected = false;
    }

    private void WatchShelf()
    {
        if (_shelfWatched) return;
        _shelfWatched = true;
        ShelfService.Instance.Items.CollectionChanged += (_, _) => Push("shelf");
    }
}
