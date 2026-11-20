using System.Windows;
using OrixNotch.Shell;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace OrixNotch.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public static TrayService? Instance { get; private set; }

    public TrayService(NotchWindow window)
    {
        Instance = this;
        _icon = new Forms.NotifyIcon
        {
            Icon = IconFactory.CreateTrayIcon(),
            Text = "OrixNotch",
            Visible = true,
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open panel  (Ctrl+Alt+N)", null, (_, _) => window.Expand(activate: true));
        menu.Items.Add("Command palette…", null, (_, _) => window.ShowPalette());
        menu.Items.Add("Start focus", null, (_, _) => PomodoroService.Instance.StartFocus());
        menu.Items.Add("Settings…", null, (_, _) => window.OpenTool(NotchWindow.SettingsId));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var quiet = new Forms.ToolStripMenuItem("Quiet for 1 hour", null, (_, _) => NotchWindow.ToggleQuietHour());
        menu.Items.Add(quiet);
        menu.Items.Add("Copy text from screen", null, (_, _) => OcrService.Instance.Start());
        var mic = new Forms.ToolStripMenuItem("Mute microphone", null, (_, _) => VolumeService.Instance.ToggleMic());
        menu.Items.Add(mic);
        var awake = new Forms.ToolStripMenuItem("Keep awake", null, (_, _) => KeepAwakeService.Toggle());
        menu.Items.Add(awake);
        menu.Opening += (_, _) =>
        {
            mic.Visible = VolumeService.Instance.HasMic;
            mic.Checked = VolumeService.Instance.MicMuted;
            awake.Checked = KeepAwakeService.On;
            quiet.Text = NotchWindow.QuietSnoozed() ? "End quiet hour"
                : NotchWindow.QuietScheduled() ? $"Quiet hours until {App.Settings.QuietTo}"
                : "Quiet for 1 hour";
            quiet.Enabled = !NotchWindow.QuietScheduled() || NotchWindow.QuietSnoozed();
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit OrixNotch", null, (_, _) => Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) window.Toggle();
        };
    }

    public void Notify(string title, string text)
    {
        _sent = (title, DateTime.Now);
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
    }

    private static (string Title, DateTime At) _sent;

    /// <summary>
    /// Our own balloon coming back through the notification mirror: Windows files tray balloons under a generated app
    /// id ("NotifyIconGeneratedAumid_…"), so the "our own toasts" check by id misses them.
    /// </summary>
    public static bool SentRecently(string title) => _sent.Title == title && DateTime.Now - _sent.At < TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        _icon.Visible = false;
        // NotifyIcon.Dispose leaves its Icon (a GDI handle) and menu to their owner.
        var (icon, menu) = (_icon.Icon, _icon.ContextMenuStrip);
        _icon.Dispose();
        icon?.Dispose();
        menu?.Dispose();
    }
}

internal static class IconFactory
{
    /// <summary>The app icon (Assets/Brand/OrixNotch.ico) at the tray's size, so the tray matches the taskbar.</summary>
    public static Drawing.Icon CreateTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Brand/OrixNotch.ico"));
        using var stream = resource.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
