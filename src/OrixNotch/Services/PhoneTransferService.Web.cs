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

    private const string KeyFile = "phone-key.bin";
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("OrixNotch.PhoneKey");

    /// <summary>The saved key, or a new one (saved) when there's none yet.</summary>
    private void LoadOrCreateWebKey()
    {
        var path = Storage.PathOf(KeyFile);
        try
        {
            if (File.Exists(path))
            {
                var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), KeyEntropy, DataProtectionScope.CurrentUser));
                if (key.Length >= 12)
                {
                    _webKey = key;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            App.Log(ex);
        }
        _webKey = RandomId();
        try
        {
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(_webKey), KeyEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            App.Log(ex); // the link still works until the app restarts
        }
    }

    private static void ForgetWebKey()
    {
        try
        {
            File.Delete(Storage.PathOf(KeyFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
        }
    }

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
            image = Shell.DropPayload.IsImagePath(f),
        }), Json) : Results.StatusCode(403));
        // What the phone sent this session, so the page's history survives a reload.
        app.MapGet("/web/received", (HttpContext ctx) => Allowed(ctx) ? Results.Json(OnUi(() => Transfers
            .Where(t => t.Incoming && !t.IsActive && !t.Failed)
            .Select(t => new
            {
                kind = t.Text is null ? "file" : "text",
                name = t.Title,
                size = t.Total,
                text = t.Text,
                at = t.At,
            }).ToList()), Json) : Results.StatusCode(403));
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
                Received?.Invoke(peer, 1, false);
                Push("received");
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
        var firstLine = text.Trim().Split('\n')[0].Trim();
        var transfer = new PhoneTransfer(firstLine.Length > 60 ? firstLine[..57] + "…" : firstLine, peer, incoming: true, Encoding.UTF8.GetByteCount(text))
        {
            Text = text,
        };
        transfer.Finish("Text · click to copy");
        OnUi(() =>
        {
            Track(transfer);
            Received?.Invoke(peer, 1, true);
            Push("received");
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
        return _page.Replace("{{NAME}}", WebUtility.HtmlEncode(Environment.MachineName)).Replace("{{ACCENT}}", AccentHex());
    }

    /// <summary>The notch's accent, so the page matches the PC's theme.</summary>
    private static string AccentHex()
    {
        try
        {
            var c = OnUi(() => ThemeService.CurrentAccent.Mean);
            return $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return "#ff8a3d";
        }
    }

    private static string ContentTypeOf(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";

    private const string Denied =
        "<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>" +
        "<meta name=color-scheme content='dark light'><title>OrixNotch · Link turned off</title>" +
        "<style>:root{--bg:#0a0a0c;--text:#f4f4f6;--sub:#8b8b93;--card:#151518;--line:#26262b}" +
        "@media(prefers-color-scheme:light){:root{--bg:#f3f3f6;--text:#141417;--sub:#6b6b73;--card:#fff;--line:#e4e4ea}}" +
        "body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--text);" +
        "font:15px/1.5 system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;padding:24px;box-sizing:border-box}" +
        "div{max-width:360px;text-align:center;background:var(--card);border:1px solid var(--line);border-radius:22px;padding:32px 24px}" +
        "i{display:block;width:12px;height:12px;border-radius:50%;background:#ff453a;margin:0 auto 18px;box-shadow:0 0 0 6px #ff453a26}" +
        "h1{font-size:20px;margin:0 0 8px}p{margin:0;color:var(--sub)}</style>" +
        "<div><i></i><h1>Link turned off</h1><p>Phone transfer was turned off on the PC. Turn on Phone in OrixNotch and scan the new code.</p></div>";
}
