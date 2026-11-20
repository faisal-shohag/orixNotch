using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace OrixNotch.Shell;

/// <summary>
/// The app's progress style: a thin track, a coloured fill, and at the fill's end a glowing head, either a pill with an
/// icon and the value (⚡ 34%) or, compact, a glowing dot; an optional label sits at the far end (~15m).
/// <para>The glow is the tone's brush under a radial opacity mask, not a BlurEffect: the notch is a layered window where
/// every effect pass is costly, and this costs nothing extra. Colours are resource keys, so themes retint it live.</para>
/// </summary>
public sealed class GlowBar : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(GlowBar),
        new PropertyMetadata(0.0, (d, e) => ((GlowBar)d).AnimateTo((double)e.NewValue)));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(GlowBar),
        new PropertyMetadata(0.0, (d, _) => ((GlowBar)d).Arrange()));

    private readonly Grid _area = new() { ClipToBounds = false };
    private readonly Border _track = new();
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Rectangle _glow = new() { IsHitTestVisible = false };
    private readonly Canvas _glowLayer = new() { IsHitTestVisible = false }; // a Canvas doesn't clip the glow to the bar's slot
    private readonly Border _head = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform _headShift = new(); // moves the head without a layout pass
    private readonly StackPanel _headContent = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly HeroIcon _headIcon = new() { Width = 11, Height = 11, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _headText = new() { FontSize = 11.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _end = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private string _tone = "AccentBrush";
    private string? _headLabel;
    private AppIcon? _icon;
    private double _barHeight = 6;

    // Soft falloff from the head's centre: shapes the glow out of the tone brush.
    private static readonly RadialGradientBrush GlowMask = Freeze(new RadialGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(150, 0, 0, 0), 0),
        new(Color.FromArgb(60, 0, 0, 0), 0.45),
        new(Color.FromArgb(0, 0, 0, 0), 1),
    }));

    public GlowBar()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        _glow.OpacityMask = GlowMask;
        _headContent.Children.Add(_headIcon);
        _headContent.Children.Add(_headText);
        _head.Child = _headContent;
        _head.RenderTransform = _headShift;
        _glowLayer.Children.Add(_glow);
        _area.Children.Add(_glowLayer);
        _area.Children.Add(_track);
        _area.Children.Add(_fill);
        _area.Children.Add(_head);
        Children.Add(_area);
        Grid.SetColumn(_end, 1);
        _end.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        Children.Add(_end);
        _end.Visibility = Visibility.Collapsed;
        _area.SizeChanged += (_, _) => Arrange();
        ApplyTone();
        ApplyHead();
    }

    /// <summary>0–1. Glides to a new value (instant with Windows' animation effects off).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1));
    }

    /// <summary>The head's label ("34%"). Null: a compact glowing dot instead of the pill.</summary>
    public string? HeadText
    {
        get => _headLabel;
        set
        {
            if (_headLabel == value) return; // set every second by the System tool: only a real change redraws
            var sizeChanges = (_headLabel is null) != (value is null);
            _headLabel = value;
            if (sizeChanges) ApplyHead();
            else
            {
                _headText.Text = value;
                Arrange();
            }
        }
    }

    /// <summary>Icon in the head pill, before the value.</summary>
    public AppIcon? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            ApplyHead();
        }
    }

    /// <summary>Label after the bar ("~15m", "2 GB free"); hidden when null or empty.</summary>
    public string? EndText
    {
        get => _end.Text;
        set
        {
            _end.Text = value ?? "";
            _end.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Resource key of the colour: "AccentBrush" (default), "GoodBrush", "BadBrush", …</summary>
    public string Tone
    {
        get => _tone;
        set
        {
            if (_tone == value) return;
            _tone = value;
            ApplyTone();
        }
    }

    /// <summary>Track thickness in DIPs (the head and glow scale with it).</summary>
    public double BarHeight
    {
        get => _barHeight;
        set
        {
            _barHeight = value;
            ApplyHead();
        }
    }

    /// <summary>The usual warning colours: high is bad (memory, disk, usage), or low is bad (battery).</summary>
    public static string ToneFor(double fraction, bool lowIsBad = false) =>
        lowIsBad ? fraction < 0.2 ? "BadBrush" : "AccentBrush" : fraction >= 0.85 ? "BadBrush" : "AccentBrush";

    private void ApplyTone()
    {
        _fill.SetResourceReference(Border.BackgroundProperty, _tone);
        _glow.SetResourceReference(Shape.FillProperty, _tone);
        _head.SetResourceReference(Border.BackgroundProperty, _tone);
    }

    private void ApplyHead()
    {
        var pill = _headLabel is not null;
        var h = _barHeight;
        _track.Height = _fill.Height = h;
        _track.CornerRadius = _fill.CornerRadius = new CornerRadius(h / 2);
        _track.VerticalAlignment = _fill.VerticalAlignment = VerticalAlignment.Center;
        if (pill)
        {
            _headText.Text = _headLabel;
            _headIcon.Visibility = _icon is null ? Visibility.Collapsed : Visibility.Visible;
            if (_icon is { } kind) _headIcon.Kind = kind;
            _headIcon.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
            _headText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
            _head.Height = Math.Max(18, h + 12);
            _head.Padding = new Thickness(7, 0, 8, 0);
            _head.CornerRadius = new CornerRadius(_head.Height / 2);
            _head.Width = double.NaN;
            _headContent.Visibility = Visibility.Visible;
            MinHeight = _head.Height;
        }
        else
        {
            var dot = h + 4;
            _head.Width = _head.Height = dot;
            _head.Padding = new Thickness(0);
            _head.CornerRadius = new CornerRadius(dot / 2);
            _headContent.Visibility = Visibility.Collapsed;
            MinHeight = dot;
        }
        Arrange();
    }

    private void AnimateTo(double value)
    {
        if (Motion.Reduced || !IsLoaded)
        {
            BeginAnimation(ShownProperty, null);
            SetValue(ShownProperty, value);
            return;
        }
        // Long enough to carry over to the next once-a-second sample: the bar moves steadily instead of in steps.
        BeginAnimation(ShownProperty, new DoubleAnimation(value, Motion.Scale(650)) { EasingFunction = Motion.Out });
    }

    /// <summary>Places fill, head and glow for the shown value; the head stays inside the track at both ends.</summary>
    private void Arrange()
    {
        var width = _area.ActualWidth;
        if (width <= 0) return;
        var shown = (double)GetValue(ShownProperty);
        // The head's own width (positioned by a transform, so no margin creeps into its measured size).
        _head.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var headW = _head.DesiredSize.Width;
        var x = shown * width;
        var left = Math.Clamp(x - headW / 2, 0, Math.Max(0, width - headW));
        var centre = left + headW / 2;
        _headShift.X = left;
        // The fill runs under the head's centre, so the head reads as the fill's leading edge.
        _fill.Width = Math.Max(0, shown <= 0 ? 0 : centre);
        _head.Visibility = shown <= 0 && _headLabel is null ? Visibility.Collapsed : Visibility.Visible;
        // A round-ish bloom around the head: wide enough to glow past it, not so wide it reads as more fill.
        var glowH = Math.Max(_head.Height, _barHeight) * 2.6;
        var glowW = Math.Max(glowH, headW * 1.3);
        _glow.Width = glowW;
        _glow.Height = glowH;
        Canvas.SetLeft(_glow, centre - glowW / 2);
        Canvas.SetTop(_glow, (_area.ActualHeight - glowH) / 2);
        _glow.Visibility = _head.Visibility;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
