using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrixNotch.Shell;

/// <summary>
/// More tabs than the notch is wide: the strip scrolls sideways with the mouse wheel or by resting the
/// pointer near an end, the ends that hide tabs fade out, and the selected tab is scrolled into view.
/// </summary>
public partial class NotchWindow
{
    private const double TabFade = 28;
    private const double EdgeZone = 44;      // pointer this close to an end scrolls that way
    private const double EdgeMaxSpeed = 520; // px/s right at the end

    private double _edgeSpeed;               // px/s, negative = toward the start
    private TimeSpan _edgeLast;
    private bool _edgeRunning;

    // Animated proxy: pushing values through it scrolls the strip to them.
    private static readonly DependencyProperty TabScrollProperty = DependencyProperty.Register(
        "TabScroll", typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0.0, (d, e) => ((NotchWindow)d).TabScroller.ScrollToHorizontalOffset((double)e.NewValue)));

    private void WireTabScroll()
    {
        TabScroller.PreviewMouseWheel += OnTabWheel;
        TabScroller.PreviewMouseDown += (_, _) => StopTabScroll();
        TabScroller.ScrollChanged += (_, _) => UpdateTabFade();
        TabScroller.PreviewMouseMove += OnTabHover;
        TabScroller.MouseLeave += (_, _) => StopEdgeScroll();
        TabBar.IsVisibleChanged += (_, _) => StopEdgeScroll();
    }

    /// <summary>Near an end with more tabs past it: glide that way, faster the closer the pointer.</summary>
    private void OnTabHover(object sender, MouseEventArgs e)
    {
        var x = e.GetPosition(TabScroller).X;
        var w = TabScroller.ActualWidth;
        var speed = 0.0;
        if (_dragTab is null && TabScroller.ScrollableWidth > 0)
        {
            if (x < EdgeZone && TabScroller.HorizontalOffset > 0) speed = -EdgeMaxSpeed * Ease(1 - x / EdgeZone);
            else if (x > w - EdgeZone && TabScroller.HorizontalOffset < TabScroller.ScrollableWidth)
                speed = EdgeMaxSpeed * Ease(1 - (w - x) / EdgeZone);
        }
        if (speed == 0)
        {
            StopEdgeScroll();
            return;
        }
        _edgeSpeed = speed;
        if (_edgeRunning) return;
        StopTabScroll();
        _edgeRunning = true;
        _edgeLast = TimeSpan.Zero;
        CompositionTarget.Rendering += OnEdgeFrame;

        static double Ease(double t) => Math.Clamp(t, 0, 1) * (0.35 + 0.65 * Math.Clamp(t, 0, 1));
    }

    private void OnEdgeFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_edgeLast == TimeSpan.Zero || now == _edgeLast)
        {
            _edgeLast = now;
            return;
        }
        var dt = Math.Min((now - _edgeLast).TotalSeconds, 0.05);
        _edgeLast = now;
        var target = Math.Clamp(TabScroller.HorizontalOffset + _edgeSpeed * dt, 0, TabScroller.ScrollableWidth);
        TabScroller.ScrollToHorizontalOffset(target);
        if (target <= 0 || target >= TabScroller.ScrollableWidth) StopEdgeScroll();
    }

    private void StopEdgeScroll()
    {
        if (!_edgeRunning) return;
        _edgeRunning = false;
        CompositionTarget.Rendering -= OnEdgeFrame;
        SetValue(TabScrollProperty, TabScroller.HorizontalOffset); // later glides start from here
    }

    private void OnTabWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || TabScroller.ScrollableWidth <= 0) return;
        e.Handled = true;
        StopEdgeScroll();
        GlideTabsTo(TabScroller.HorizontalOffset - e.Delta, animate: true);
    }

    /// <summary>Scrolls just enough that a tab at <paramref name="x"/> (strip coordinates) clears the faded ends.</summary>
    private void ScrollTabIntoView(double x, double width, bool animate)
    {
        if (TabScroller.ScrollableWidth <= 0) return;
        var offset = TabScroller.HorizontalOffset;
        var view = TabScroller.ViewportWidth;
        double target;
        if (x - TabFade < offset) target = x - TabFade;
        else if (x + width + TabFade > offset + view) target = x + width + TabFade - view;
        else return;
        GlideTabsTo(target, animate);
    }

    private void GlideTabsTo(double target, bool animate)
    {
        StopEdgeScroll();
        target = Math.Clamp(target, 0, TabScroller.ScrollableWidth);
        if (!animate)
        {
            StopTabScroll();
            TabScroller.ScrollToHorizontalOffset(target);
            SetValue(TabScrollProperty, target);
            return;
        }
        // From the live offset so chained wheel notches retarget mid-glide.
        BeginAnimation(TabScrollProperty, new DoubleAnimation(TabScroller.HorizontalOffset, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Parks the proxy at the live offset before dropping the glide, so nothing snaps back.</summary>
    private void StopTabScroll()
    {
        SetValue(TabScrollProperty, TabScroller.HorizontalOffset);
        BeginAnimation(TabScrollProperty, null);
    }

    /// <summary>Fades whichever end still has tabs past it; none when everything fits.</summary>
    private void UpdateTabFade()
    {
        var w = TabScroller.ActualWidth;
        if (TabScroller.ScrollableWidth <= 0.5 || w <= 2 * TabFade)
        {
            TabScroller.OpacityMask = null;
            return;
        }
        var left = TabScroller.HorizontalOffset > 0.5;
        var right = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;
        var f = TabFade / w;
        TabScroller.OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(left ? Colors.Transparent : Colors.Black, 0),
            new(Colors.Black, f),
            new(Colors.Black, 1 - f),
            new(right ? Colors.Transparent : Colors.Black, 1),
        }, new Point(0, 0), new Point(1, 0));
    }
}
