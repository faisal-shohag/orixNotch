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
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var file = File.Create(Path.Combine(dir, name + ".png")))
                encoder.Save(file);
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

        if (Want("settings"))
        {
            if (!_expanded) Expand(activate: false);
            _pinned = true;
            OpenTool(SettingsId);
            if (_views.GetValueOrDefault(SettingsId) is Settings.SettingsView settings) settings.ShowPage("appearance");
            await Task.Delay(900);
            await Shot("settings");
        }

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

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var flag = Path.Combine(dir, "continue.flag");
        for (var i = 0; i < 240 && !File.Exists(flag); i++) await Task.Delay(500);
        Application.Current.Shutdown();
    }

    /// <summary>Waits for the springs to stop and the content to finish materialising.</summary>
    private async Task Settle()
    {
        for (var i = 0; i < 100 && (_animating || (_expanded && ExpandedLayer.Opacity < 0.999)); i++) await Task.Delay(50);
        await Task.Delay(250);
    }
}
