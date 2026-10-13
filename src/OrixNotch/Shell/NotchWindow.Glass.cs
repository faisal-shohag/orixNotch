using System.Windows;
using System.Windows.Media;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// Glass mode: the notch is a see-through tint (PillBrush from <see cref="ThemeService"/>) with a sheen
/// across the top and a faint accent glow at the bottom.
/// No real backdrop blur: the notch is a layered window, and DWM's acrylic paints a layered window as a
/// black rectangle; on a separate plain window it ignores the window region, so it can't follow the notch shape.
/// </summary>
public partial class NotchWindow
{
    private void ApplyGlass()
    {
        var on = ThemeService.IsGlass;
        GlassSheen.Visibility = GlassGlow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GlassSheen.Data = GlassGlow.Data = on ? Shape.Data : null;
        var accent = ThemeService.CurrentAccent.Mean;
        GlassGlow.Fill = new RadialGradientBrush(Color.FromArgb(0x1C, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B))
        {
            Center = new Point(0.5, 1.05),
            GradientOrigin = new Point(0.5, 1.05),
            RadiusX = 0.6,
            RadiusY = 0.7,
        };
    }
}
