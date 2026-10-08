using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OrixNotch.Shell;

/// <summary>
/// Loading placeholders: rounded blocks in the theme's hover color that pulse softly.
/// The pulse only runs while the block is visible, so hidden skeletons cost nothing.
/// </summary>
public static class Skeleton
{
    public static Border Block(double width, double height, double radius = 4, Thickness margin = default,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var block = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Margin = margin,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (double.IsNaN(width)) block.HorizontalAlignment = HorizontalAlignment.Stretch;
        block.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        Pulse(block);
        return block;
    }

    public static Border Circle(double size, Thickness margin = default, HorizontalAlignment align = HorizontalAlignment.Left) =>
        Block(size, size, size / 2, margin, align);

    /// <summary>Pulses an element's opacity while it is visible.</summary>
    public static void Pulse(UIElement element)
    {
        void Update()
        {
            if (element.IsVisible)
            {
                element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(850))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            }
            else
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }

        element.IsVisibleChanged += (_, _) => Update();
        Update();
    }
}
