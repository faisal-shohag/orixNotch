using System.IO;
using System.Windows;

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
            var dir = ShellFolders.Downloads;
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            _watcher.Renamed += (_, e) =>
            {
                if (IsPartial(e.OldName) && !IsPartial(e.Name))
                    Application.Current?.Dispatcher.BeginInvoke(() => Finished?.Invoke(e.FullPath));
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

    private static bool IsPartial(string? name) =>
        name is not null && Partial.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}
