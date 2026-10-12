using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace OrixNotch.Services;

/// <summary>
/// Browser page for the phone: scan the QR code on the notch, then upload to the PC
/// and download what's on the Shelf. Plain HTTP on the LAN; every request needs the key from the QR
/// code, which changes each time the server starts.
/// </summary>
public sealed partial class PhoneTransferService
{
    private const long MaxWebText = 1 << 20;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private string _webKey = "";
    private string? _page;

    private void NewWebKey() => _webKey = RandomId();

    /// <summary>Unguessable, URL-safe and easy to read out (no 0/o, 1/l).</summary>
    private static string RandomId()
    {
        const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        return string.Create(12, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
    }

    private void MapWeb(WebApplication app)
    {
        app.MapGet("/", (HttpContext ctx) => Allowed(ctx)
            ? Results.Content(Page(), "text/html; charset=utf-8")
            : Results.Content(Denied, "text/html; charset=utf-8", statusCode: 403));
        app.MapGet("/web/files", (HttpContext ctx) => Allowed(ctx) ? Results.Json(ShelfFiles().Select((f, i) => new
        {
            id = i,
            name = Path.GetFileName(f),
            size = new FileInfo(f).Length,
        }), Json) : Results.StatusCode(403));
        app.MapGet("/web/file", (HttpContext ctx) =>
        {
            if (!Allowed(ctx)) return Results.StatusCode(403);
            var files = ShelfFiles();
            if (!int.TryParse(ctx.Request.Query["id"], out var id) || id < 0 || id >= files.Count) return Results.NotFound();
            return Results.File(files[id], ContentTypeOf(files[id]), Path.GetFileName(files[id]), enableRangeProcessing: true);
        });
        app.MapPost("/web/upload", Handler(WebUploadAsync));
        app.MapPost("/web/text", Handler(WebTextAsync));
        MapOutbox(app);
    }

    private bool Allowed(HttpContext ctx) =>
        _webKey.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(ctx.Request.Query["k"].ToString()), Encoding.UTF8.GetBytes(_webKey));

    /// <summary>Shelf files (folders are skipped), read on the UI thread; ids index this list.</summary>
    private static List<string> ShelfFiles() =>
        OnUi(() => ShelfService.Instance.Items.Where(i => File.Exists(i.Path)).Select(i => i.Path).ToList());

    private static string PeerName(HttpContext ctx) => $"Browser ({ctx.Connection.RemoteIpAddress?.MapToIPv4()})";

    private async Task<IResult> WebUploadAsync(HttpContext ctx)
    {
        if (!Allowed(ctx)) return Results.StatusCode(403);
        var name = ctx.Request.Query["name"].ToString();
        if (name.Length == 0) return Results.BadRequest();
        var peer = PeerName(ctx);
        var transfer = new PhoneTransfer(Path.GetFileName(name.Replace('\\', '/')), peer, incoming: true, ctx.Request.ContentLength ?? 0)
        {
            State = "Receiving",
        };
        OnUi(() => Track(transfer));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, transfer.Cts.Token);
        try
        {
            var path = TargetPath(ReceiveFolder, name);
            await SaveBodyAsync(ctx, path, transfer, cts.Token);
            OnUi(() =>
            {
                transfer.SavedPath = path;
                transfer.Finish($"Saved to {Path.GetFileName(ReceiveFolder.TrimEnd('\\'))}");
                ShelfService.Instance.AddFiles([path]);
                Received?.Invoke(peer, 1);
            });
            return Results.Ok();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            OnUi(() => transfer.Finish(transfer.Cts.IsCancellationRequested ? "Canceled" : "Stopped", failed: true));
            return Results.StatusCode(500);
        }
    }

    private async Task<IResult> WebTextAsync(HttpContext ctx)
    {
        if (!Allowed(ctx)) return Results.StatusCode(403);
        if (ctx.Request.ContentLength > MaxWebText) return Results.StatusCode(413);
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest();
        var peer = PeerName(ctx);
        OnUi(() =>
        {
            ShelfService.Instance.AddText(text);
            Received?.Invoke(peer, 1);
        });
        return Results.Ok();
    }

    private string Page()
    {
        if (_page is null)
        {
            using var stream = typeof(PhoneTransferService).Assembly.GetManifestResourceStream("OrixNotch.Resources.phone.html")!;
            using var reader = new StreamReader(stream);
            _page = reader.ReadToEnd();
        }
        return _page.Replace("{{NAME}}", WebUtility.HtmlEncode(Environment.MachineName));
    }

    private static string ContentTypeOf(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";

    private const string Denied =
        "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'>" +
        "<body style='font:16px system-ui;background:#0b0b0d;color:#f5f5f7;padding:32px'>" +
        "<h2>Link expired</h2><p>Scan the QR code in OrixNotch's Phone tab again.</p>";
}
