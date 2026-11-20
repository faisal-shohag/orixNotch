using System.Security.Cryptography;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OrixNotch.Services;

/// <summary>
/// JSON persistence under %LOCALAPPDATA%\OrixNotch. ORIXNOTCH_DATA_DIR overrides the folder
/// (used by end-to-end tests so they never touch real data).
/// </summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Root { get; } =
        Environment.GetEnvironmentVariable("ORIXNOTCH_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrixNotch");

    static Storage() => Directory.CreateDirectory(Root);

    public static string PathOf(string name) => Path.Combine(Root, name);

    public static string Dir(string name)
    {
        var dir = PathOf(name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var path = PathOf(name);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            // The next Save would overwrite the unreadable file with defaults: keep a copy the user can recover.
            try { File.Copy(PathOf(name), $"{PathOf(name)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
            catch (Exception copyEx) { App.Log(copyEx); }
        }
        return new T();
    }

    /// <summary>
    /// Writes via a temp file and a move, so a crash mid-write leaves the old file instead of a torn one (a torn
    /// DPAPI blob would read as "no key" and lose the secret). Throws like File.WriteAllBytes.
    /// </summary>
    public static void WriteAllBytesAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A DPAPI-protected (current user) secret from the data folder; null when missing or unreadable (logged).</summary>
    public static string? LoadSecret(string name, byte[] entropy)
    {
        try
        {
            var path = PathOf(name);
            if (!File.Exists(path)) return null;
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>Saves a secret DPAPI-protected and atomically; null or empty deletes it. Throws when it can't be written.</summary>
    public static void SaveSecret(string name, string? value, byte[] entropy)
    {
        var path = PathOf(name);
        if (string.IsNullOrEmpty(value))
        {
            File.Delete(path);
            return;
        }
        WriteAllBytesAtomic(path, ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Writes an image as a PNG file (clipboard copies, the phone outbox, the avatar, the day's wrap).</summary>
    public static void SavePng(System.Windows.Media.Imaging.BitmapSource image, string path)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            var path = PathOf(name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}

public static class Http
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OrixNotch/" + UpdateService.CurrentVersion);
        return client;
    }
}
