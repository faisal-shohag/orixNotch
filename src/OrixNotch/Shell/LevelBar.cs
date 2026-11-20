using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// A rounded level bar you drag, click or scroll (Controls' volumes), in the style of Control Center: a soft track
/// with an accent fill, no thumb. An optional icon sits inside the bar's start; it turns white once the fill
/// reaches it.
/// </summary>
public sealed class LevelBar : Grid
{
    private readonly Border _track = new();
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly HeroIcon? _icon;
    private double _value;

    public event Action<double>? ValueChanged;

    public LevelBar(double height, AppIcon? icon = null)
    {
        Height = height;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent; // the whole bar takes the mouse, not only the painted parts
        _track.CornerRadius = _fill.CornerRadius = new CornerRadius(height / 2);
        _track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        Children.Add(_track);
        Children.Add(_fill);
        if (icon is { } kind)
        {
            var size = Math.Round(height * 0.5);
            _icon = new HeroIcon { Kind = kind, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(height * 0.32, 0, 0, 0), IsHitTestVisible = false };
            Children.Add(_icon);
        }
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) =>
        {
            CaptureMouse();
            SetFromPointer(e);
            e.Handled = true;
        };
        MouseMove += (_, e) => { if (IsMouseCaptured) SetFromPointer(e); };
        MouseLeftButtonUp += (_, _) => ReleaseMouseCapture();
        MouseWheel += (_, e) =>
        {
            Set(_value + Math.Sign(e.Delta) * 0.05, notify: true);
            e.Handled = true;
        };
    }

    /// <summary>0–1. Setting it from code doesn't raise <see cref="ValueChanged"/>.</summary>
    public double Value
    {
        get => _value;
        set => Set(value, notify: false);
    }

    /// <summary>The user is dragging it: outside updates should wait.</summary>
    public bool Dragging => IsMouseCaptured;

    private void SetFromPointer(MouseEventArgs e) => Set(e.GetPosition(this).X / Math.Max(1, ActualWidth), notify: true);

    private void Set(double value, bool notify)
    {
        value = Math.Clamp(value, 0, 1);
        if (Math.Abs(value - _value) < 0.0005 && !notify) return;
        _value = value;
        Layout();
        if (notify) ValueChanged?.Invoke(value);
    }

    private void Layout()
    {
        // Never thinner than the bar is tall, so a low level still reads as a rounded dot, not a sliver.
        _fill.Width = _value <= 0 ? 0 : Math.Max(Height, ActualWidth * _value);
        if (_icon is not null)
            _icon.SetResourceReference(Control.ForegroundProperty, _fill.Width >= Height ? "OnAccentBrush" : "SubTextBrush");
    }
}
