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
    /// <summary>Draws the black-pill-with-orange-dot logo so no .ico asset is needed.</summary>
    public static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var path = RoundedRect(new Drawing.RectangleF(1.5f, 8.5f, 29f, 15f), 7.5f);
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 12, 12, 12));
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(230, 240, 240, 240), 1.6f);
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 255, 138, 61));
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            g.FillEllipse(dot, 19.5f, 12.5f, 7f, 7f);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
