using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace OrixNotch.Shell;

/// <summary>
/// Buttery mouse-wheel scrolling for a ScrollViewer: each wheel notch glides
/// the offset with a short ease-out instead of jumping. Dragging, clicking or
/// keyboard scrolling cancels the glide so inputs never fight it.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);

    // Animated proxy: pushing values through it scrolls to them.
    private static readonly DependencyProperty ProxyProperty = DependencyProperty.RegisterAttached(
        "Proxy", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnWheel;
            sv.PreviewMouseDown += Release;
            sv.PreviewKeyDown += Release;
        }
        else
        {
            sv.PreviewMouseWheel -= OnWheel;
            sv.PreviewMouseDown -= Release;
            sv.PreviewKeyDown -= Release;
        }
    }

    private static void Release(object sender, RoutedEventArgs e)
    {
        // The wheel animation holds its end value (HoldEnd) while the property's base
        // value stays 0. Dropping the animation would snap the effective value back to
        // 0 and the callback would ScrollToVerticalOffset(0) — i.e. every click after
        // a wheel-scroll jumped the page to the top. Park the base value at the live
        // offset first so removing the animation changes nothing.
        var sv = (ScrollViewer)sender;
        sv.SetValue(ProxyProperty, sv.VerticalOffset);
        sv.BeginAnimation(ProxyProperty, null);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (e.Delta == 0 || sv.ScrollableHeight <= 0) return;
        e.Handled = true;
        var target = Math.Clamp(sv.VerticalOffset - e.Delta, 0, sv.ScrollableHeight);
        // From the live offset so chained notches retarget mid-glide; HoldEnd
        // keeps the end value until another input releases it.
        var anim = new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        sv.BeginAnimation(ProxyProperty, anim);
    }
}
