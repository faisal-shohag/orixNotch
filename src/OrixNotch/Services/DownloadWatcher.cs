using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace OrixNotch.Services;

/// <summary>
/// A browser download finishing in the Downloads folder. Browsers write to a temporary name
/// (Chrome/Edge ".crdownload", Firefox ".part") and rename it to the real one when done; that rename is the signal.
/// </summary>
public sealed class DownloadWatcher
{
    public static DownloadWatcher Instance { get; } = new();

    /// <summary>Full path of the finished file (raised on the UI thread).</summary>
    public event Action<string>? Finished;

    private static readonly string[] Partial = [".crdownload", ".part", ".partial", ".download"];
    private FileSystemWatcher? _watcher;

    public void Start()
    {
        if (_watcher is not null) return;
        try
        {
            var dir = DownloadsFolder();
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            _watcher.Renamed += (_, e) =>
            {
                if (IsPartial(e.OldName) && !IsPartial(e.Name))
                    Application.Current?.Dispatcher.BeginInvoke(() => Finished?.Invoke(e.FullPath));
            };
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static bool IsPartial(string? name) =>
        name is not null && Partial.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>The user's Downloads folder, even if they moved it (Explorer's "User Shell Folders" entry).</summary>
    private static string DownloadsFolder()
    {
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        if (Registry.GetValue(key, "{374DE290-123F-4565-9164-39C4925E467B}", null) is string path && path.Length > 0)
            return Environment.ExpandEnvironmentVariables(path);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
