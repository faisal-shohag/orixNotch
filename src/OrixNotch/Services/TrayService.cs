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
    /// <summary>Draws the OrixNotch brand pill (blue → purple → pink gloss) so the tray matches the app icon.</summary>
    public static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            // Dark rounded-square chip (matches Assets/Brand app icon).
            using var bgPath = RoundedRect(new Drawing.RectangleF(0, 0, 32, 32), 7.5f);
            using var bgFill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 11, 14, 23));
            g.FillPath(bgFill, bgPath);

            // Gradient pill.
            var pill = new Drawing.RectangleF(4.5f, 12f, 23f, 8f);
            using var pillPath = RoundedRect(pill, 4f);
            using var brush = new Drawing.Drawing2D.LinearGradientBrush(
                pill,
                Drawing.Color.FromArgb(255, 46, 156, 255),
                Drawing.Color.FromArgb(255, 217, 70, 239),
                Drawing.Drawing2D.LinearGradientMode.Horizontal);
            var blend = new Drawing.Drawing2D.ColorBlend(4);
            blend.Colors =
            [
                Drawing.Color.FromArgb(255, 46, 156, 255),
                Drawing.Color.FromArgb(255, 79, 70, 255),
                Drawing.Color.FromArgb(255, 124, 60, 255),
                Drawing.Color.FromArgb(255, 217, 70, 239),
            ];
            blend.Positions = [0f, 0.38f, 0.68f, 1f];
            brush.InterpolationColors = blend;
            g.FillPath(brush, pillPath);

            // Notch dip punched with the chip background + soft highlight.
            using var notch = Notch(new Drawing.RectangleF(11.5f, 11f, 9.5f, 4.5f));
            g.FillPath(bgFill, notch);
            using var edge = new Drawing.Pen(Drawing.Color.FromArgb(120, 255, 255, 255), 1f);
            g.DrawPath(edge, notch);
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

    private static Drawing.Drawing2D.GraphicsPath Notch(Drawing.RectangleF r)
    {
        var radius = r.Width * 0.16f;
        var d = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddLine(r.X, r.Y, r.Right, r.Y);
        path.AddLine(r.Right, r.Y, r.Right, r.Bottom - radius);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddLine(r.Right - radius, r.Bottom, r.X + radius, r.Bottom);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.AddLine(r.X, r.Bottom - radius, r.X, r.Y);
        path.CloseFigure();
        return path;
    }
}
