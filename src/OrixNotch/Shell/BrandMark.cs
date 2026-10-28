using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
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

/// <summary>Brand colours: the mark's gradient runs pink → orange (from the logo artwork).</summary>
public static class BrandPalette
{
    public static readonly Color Pink = Color.FromRgb(0xFD, 0x03, 0x88);
    public static readonly Color Orange = Color.FromRgb(0xFC, 0x6F, 0x18);
    public static readonly Color MonoDark = Color.FromRgb(0x11, 0x13, 0x18);

    public static LinearGradientBrush CreateGradientBrush()
    {
        var brush = new LinearGradientBrush(Pink, Orange, new Point(0, 0.5), new Point(1, 0.5));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The OrixNotch mark (sparkles + notched ring), drawn from Assets/Brand/mark.svg, the same file the
/// icon and Store-art scripts use (tools/BrandMark.ps1). Keeps its aspect ratio, centred in the control.
/// </summary>
public class BrandMark : Control
{
    private static readonly Geometry Mark = LoadMark();
    private static readonly LinearGradientBrush Gradient = BrandPalette.CreateGradientBrush();
    private static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush DarkBrush = Freeze(new SolidColorBrush(BrandPalette.MonoDark));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(BrandVariant), typeof(BrandMark),
        new FrameworkPropertyMetadata(BrandVariant.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    static BrandMark()
    {
        FocusableProperty.OverrideMetadata(typeof(BrandMark), new FrameworkPropertyMetadata(false));
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
        var variant = EffectiveVariant();
        Draw(dc, RenderSize, variant switch
        {
            BrandVariant.MonoWhite => WhiteBrush,
            BrandVariant.MonoDark => DarkBrush,
            BrandVariant.Outline => null,
            _ => Gradient,
        }, variant == BrandVariant.Outline ? Foreground : null);
    }

    /// <summary>The mark, kept in proportion and centred in <paramref name="size"/>.</summary>
    private static void Draw(DrawingContext dc, Size size, Brush? fill, Brush? outline = null)
    {
        var bounds = Mark.Bounds;
        if (size.Width <= 0 || size.Height <= 0 || bounds.IsEmpty) return;
        var scale = Math.Min(size.Width / bounds.Width, size.Height / bounds.Height);
        dc.PushTransform(new TranslateTransform((size.Width - bounds.Width * scale) / 2, (size.Height - bounds.Height * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(fill ?? Brushes.Transparent, outline is null ? null : new Pen(outline, 1.5 / scale) { LineJoin = PenLineJoin.Round }, Mark);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Both paths of mark.svg (with their translate), moved so the bounds start at 0,0. Even-odd keeps the ring's hole.</summary>
    private static Geometry LoadMark()
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Brand/mark.svg"));
            using (var stream = resource.Stream)
            {
                foreach (var path in XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "path"))
                {
                    var geometry = Geometry.Parse(path.Attribute("d")?.Value ?? "").Clone();
                    var move = Regex.Match(path.Attribute("transform")?.Value ?? "", @"translate\(\s*([-\d.]+)[\s,]+([-\d.]+)\s*\)");
                    if (move.Success)
                        geometry.Transform = new TranslateTransform(
                            double.Parse(move.Groups[1].Value, CultureInfo.InvariantCulture),
                            double.Parse(move.Groups[2].Value, CultureInfo.InvariantCulture));
                    group.Children.Add(geometry);
                }
            }
            var bounds = group.Bounds;
            group.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
        }
        catch (Exception ex)
        {
            App.Log(ex); // missing or broken asset: draw nothing rather than crash the About page
        }
        group.Freeze();
        return group;
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
