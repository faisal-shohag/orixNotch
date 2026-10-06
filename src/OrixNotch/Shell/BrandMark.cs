using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>Which artwork to render. <see cref="Auto"/> follows the theme: gradient on dark, near-black mono on light.</summary>
public enum BrandVariant
{
    Auto,
    Gradient,
    MonoWhite,
    MonoDark,
    Outline,
}

/// <summary>
/// Vector recreation of the OrixNotch notch-pill mark (blue → purple → pink gloss).
/// ViewBox is 208x64; the control scales to its Width/Height.
/// </summary>
public static class BrandPalette
{
    public static readonly Color Blue = Color.FromRgb(0x2E, 0x9C, 0xFF);
    public static readonly Color Indigo = Color.FromRgb(0x4F, 0x46, 0xFF);
    public static readonly Color Violet = Color.FromRgb(0x7C, 0x3C, 0xFF);
    public static readonly Color Pink = Color.FromRgb(0xD9, 0x46, 0xEF);
    public static readonly Color MonoDark = Color.FromRgb(0x11, 0x13, 0x18);

    public static LinearGradientBrush CreateGradientBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Blue, 0.0),
                new GradientStop(Indigo, 0.38),
                new GradientStop(Violet, 0.68),
                new GradientStop(Pink, 1.0),
            },
        };
        brush.Freeze();
        return brush;
    }
}

public class BrandMark : Control
{
    private const double ViewBoxW = 208;
    private const double ViewBoxH = 64;

    private static readonly LinearGradientBrush Gradient = BrandPalette.CreateGradientBrush();
    private static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush DarkBrush = Freeze(new SolidColorBrush(BrandPalette.MonoDark));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(BrandVariant), typeof(BrandMark),
        new FrameworkPropertyMetadata(BrandVariant.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    static BrandMark()
    {
        FocusableProperty.OverrideMetadata(typeof(BrandMark), new FrameworkPropertyMetadata(false));
        ThemeService.Changed += () =>
        {
            // Auto instances need a repaint when the scheme flips light/dark.
            // No instance tracking; WPF re-renders on resource change anyway for most cases.
        };
    }

    public BrandVariant Variant
    {
        get => (BrandVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    private BrandVariant EffectiveVariant()
    {
        if (Variant != BrandVariant.Auto) return Variant;
        try
        {
            return ThemeService.CurrentScheme.IsLight ? BrandVariant.MonoDark : BrandVariant.Gradient;
        }
        catch
        {
            return BrandVariant.Gradient;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var effective = EffectiveVariant();
        var geometry = BuildMarkGeometry();

        dc.PushTransform(new ScaleTransform(ActualWidth / ViewBoxW, ActualHeight / ViewBoxH));
        try
        {
            switch (effective)
            {
                case BrandVariant.MonoWhite:
                    dc.DrawGeometry(WhiteBrush, null, geometry);
                    break;
                case BrandVariant.MonoDark:
                    dc.DrawGeometry(DarkBrush, null, geometry);
                    break;
                case BrandVariant.Outline:
                    var pen = new Pen(Foreground, 3.5) { LineJoin = PenLineJoin.Round };
                    dc.DrawGeometry(Brushes.Transparent, pen, geometry);
                    // Notch inner line for the outline style.
                    dc.DrawGeometry(Brushes.Transparent, new Pen(Foreground, 3) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, BuildNotchStroke());
                    break;
                default:
                    dc.DrawGeometry(Gradient, null, geometry);
                    // Glass gloss: soft white sheen across the top, clipped to the mark.
                    dc.PushClip(geometry);
                    var gloss = new LinearGradientBrush(
                        Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF),
                        Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                        new Point(0, 0), new Point(0, 0.55));
                    dc.DrawRectangle(gloss, null, new Rect(0, 0, ViewBoxW, ViewBoxH * 0.55));
                    dc.Pop();
                    // Notch inner highlight (thin, matches the glossy dip in the logo).
                    dc.DrawGeometry(Brushes.Transparent,
                        new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1.25)
                        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                        BuildNotchStroke());
                    break;
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    internal static Geometry BuildMarkGeometry()
    {
        // Slimmer capsule: 120 wide centered in the 208 viewBox (was 152).
        const double pillW = 120;
        const double pillX = (ViewBoxW - pillW) / 2; // 44
        var outer = new RectangleGeometry(new Rect(pillX, 0, pillW, ViewBoxH), ViewBoxH / 2, ViewBoxH / 2);
        var notch = BuildNotchGeometry();
        var combined = new CombinedGeometry(GeometryCombineMode.Exclude, outer, notch);
        if (combined.CanFreeze) combined.Freeze();
        return combined;
    }

    private static Geometry BuildNotchGeometry()
    {
        // Shallow top-center dip: 50 wide, ~20 tall, flat top (cut through),
        // all corners rounded (top r=6, bottom r=10).
        const double w = 50;
        const double x = (ViewBoxW - w) / 2; // 79
        const double bottom = 20;
        var fig = new PathFigure { StartPoint = new Point(x + 6, -2), IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(x + w - 6, -2), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x + w, -2), new Point(x + w, 4), true));
        fig.Segments.Add(new LineSegment(new Point(x + w, 10), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x + w, bottom), new Point(x + w - 10, bottom), true));
        fig.Segments.Add(new LineSegment(new Point(x + 10, bottom), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x, bottom), new Point(x, 10), true));
        fig.Segments.Add(new LineSegment(new Point(x, 4), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x, -2), new Point(x + 6, -2), true));
        var geo = new PathGeometry([fig]);
        if (geo.CanFreeze) geo.Freeze();
        return geo;
    }

    private static Geometry BuildNotchStroke()
    {
        const double w = 50;
        const double x = (ViewBoxW - w) / 2;
        var fig = new PathFigure { StartPoint = new Point(x + 7, 0) };
        fig.Segments.Add(new LineSegment(new Point(x + 7, 9), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x + 7, 18), new Point(x + 16, 18), true));
        fig.Segments.Add(new LineSegment(new Point(x + w - 16, 18), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x + w - 7, 18), new Point(x + w - 7, 9), true));
        fig.Segments.Add(new LineSegment(new Point(x + w - 7, 0), true));
        var geo = new PathGeometry([fig]);
        if (geo.CanFreeze) geo.Freeze();
        return geo;
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
