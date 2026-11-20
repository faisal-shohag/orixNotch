using System.Windows;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// The one chart style: a line over a soft fill that fades to nothing, with a dot on the latest value. Used by Stocks
/// and AI Usage so every trend in the app looks the same. Scales to its own size; values only, no axes.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>),
        typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(LineChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A value drawn as a faint dashed line (a stock's previous close); NaN for none.</summary>
    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(nameof(Baseline), typeof(double),
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

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values is not { Count: >= 2 } || w <= 0 || h <= 0) return;

        var min = values.Min();
        var max = values.Max();
        if (!double.IsNaN(Baseline))
        {
            min = Math.Min(min, Baseline);
            max = Math.Max(max, Baseline);
        }
        var range = Math.Max(1e-9, max - min);
        const double pad = 2; // room for the stroke and the end dot
        double X(int i) => pad + (w - 2 * pad) * i / (values.Count - 1);
        double Y(double v) => pad + (h - 2 * pad) * (1 - (v - min) / range);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(new Point(X(0), Y(values[0])), false, false);
            a.BeginFigure(new Point(X(0), h), true, true);
            a.LineTo(new Point(X(0), Y(values[0])), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                var p = new Point(X(i), Y(values[i]));
                l.LineTo(p, true, true);
                a.LineTo(p, false, false);
            }
            a.LineTo(new Point(X(values.Count - 1), h), false, false);
        }
        line.Freeze();
        area.Freeze();

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
        dc.DrawEllipse(Stroke, null, new Point(X(values.Count - 1), Y(values[^1])), 2.4, 2.4);
    }
}
