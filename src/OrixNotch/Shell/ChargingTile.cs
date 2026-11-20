using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace OrixNotch.Shell;

/// <summary>Charger plugged in: a battery on the alert's tile fills up to the current level.</summary>
public static class ChargingTile
{
    public static FrameworkElement Create(int percent)
    {
        const double bodyW = 22, bodyH = 12, inset = 2;
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11) };
        tile.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");

        var canvas = new Canvas { Width = bodyW + 3, Height = bodyH, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var body = new Rectangle { Width = bodyW, Height = bodyH, RadiusX = 3, RadiusY = 3, StrokeThickness = 1.4 };
        body.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        var nub = new Rectangle { Width = 2, Height = 5, RadiusX = 1, RadiusY = 1 };
        nub.SetResourceReference(Shape.FillProperty, "TextBrush");
        Canvas.SetLeft(nub, bodyW + 1);
        Canvas.SetTop(nub, (bodyH - 5) / 2);
        var fill = new Rectangle { Height = bodyH - inset * 2, RadiusX = 1.5, RadiusY = 1.5, Width = 0 };
        fill.SetResourceReference(Shape.FillProperty, "GoodBrush");
        Canvas.SetLeft(fill, inset);
        Canvas.SetTop(fill, inset);
        canvas.Children.Add(fill);
        canvas.Children.Add(body);
        canvas.Children.Add(nub);
        tile.Child = canvas;

        var target = Math.Max(1.5, (bodyW - inset * 2) * Math.Clamp(percent, 0, 100) / 100.0);
        tile.Loaded += (_, _) => fill.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(900))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        return tile;
    }
}
