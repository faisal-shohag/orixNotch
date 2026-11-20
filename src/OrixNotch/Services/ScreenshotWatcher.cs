using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace OrixNotch.Services;

/// <summary>
/// A new screenshot in the Screenshots folder (Win+PrtScn, Snipping Tool's auto-save). Raised on the UI thread
/// a moment after the file appears, so it's fully written.
/// </summary>
public sealed class ScreenshotWatcher
{
    public static ScreenshotWatcher Instance { get; } = new();

    public event Action<string>? Taken;

    private FileSystemWatcher? _watcher;

    public void Start()
    {
        if (_watcher is not null) return;
        try
        {
            var dir = ScreenshotsFolder();
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            foreach (var pattern in new[] { "*.png", "*.jpg", "*.jpeg" }) _watcher.Filters.Add(pattern);
            _watcher.Created += async (_, e) =>
            {
                await Task.Delay(500); // the capture is still being written
                Application.Current?.Dispatcher.BeginInvoke(() => Taken?.Invoke(e.FullPath));
            };
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>The user's Screenshots folder, even if moved (Explorer's "User Shell Folders" entry).</summary>
    private static string ScreenshotsFolder()
    {
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        if (Registry.GetValue(key, "{B7BEDE81-DF94-4682-A7D8-57A52620B86F}", null) is string path && path.Length > 0)
            return Environment.ExpandEnvironmentVariables(path);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }
}
