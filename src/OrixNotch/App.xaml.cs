using System.IO;
using System.Windows;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayService? _tray;

    public static AppSettings Settings => SettingsService.Current;

    /// <summary>
    /// The UI font: Latin in SF Pro when installed, otherwise the bundled Inter; Bengali always in the
    /// bundled Noto Sans Bengali, even inside mixed strings. Both variants are composite fonts shipped
    /// in Fonts/*.CompositeFont, whose targets are relative to the file. (A composite built in code
    /// can't point at pack:// resources: those targets silently fall back to Windows' Bengali font.)
    /// Nothing is installed, and the SF Pro check is one lookup instead of a scan of every system font.
    /// </summary>
    private static FontFamily BuildUiFont()
    {
        var sf = new Typeface("SF Pro Text").TryGetGlyphTypeface(out _);
        return new FontFamily(new Uri("pack://application:,,,/Fonts/"), sf ? "./#OrixUI SF" : "./#OrixUI");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "OrixNotch.SingleInstance", out var created);
        if (!created)
        {
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        // Before any UI measures text.
        Resources["UiFont"] = BuildUiFont();
        ThemeService.Apply();
        var window = new NotchWindow();
        MainWindow = window;
        window.Show();
        _tray = new TrayService(window);
        _ = StartupService.SyncAsync(); // open-at-login: on by default, kept in step with Windows

        // `OrixNotch.exe --open weather` starts with the panel open on a tool.
        var openAt = Array.IndexOf(e.Args, "--open");
        if (openAt >= 0)
        {
            window.Expand(activate: true);
            if (openAt + 1 < e.Args.Length) window.ShowTool(e.Args[openAt + 1]);
        }
        if (e.Args.Contains("--settings")) window.OpenTool(NotchWindow.SettingsId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        ShelfService.Instance.Save();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Storage.PathOf("error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
