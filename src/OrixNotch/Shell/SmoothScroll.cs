using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// Buttery mouse-wheel scrolling for a vertical ScrollViewer: each wheel notch moves a target and the
/// view eases toward it on the display's frame clock, so fast spins add up instead of stuttering.
/// Clicking, dragging the scrollbar, keys, or code scrolling the view (e.g. ScrollToEnd) take over at
/// once. On for every ScrollViewer through the implicit style in Theme.xaml; set False to opt out.
/// </summary>
public static class SmoothScroll
{
    private const double WheelStep = 0.75;  // DIPs per wheel delta unit (120 per notch → 90 DIPs)
    private const double FollowEase = 0.075; // seconds: how quickly the view catches up with the target

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScroll));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            if (sv.GetValue(StateProperty) is State) return;
            var state = new State(sv);
            sv.SetValue(StateProperty, state);
            sv.PreviewMouseWheel += state.OnWheel;
            sv.PreviewMouseDown += state.OnTakeOver;
            sv.PreviewKeyDown += state.OnTakeOver;
            sv.Unloaded += state.OnTakeOver;
        }
        else if (sv.GetValue(StateProperty) is State state)
        {
            sv.PreviewMouseWheel -= state.OnWheel;
            sv.PreviewMouseDown -= state.OnTakeOver;
            sv.PreviewKeyDown -= state.OnTakeOver;
            sv.Unloaded -= state.OnTakeOver;
            state.Stop();
            sv.ClearValue(StateProperty);
        }
    }

    private sealed class State(ScrollViewer sv)
    {
        private bool _running;
        private TimeSpan _lastFrame;
        private double _position; // where we last put the view
        private double _target;   // where it is heading

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // Item-based (logical) scrolling counts offsets in items, not DIPs: leave it to WPF.
            if (e.Delta == 0 || sv.ScrollableHeight <= 0 || sv.CanContentScroll) return;
            // The preview tunnels outer-first: a scrollable viewer nearer the pointer gets the wheel.
            if (HasInnerScroller(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            if (!_running)
            {
                _running = true;
                _position = _target = sv.VerticalOffset;
                _lastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += OnFrame;
            }
            _target = Math.Clamp(_target - e.Delta * WheelStep, 0, sv.ScrollableHeight);
        }

        private bool HasInnerScroller(DependencyObject? node)
        {
            while (node is not null && node != sv)
            {
                if (node is ScrollViewer { ScrollableHeight: > 0 }) return true;
                node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        public void OnTakeOver(object sender, RoutedEventArgs e) => Stop();

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _lastFrame) return;
            var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
            _lastFrame = now;

            // Something else moved the view (code, scrollbar, content change): let it win.
            if (Math.Abs(sv.VerticalOffset - _position) > 2)
            {
                Stop();
                return;
            }
            _target = Math.Min(_target, sv.ScrollableHeight); // content may have shrunk
            _position += (_target - _position) * (1 - Math.Exp(-dt / FollowEase));
            if (Math.Abs(_target - _position) < 0.3) _position = _target;
            sv.ScrollToVerticalOffset(_position);
            if (_position == _target) Stop();
        }
    }
}
