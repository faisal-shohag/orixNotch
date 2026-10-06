using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OrixNotch.Services;

/// <summary>JSON persistence under %LOCALAPPDATA%\OrixNotch.</summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrixNotch");

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
        }
        return new T();
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OrixNotch/0.1");
        return client;
    }
}
