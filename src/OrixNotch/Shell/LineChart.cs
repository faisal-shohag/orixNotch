using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrixNotch.Shell;

/// <summary>
/// The one chart style: a line over a soft fill that fades to nothing, with a dot on the latest value. Used by Stocks
/// and AI Usage so every trend in the app looks the same. Scales to its own size; values only, no axes.
/// <para>Live mode (<see cref="Capacity"/> &gt; 0, the System tool): a fixed number of slots filled from the right, the line
/// drawn as a smooth curve, and each new sample <em>glides</em> in: the line scrolls left by one slot over
/// <see cref="Glide"/> and an auto scale eases to its new top, instead of everything jumping once a second.</para>
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>),
        typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LineChart)d).OnValuesChanged()));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(LineChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A value drawn as a faint dashed line (a stock's previous close); NaN for none.</summary>
    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(nameof(Baseline), typeof(double),
        typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A fixed top of the scale (100 for a percentage); NaN scales to the values.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double),
        typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Live mode: how many samples the width holds (0: the values fill the width, the classic chart).</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int),
        typeof(LineChart), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Live mode: how long a new sample takes to scroll in (match the sampling interval).</summary>
    public Duration Glide { get; set; } = new(TimeSpan.FromSeconds(1));

    // 1 → 0 while the newest sample scrolls in; the auto scale's top, eased.
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double),
        typeof(LineChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ScaleTopProperty = DependencyProperty.Register("ScaleTop", typeof(double),
        typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Baseline
    {
        get => (double)GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    private void OnValuesChanged()
    {
        if (Capacity <= 0 || Values is not { Count: > 0 } values) return;
        var reduced = Motion.Reduced;
        // Scroll: start one slot to the right and slide into place over the sampling interval.
        if (values.Count >= 2 && !reduced)
            BeginAnimation(PhaseProperty, new DoubleAnimation(1, 0, Glide));
        // Auto scale: ease the top to the new peak (plus headroom) rather than snapping.
        if (double.IsNaN(Maximum))
        {
            var top = Math.Max(1e-9, values.Max() * 1.2);
            var current = (double)GetValue(ScaleTopProperty);
            if (reduced || double.IsNaN(current)) SetValue(ScaleTopProperty, top);
            else BeginAnimation(ScaleTopProperty, new DoubleAnimation(current, top, Glide) { EasingFunction = Motion.Out });
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values is not { Count: >= 2 } || w <= 0 || h <= 0) return;
        var live = Capacity > 0;

        double min, max;
        if (live)
        {
            // Live charts start at zero: a quiet network is a flat line at the bottom, not a magnified wobble.
            min = 0;
            max = !double.IsNaN(Maximum) ? Maximum : (double)GetValue(ScaleTopProperty) is var top && !double.IsNaN(top) ? top : values.Max() * 1.2;
            max = Math.Max(max, 1e-9);
        }
        else
        {
            min = values.Min();
            max = values.Max();
            if (!double.IsNaN(Baseline))
            {
                min = Math.Min(min, Baseline);
                max = Math.Max(max, Baseline);
            }
            if (!double.IsNaN(Maximum)) (min, max) = (0, Math.Max(Maximum, max)); // a fixed 0…Maximum scale: 1% sits at the bottom
        }
        var range = Math.Max(1e-9, max - min);
        const double pad = 2; // room for the stroke and the end dot
        var n = values.Count;
        var step = live ? (w - 2 * pad) / Math.Max(1, Capacity - 1) : (w - 2 * pad) / (n - 1);
        var shift = live ? (double)GetValue(PhaseProperty) * step : 0;
        double X(int i) => live ? w - pad - (n - 1 - i) * step + shift : pad + step * i;
        double Y(double v) => pad + (h - 2 * pad) * (1 - Math.Clamp((v - min) / range, 0, 1.05));

        var points = new Point[n];
        for (var i = 0; i < n; i++) points[i] = new Point(X(i), Y(values[i]));
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(points[0], false, false);
            a.BeginFigure(new Point(points[0].X, h), true, true);
            a.LineTo(points[0], false, false);
            for (var i = 1; i < n; i++)
            {
                if (live)
                {
                    // Catmull-Rom through the samples as cubic Béziers: a smooth line, still passing every point.
                    var p0 = points[Math.Max(0, i - 2)];
                    var p1 = points[i - 1];
                    var p2 = points[i];
                    var p3 = points[Math.Min(n - 1, i + 1)];
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, pad, h - pad));
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, pad, h - pad));
                    l.BezierTo(c1, c2, p2, true, true);
                    a.BezierTo(c1, c2, p2, false, false);
                }
                else
                {
                    l.LineTo(points[i], true, true);
                    a.LineTo(points[i], false, false);
                }
            }
            a.LineTo(new Point(points[n - 1].X, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        if (live) dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h))); // the slot scrolling in starts past the edge
        var color = Stroke is SolidColorBrush solid ? solid.Color : ((Stroke as GradientBrush)?.GradientStops.LastOrDefault()?.Color ?? Colors.Gray);
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        if (!double.IsNaN(Baseline))
        {
            var dash = new Pen(new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)), 1) { DashStyle = new DashStyle([2, 3], 0) };
            dash.Freeze();
            dc.DrawLine(dash, new Point(pad, Y(Baseline)), new Point(w - pad, Y(Baseline)));
        }

        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        dc.DrawEllipse(Stroke, null, points[n - 1], 2.4, 2.4);
        if (live) dc.Pop();
    }
}
