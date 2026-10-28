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
        menu.Items.Add("Settings…", null, (_, _) => window.OpenTool(NotchWindow.SettingsId));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit OrixNotch", null, (_, _) => Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) window.Toggle();
        };
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
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
