using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// Store screenshot mode (dev only): with <c>ORIXNOTCH_SHOTS=&lt;dir&gt;</c> set, walks every tool and a few
/// notch states, renders the notch to transparent 3× PNGs in that folder, then exits. Run it against seeded
/// demo data (<c>ORIXNOTCH_DATA_DIR</c>); see marketing/store-shots/Make-StoreShots.ps1.
/// <c>ORIXNOTCH_SHOTS_ONLY=home,todos</c> limits the run. Before exiting it waits for a
/// <c>continue.flag</c> file in the folder (up to 2 min) so the script can grab the phone page meanwhile.
/// </summary>
public partial class NotchWindow
{
    private const double ShotDpi = 288; // 3x: crisp even when shown large in a 4K frame

    /// <summary>Screenshot mode slows the corner turn down so its in-between frames can be caught.</summary>
    private double _shotTimeScale = 1;

    public static string? ShotsDir => Environment.GetEnvironmentVariable("ORIXNOTCH_SHOTS") is { Length: > 0 } dir ? dir : null;

    public async void RunShots()
    {
        var dir = ShotsDir!;
        Directory.CreateDirectory(dir);
        var only = Environment.GetEnvironmentVariable("ORIXNOTCH_SHOTS_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Want(string name) => only is null || only.Contains(name);
        var manifest = new Dictionary<string, int[]>();

        async Task Shot(string name)
        {
            await Settle();
            UpdateLayout();
            // Render the whole window as it appears, then crop to the notch silhouette.
            var root = (FrameworkElement)Content;
            if (Shape.Data is not { } data || root.ActualWidth <= 0) return;
            var notch = Shape.TransformToAncestor(root).TransformBounds(data.Bounds);
            notch.Inflate(1, 1);
            notch.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            if (notch.IsEmpty) return;
            var scale = ShotDpi / 96;
            var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), ShotDpi, ShotDpi, PixelFormats.Pbgra32);
            full.Render(root);
            var crop = new Int32Rect((int)Math.Floor(notch.X * scale), (int)Math.Floor(notch.Y * scale),
                (int)Math.Ceiling(notch.Width * scale), (int)Math.Ceiling(notch.Height * scale));
            crop.Width = Math.Min(crop.Width, full.PixelWidth - crop.X);
            crop.Height = Math.Min(crop.Height, full.PixelHeight - crop.Y);
            var bitmap = new CroppedBitmap(full, crop);
            var (w, h) = (crop.Width, crop.Height);
            Storage.SavePng(bitmap, Path.Combine(dir, name + ".png"));
            manifest[name] = [w, h];
        }

        async Task Tool(string id, int extraMs = 900, string? name = null)
        {
            name ??= id;
            if (!Want(name)) return;
            if (!_expanded) Expand(activate: false);
            _pinned = true;
            ShowTool(id);
            await Task.Delay(extraMs);
            await Shot(name);
        }

        await Task.Delay(1500); // services warm up (weather, stocks, media session)

        // Collapsed notch: whatever is live first (music if something plays), then idle-only below.
        if (Want("pill-live")) await Shot("pill-live");

        await Tool("home", 1500);
        await Tool("ask", 1500);
        await Tool("aiusage", 3000);
        await Tool("shelf");
        await Tool("clipboard");
        await Tool("shortcuts", 1500);
        if (NowPlayingService.Instance.HasSession) await Tool("nowplaying", 2500);
        await Tool("note");
        await Tool("todos");
        await Tool("calendar");
        await Tool("notifications", 1500);
        await Tool("weather", 5000);
        await Tool("system", 2500);
        await Tool("stocks", 5000);
        await Tool("converter");
        await Tool("emoji", 2500);
        await Tool("teleprompter");
        await Tool("screentime", 1500);
        await Tool("controls", 1500);
        await Tool("subscriptions", 1200);

        if (Want("drop-chooser"))
        {
            ShowDropChooser();
            await Task.Delay(700);
            await Shot("drop-chooser");
            HideDropChooser(restore: true);
        }

        // Phone: put a few things in the outbox so the phone page has something to show.
        if (Want("phone"))
        {
            var shelf = Storage.PathOf("shelf");
            if (Directory.Exists(shelf)) PhoneTransferService.Instance.Send(Directory.GetFiles(shelf).Take(3));
            PhoneTransferService.Instance.SendText("Meeting moved to 3:30 — room 4B");
            await Tool("phone", 1500);
        }

        async Task SettingsShot(string name, string page, string? section = null)
        {
            if (!Want(name)) return;
            if (!_expanded) Expand(activate: false);
            _pinned = true;
            OpenTool(SettingsId);
            if (_views.GetValueOrDefault(SettingsId) is Settings.SettingsView settings)
            {
                settings.ShowPage(page);
                if (section is not null) settings.ScrollToSection(section);
            }
            await Task.Delay(1200);
            await Shot(name);
        }
        await SettingsShot("settings", "appearance");
        await SettingsShot("settings-behavior", "display", "WHEN IT SHOWS");          // show / auto-hide / full screen / capture
        await SettingsShot("settings-hideapps", "display", "HIDE FOR THESE APPS");

        // Alerts out of the closed notch.
        async Task Peek(string name, Action show)
        {
            if (!Want(name)) return;
            _pinned = false;
            Collapse();
            await Task.Delay(900);
            show();
            await Task.Delay(400);
            await Shot(name);
            EndPeek();
            await Task.Delay(700);
        }
        await Peek("peek-download", () => ShowDownloadPeek(Path.Combine(Storage.PathOf("shelf"), "Brand assets.zip")));
        await Peek("peek-bluetooth", () => ShowBluetoothPeek("AirPods Pro", true));
        await Peek("peek-battery", () => ShowPowerPeek("Charging", "84%", true));

