using System.IO;
using System.Windows;

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

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Start()
    {
        if (_watcher is not null) return;
        try
        {
            var dir = ShellFolders.Screenshots;
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            foreach (var pattern in new[] { "*.png", "*.jpg", "*.jpeg" }) _watcher.Filters.Add(pattern);
            _watcher.Created += async (_, e) =>
            {
                await Task.Delay(500); // the capture is still being written
                Application.Current?.Dispatcher.BeginInvoke(() => Taken?.Invoke(e.FullPath));
            };
            // Buffer overflow or the folder going away kills the watcher silently: log it and start a fresh one.
            _watcher.Error += (_, e) =>
            {
                App.Log(e.GetException());
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Stop();
                    Start();
                });
            };
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
