using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>
/// <c>POST http://127.0.0.1:{port}/peek</c> with <c>{"title": "Build done", "sub": "…", "icon": "Check"}</c> shows an
/// alert, so scripts, CI and coding agents can ping the notch. Loopback only, never the LAN phone server; every
/// request needs the token, and requests from web pages (any Origin header) are refused. Off until turned on.
/// </summary>
public sealed class PeekApiService
{
    public static PeekApiService Instance { get; } = new();

    private const string TokenFile = "peek-token.bin";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("OrixNotch.PeekToken");

    private WebApplication? _server;
    private readonly SemaphoreSlim _gate = new(1, 1); // quick toggles in Settings must not start two servers
    private string? _token;

    /// <summary>Why the server isn't running although it's on (port taken), or null.</summary>
    public string? Error { get; private set; }

    public bool Running => _server is not null;

    public string Token => _token ??= LoadToken() ?? NewToken();

    /// <summary>A ready-to-run example for the Settings page.</summary>
    public string CurlExample =>
        $"curl -X POST http://127.0.0.1:{App.Settings.PeekApiPort}/peek -H \"Authorization: Bearer {Token}\" -H \"Content-Type: application/json\" -d \"{{\\\"title\\\":\\\"Build done\\\"}}\"";

    /// <summary>Starts or stops to match the setting.</summary>
    public async Task SyncAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopAsync();
            Error = null;
            if (!App.Settings.PeekApiEnabled) return;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = 4096;
                k.Listen(IPAddress.Loopback, App.Settings.PeekApiPort);
            });
            var app = builder.Build();
            app.MapPost("/peek", (Delegate)HandlePeekAsync);
            await app.StartAsync();
            _server = app;
        }
        catch (Exception ex)
        {
            Error = ex is Microsoft.AspNetCore.Connections.AddressInUseException || ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException
                ? $"Port {App.Settings.PeekApiPort} is in use by another app. Pick another port."
                : "Couldn't start. Details are in error.log.";
            App.Log(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        var server = _server;
        _server = null;
        if (server is null) return;
        try
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private sealed record PeekBody(string? Title, string? Text, string? Sub, string? Icon);

    private async Task<IResult> HandlePeekAsync(HttpContext ctx)
    {
        // A browser always sends Origin on a cross-site POST; scripts and curl don't. Stops web pages firing alerts.
        if (ctx.Request.Headers.ContainsKey("Origin")) return Results.StatusCode(403);
        var auth = ctx.Request.Headers.Authorization.ToString();
        var given = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token)))
            return Results.Unauthorized();

        PeekBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<PeekBody>(ctx.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "Send JSON like {\"title\": \"Build done\", \"sub\": \"optional\"}." });
        }
        var title = ExternalCommands.Clip(body?.Title ?? body?.Text);
        if (title.Length == 0) return Results.BadRequest(new { error = "\"title\" is required." });
        var sub = ExternalCommands.Clip(body?.Sub);
        _ = Application.Current.Dispatcher.BeginInvoke(() => NotchWindow.Instance?.ShowExternalPeek(title, sub, body?.Icon));
        return Results.Ok(new { ok = true });
    }

    // ---------------------------------------------------------------- token

    /// <summary>Replaces the token; scripts using the old one stop working.</summary>
    public string NewToken()
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            File.WriteAllBytes(Storage.PathOf(TokenFile), ProtectedData.Protect(Encoding.UTF8.GetBytes(_token), TokenEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            App.Log(ex); // works until the app restarts
        }
        return _token;
    }

    private static string? LoadToken()
    {
        try
        {
            var path = Storage.PathOf(TokenFile);
            if (!File.Exists(path)) return null;
            var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), TokenEntropy, DataProtectionScope.CurrentUser));
            return token.Length >= 16 ? token : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            App.Log(ex);
            return null;
        }
    }
}