        // Pomodoro running: the Timers panel and the collapsed timer pill.
        if (Want("timer") || Want("pill-timer"))
        {
            PomodoroService.Instance.ToggleCommand.Execute(null);
            await Task.Delay(2200);
            await Tool("timer");
            if (Want("pill-timer"))
            {
                Collapse();
                await Task.Delay(800);
                await Shot("pill-timer");
            }
            PomodoroService.Instance.ToggleCommand.Execute(null);
        }

        // The same Home panel in a few schemes, and with glass.
        var scheme = App.Settings.ThemeScheme;
        foreach (var id in new[] { "aurora", "nebula", "daylight", "sunset", "ocean" })
        {
            if (!Want("theme-" + id)) continue;
            ThemeService.SetScheme(id);
            await Tool("home", 700, "theme-" + id);
        }
        if (Want("theme-glass"))
        {
            ThemeService.SetScheme("nebula");
            App.Settings.Glass = true;
            ThemeService.Apply();
            await Tool("home", 700, "theme-glass");
            App.Settings.Glass = false;
        }
        ThemeService.SetScheme(scheme);

        if (Want("pill-idle"))
        {
            Collapse();
            await Task.Delay(800);
            await Shot("pill-idle");
        }

        await EdgeShots(dir, Want);

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var flag = Path.Combine(dir, "continue.flag");
        for (var i = 0; i < 240 && !File.Exists(flag); i++) await Task.Delay(500);
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Shapes, screen edges and the corner turn. These are whole-window renders (transparent, 3×) with where the window
    /// sat on the screen, written to screens.js (<c>window.SCREENS[name] = [x, y, w, h, screenW, screenH]</c> in DIPs)
    /// so a frame can put each one on a drawn monitor exactly where it was.
    /// </summary>
    private async Task EdgeShots(string dir, Func<string, bool> want)
    {
        var screens = new Dictionary<string, double[]>();
        var (edge, shape, corner) = (App.Settings.NotchEdge, App.Settings.NotchShape, App.Settings.CornerStyle);

        void Capture(string name)
        {
            UpdateLayout();
            var root = (FrameworkElement)Content;
            if (root.ActualWidth <= 0) return;
            var scale = ShotDpi / 96;
            var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), ShotDpi, ShotDpi, PixelFormats.Pbgra32);
            full.Render(root);
            Storage.SavePng(full, Path.Combine(dir, name + ".png"));
            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = root.PointToScreen(new Point(0, 0));
            var b = CurrentScreen().Bounds;
            screens[name] = [Math.Round((origin.X - b.Left) / dpi.DpiScaleX, 1), Math.Round((origin.Y - b.Top) / dpi.DpiScaleY, 1),
                Math.Round(root.ActualWidth, 1), Math.Round(root.ActualHeight, 1), Math.Round(b.Width / dpi.DpiScaleX), Math.Round(b.Height / dpi.DpiScaleY)];
        }

        async Task Use(string newEdge, string newShape)
        {
            _pinned = false;
            Collapse();
            App.Settings.NotchEdge = newEdge;
            App.Settings.NotchShape = newShape;
            SettingsService.Save(notify: true);
            await Task.Delay(900);
            await Settle();
        }

        async Task Closed(string name)
        {
            if (!want(name)) return;
            await Settle();
            Capture(name);
        }

        async Task Open(string name, string tool)
        {
            if (!want(name)) return;
            Expand(activate: false);
            _pinned = true;
            ShowTool(tool);
            await Task.Delay(1500);
            await Settle();
            Capture(name);
            _pinned = false;
            Collapse();
            await Task.Delay(700);
        }

        foreach (var s in new[] { "Island", "Notch", "Tab", "Pill" })
        {
            if (!want("shape-" + s.ToLowerInvariant())) continue;
            await Use("Top", s);
            await Closed("shape-" + s.ToLowerInvariant());
        }
        if (want("edge-top-open"))
        {
            await Use("Top", "Island");
            await Open("edge-top-open", "home");
        }
        foreach (var e in new[] { "Bottom", "Left", "Right" })
        {
            var id = e.ToLowerInvariant();
            if (!want($"edge-{id}") && !want($"edge-{id}-open")) continue;
            await Use(e, "Island");
            await Closed($"edge-{id}");
            await Open($"edge-{id}-open", "home");
        }

        // The corner turn, top edge → right edge, slowed down: a few frames of the tuck, then of the bend.
        foreach (var style in new[] { "Tuck", "Bend" })
        {
            var id = style.ToLowerInvariant();
            if (!want("turn-" + id)) continue;
            await Use("Top", "Island");
            App.Settings.CornerStyle = style;
            _shotTimeScale = 0.04;
            StartCornerTurn(+1);
            var i = 0;
            foreach (var t in new[] { 0.06, 0.3, 0.5, 0.7, 0.94 })
            {
                while (Turning && (style == "Bend" ? _bendT : _turnT) < t) await Task.Delay(5);
                if (!Turning) break;
                Capture($"turn-{id}-{++i}");
            }
            while (Turning) await Task.Delay(20);
            _shotTimeScale = 1;
            screens["turn-" + id] = [i];
        }

        App.Settings.CornerStyle = corner;
        await Use(edge, shape);
        File.WriteAllText(Path.Combine(dir, "screens.js"),
            "window.SCREENS = " + JsonSerializer.Serialize(screens, new JsonSerializerOptions { WriteIndented = true }) + ";\n");
    }

    /// <summary>Waits for the springs to stop and the content to finish materialising.</summary>
    private async Task Settle()
    {
        for (var i = 0; i < 100 && (_animating || (_expanded && ExpandedLayer.Opacity < 0.999)); i++) await Task.Delay(50);
        await Task.Delay(250);
    }
}
