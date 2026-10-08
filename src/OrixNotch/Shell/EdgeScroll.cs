using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// Smooth horizontal scrolling for card strips: resting the pointer near the left or right edge
/// scrolls that way (faster the closer it is to the edge), and the mouse wheel glides instead of
/// jumping. Runs on the display's frame clock only while something is moving.
/// <code>&lt;ScrollViewer shell:EdgeScroll.IsEnabled="True" … /&gt;</code>
/// </summary>
public static class EdgeScroll
{
    private const double ZoneWidth = 56;     // DIPs from each edge that trigger auto-scroll
    private const double MaxSpeed = 900;     // DIPs per second at the very edge
    private const double SpeedEase = 0.12;   // seconds: ramp for edge speed
    private const double FollowEase = 0.09;  // seconds: how quickly the view follows its target
    private const double WheelStep = 0.8;    // DIPs per wheel delta unit

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EdgeScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(EdgeScroll));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            var state = new State(sv);
            sv.SetValue(StateProperty, state);
            sv.PreviewMouseWheel += state.OnWheel;
            sv.MouseMove += state.OnMouseMove;
            sv.MouseLeave += state.OnMouseLeave;
            sv.Unloaded += state.OnUnloaded;
        }
        else if (sv.GetValue(StateProperty) is State state)
        {
            sv.PreviewMouseWheel -= state.OnWheel;
            sv.MouseMove -= state.OnMouseMove;
            sv.MouseLeave -= state.OnMouseLeave;
            sv.Unloaded -= state.OnUnloaded;
            state.Stop();
            sv.ClearValue(StateProperty);
        }
    }

    private sealed class State(ScrollViewer sv)
    {
        private bool _running;
        private TimeSpan _lastFrame;
        private double _position;    // where the view is (tracked here; ScrollViewer applies offsets lazily)
        private double _target;      // where it is heading
        private double _edgeSpeed;   // requested by the pointer position, DIPs/s (negative = left)
        private double _speed;       // eased toward _edgeSpeed

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (sv.ScrollableWidth <= 0) return;
            Start();
            _target = Math.Clamp(_target - e.Delta * WheelStep, 0, sv.ScrollableWidth);
            e.Handled = true;
        }

        public void OnMouseMove(object sender, MouseEventArgs e)
        {
            var x = e.GetPosition(sv).X;
            var width = sv.ActualWidth;
            var zone = Math.Min(ZoneWidth, width * 0.15);
            double speed = 0;
            if (x > width - zone && sv.HorizontalOffset < sv.ScrollableWidth)
                speed = MaxSpeed * Math.Pow((x - (width - zone)) / zone, 2);
            else if (x < zone && sv.HorizontalOffset > 0)
                speed = -MaxSpeed * Math.Pow((zone - x) / zone, 2);
            _edgeSpeed = speed;
            if (speed != 0) Start();
        }

        public void OnMouseLeave(object sender, MouseEventArgs e) => _edgeSpeed = 0;

        public void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

        private void Start()
        {
            if (_running) return;
            _running = true;
            _position = _target = sv.HorizontalOffset;
            _speed = 0;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _edgeSpeed = _speed = 0;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _lastFrame) return;
            var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
            _lastFrame = now;

            var max = sv.ScrollableWidth;
            // Reached the end the pointer is pushing toward: nothing more to do until it moves again.
            if ((_edgeSpeed > 0 && _target >= max) || (_edgeSpeed < 0 && _target <= 0)) _edgeSpeed = 0;
            _speed += (_edgeSpeed - _speed) * (1 - Math.Exp(-dt / SpeedEase));
            if (Math.Abs(_speed) > 0.5) _target = Math.Clamp(_target + _speed * dt, 0, max);
            _position += (_target - _position) * (1 - Math.Exp(-dt / FollowEase));
            if (Math.Abs(_target - _position) < 0.3) _position = _target;
            sv.ScrollToHorizontalOffset(_position);

            if (_edgeSpeed == 0 && Math.Abs(_speed) < 0.5 && _position == _target) Stop();
        }
    }
}
