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

    /// <summary>Registers the bundled Noto Sans Bengali for this user (no admin needed),
    /// so the UiFont fallback map can resolve it. Skipped when already available.</summary>
    private static void EnsureBengaliFont()
    {
        const string family = "Noto Sans Bengali";
        try
        {
            if (Fonts.SystemFontFamilies.Any(f => f.Source.StartsWith(family, StringComparison.OrdinalIgnoreCase)))
                return;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", writable: true);
            // Another app (or a previous run) may have registered it under a different value name.
            if (key?.GetValueNames().Any(n => n.Contains(family, StringComparison.OrdinalIgnoreCase)) == true)
                return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");
            Directory.CreateDirectory(dir);
            const string fileName = "NotoSansBengali-OrixNotch.ttf";
            var dest = Path.Combine(dir, fileName);
            if (!File.Exists(dest))
            {
                using var src = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Fonts/NotoSansBengali.ttf", UriKind.Absolute))?.Stream;
                if (src is null) return;
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
            key?.SetValue($"{family} (TrueType)", fileName, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log(ex);
        }
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

        // Bengali script renders in Noto Sans Bengali (see UiFont in Theme.xaml): make sure
        // the family is registered before any UI measures text. No admin needed (per-user).
        EnsureBengaliFont();

        // Use Apple's SF Pro when the user has installed it; otherwise the bundled Inter (Theme.xaml).
        var sf = System.Windows.Media.Fonts.SystemFontFamilies
            .FirstOrDefault(f => f.Source is "SF Pro Text" or "SF Pro Display" or "SF Pro");
        if (sf is not null) Resources["UiFont"] = sf;
        ThemeService.Apply();
        var window = new NotchWindow();
        MainWindow = window;
        window.Show();
        _tray = new TrayService(window);

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
