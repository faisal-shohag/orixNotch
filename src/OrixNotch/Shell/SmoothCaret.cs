using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OrixNotch.Shell;

/// <summary>
/// Replaces the stock 1 px, hard on/off text caret with a 2 px accent caret that glides to its new
/// position as you type or move, stays solid while you're typing, and fades gently when idle.
///
/// Used by the TextBox template in Theme.xaml: the template provides a Canvas "CaretLayer" holding a
/// Rectangle "Caret" over the text, hides the native caret (CaretBrush transparent) and turns this on.
/// Animations are frame-capped because the notch is a transparent window that is costly to redraw.
/// </summary>
public static class SmoothCaret
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothCaret), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(CaretState), typeof(SmoothCaret));

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb || e.NewValue is not true) return;
        tb.Loaded += (_, _) =>
        {
            if (tb.GetValue(StateProperty) is null) tb.SetValue(StateProperty, new CaretState(tb));
        };
    }

    private sealed class CaretState
    {
        private const double GlideMs = 90;
        private readonly TextBox _tb;
        private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(550) };
        private readonly TranslateTransform _shift = new();
        private Canvas? _layer;
        private Rectangle? _caret;
        private bool _placed;
        private bool _updateQueued;

        public CaretState(TextBox tb)
        {
            _tb = tb;
            tb.ApplyTemplate();
            Attach();
            tb.GotKeyboardFocus += (_, _) => { _placed = false; Queue(); };
            tb.LostKeyboardFocus += (_, _) => Hide();
            tb.SelectionChanged += (_, _) => Queue();
            tb.TextChanged += (_, _) => Queue();
            tb.SizeChanged += (_, _) => Queue();
            tb.IsVisibleChanged += (_, _) => Queue();
            tb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Queue()));
            _idle.Tick += (_, _) =>
            {
                _idle.Stop();
                Blink();
            };
        }

        private void Attach()
        {
            _layer = _tb.Template?.FindName("CaretLayer", _tb) as Canvas;
            _caret = _tb.Template?.FindName("Caret", _tb) as Rectangle;
            if (_caret is null) return;
            _caret.RenderTransform = _shift;
            _caret.Opacity = 0;
        }

        /// <summary>Coalesce bursts of events (each keystroke fires several) into one update after layout.</summary>
        private void Queue()
        {
            if (_updateQueued) return;
            _updateQueued = true;
            _tb.Dispatcher.BeginInvoke(() =>
            {
                _updateQueued = false;
                Update();
            }, DispatcherPriority.Render);
        }

        private void Update()
        {
            if (_caret is null || _layer is null) Attach();
            if (_caret is null || _layer is null) return;
            if (!_tb.IsKeyboardFocused || !_tb.IsVisible || _tb.IsReadOnly || _tb.SelectionLength > 0)
            {
                Hide();
                return;
            }

            var rect = _tb.GetRectFromCharacterIndex(_tb.CaretIndex);
            if (rect.IsEmpty || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y)) return;
            var origin = _tb.TranslatePoint(rect.TopLeft, _layer);
            // The character rect is in unscrolled text coordinates but the overlay
            // canvas does not scroll with the content: compensate the scroll offset
            // so the caret stays glued to its glyph in scrolled boxes.
            var height = Math.Max(10, rect.Height);
            var x = Math.Round(origin.X - _tb.HorizontalOffset);
            var y = Math.Round(origin.Y - _tb.VerticalOffset);

            _caret.Height = height;
            if (!_placed)
            {
                // First placement after focusing: appear in place, no glide from a stale position.
                _shift.BeginAnimation(TranslateTransform.XProperty, null);
                _shift.BeginAnimation(TranslateTransform.YProperty, null);
                _shift.X = x;
                _shift.Y = y;
                _placed = true;
            }
            else
            {
                Glide(TranslateTransform.XProperty, x);
                Glide(TranslateTransform.YProperty, y);
            }
            Solid();
        }

        private void Glide(DependencyProperty property, double to)
        {
            if (Math.Abs((double)_shift.GetValue(property) - to) < 0.5) return;
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(GlideMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Timeline.SetDesiredFrameRate(anim, 60);
            _shift.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>Fully visible while typing/moving; blinking resumes after a short pause.</summary>
        private void Solid()
        {
            _caret!.BeginAnimation(UIElement.OpacityProperty, null);
            _caret.Opacity = 1;
            _idle.Stop();
            _idle.Start();
        }

        /// <summary>Soft blink: hold, fade out, hold, fade in — ~1.1 s cycle like the system caret.</summary>
        private void Blink()
        {
            if (_caret is null || !_tb.IsKeyboardFocused) return;
            var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
            blink.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), new SineEase()));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1060)), new SineEase()));
            // ~20 fps is smooth enough for a fade and keeps the transparent notch's redraws cheap.
            Timeline.SetDesiredFrameRate(blink, 20);
            _caret.BeginAnimation(UIElement.OpacityProperty, blink);
        }

        private void Hide()
        {
            _idle.Stop();
            if (_caret is null) return;
            _caret.BeginAnimation(UIElement.OpacityProperty, null);
            _caret.Opacity = 0;
            _placed = false;
        }
    }
}
