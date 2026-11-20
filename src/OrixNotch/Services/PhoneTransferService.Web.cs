using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        if (Storage.LoadSecret(KeyFile, KeyEntropy) is { Length: >= 12 } key)
        {
            _webKey = key;
            return;
        }
        _webKey = RandomId();
        try
        {
            Storage.SaveSecret(KeyFile, _webKey, KeyEntropy);
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
        app.MapGet("/web/files", (HttpContext ctx) => Allowed(ctx) ? Results.Json(ShelfFiles().Select(f => new
        {
            id = FileId(f),
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
            var id = ctx.Request.Query["id"].ToString();
            var file = ShelfFiles().FirstOrDefault(f => FileId(f) == id);
            if (file is null) return Results.NotFound(); // gone or changed since the page loaded: never another file
            return Results.File(file, ContentTypeOf(file), Path.GetFileName(file), enableRangeProcessing: true);
        });
        app.MapPost("/web/upload", Handler(WebUploadAsync));
        app.MapPost("/web/text", Handler(WebTextAsync));
        MapOutbox(app);
    }

    private bool Allowed(HttpContext ctx) =>
        _webKey.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(ctx.Request.Query["k"].ToString()), Encoding.UTF8.GetBytes(_webKey));

    /// <summary>
    /// Stable id of a Shelf file for the phone page: the same file keeps its id while the Shelf changes around it,
    /// and an edited or different file gets a new one. (Ids used to be list positions, so a new photo could reuse an
    /// old one's URL and the phone showed the cached old photo.)
    /// </summary>
    internal static string FileId(string path)
    {
        var info = new FileInfo(path);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Shelf files (folders are skipped), read on the UI thread.</summary>
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
        // UnauthorizedAccess: a receive folder that can't be written must still end the transfer (or it spins forever).
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            var state = transfer.Cts.IsCancellationRequested ? "Canceled" : ex is UnauthorizedAccessException ? "Can't write to the receive folder" : "Stopped";
            OnUi(() => transfer.Finish(state, failed: true));
            return Results.StatusCode(500);
        }
    }

    private async Task<IResult> WebTextAsync(HttpContext ctx)
    {
        if (!Allowed(ctx)) return Results.StatusCode(403);
        if (ctx.Request.ContentLength > MaxWebText) return Results.StatusCode(413);
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        // A chunked body has no Content-Length: read at most the limit (+1 to tell it was over) instead of to the end.
        var buffer = new char[MaxWebText + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), ctx.RequestAborted);
        if (length > MaxWebText) return Results.StatusCode(413);
        var text = new string(buffer, 0, length);
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
            _page = Resource("phone.html")
                .Replace("{{MARK}}", SvgUri(BrandMarkSvg(tile: false)))
                .Replace("{{FAVICON}}", SvgUri(BrandMarkSvg(tile: true)))
                .Replace("{{TOUCH_ICON}}", "data:image/png;base64," + Convert.ToBase64String(ResourceBytes("logo-512.png")));
        }
        // NAME_JS first: the default JSON encoder escapes < > & ' so the name can't close the script or the string.
        return _page.Replace("{{NAME_JS}}", System.Text.Json.JsonSerializer.Serialize(PcName)).Replace("{{NAME}}", WebUtility.HtmlEncode(PcName)).Replace("{{ACCENT}}", AccentHex());
    }

    /// <summary>
    /// The OrixNotch mark from Assets/Brand/mark.svg (white paths, each with its own transform) as a standalone SVG in
    /// the logo's colours: one pink → orange sweep across the whole mark, like Shell/BrandMark.cs draws it. The paths are
    /// a mask over one gradient rect, because a gradient on each path would restart on each piece. With
    /// <paramref name="tile"/>: on the brand's dark rounded tile (the favicon).
    /// </summary>
    private static string BrandMarkSvg(bool tile)
    {
        var paths = string.Concat(Regex.Matches(Resource("mark.svg"), @"<path\b[^>]*/>").Select(m => m.Value));
        // x 212..1020 is the mark's extent in its 1254 viewBox (measured from the path data).
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1254 1254\"><defs>"
            + "<linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"212\" y1=\"0\" x2=\"1020\" y2=\"0\">"
            + "<stop offset=\"0\" stop-color=\"#FD0388\"/><stop offset=\"1\" stop-color=\"#FC6F18\"/></linearGradient>"
            + $"<mask id=\"m\">{paths}</mask></defs>"
            + (tile ? "<rect width=\"1254\" height=\"1254\" rx=\"280\" fill=\"#0B0E17\"/>" : "")
            + "<rect width=\"1254\" height=\"1254\" fill=\"url(#g)\" mask=\"url(#m)\"/></svg>";
    }

    private static string SvgUri(string svg) => "data:image/svg+xml," + Uri.EscapeDataString(svg);

    private static byte[] ResourceBytes(string name)
    {
        using var stream = typeof(PhoneTransferService).Assembly.GetManifestResourceStream("OrixNotch.Resources." + name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Resource(string name) => Encoding.UTF8.GetString(ResourceBytes(name)).TrimStart('﻿');

    /// <summary>How the page names this PC (ORIXNOTCH_PC_NAME overrides it, for screenshots).</summary>
    private static string PcName => Environment.GetEnvironmentVariable("ORIXNOTCH_PC_NAME") is { Length: > 0 } name ? name : Environment.MachineName;

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
