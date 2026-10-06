using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// Solid vs outline rendering. <see cref="Auto"/> follows the app rule:
/// small/chrome glyphs are solid, large/decorative glyphs are outline.
/// </summary>
public enum HeroVariant
{
    Auto,
    Solid,
    Outline,
}

/// <summary>
/// App glyph drawn from <see cref="HeroIconsData"/> (Heroicons, 24x24 box) or
/// <see cref="HeroCustomIcons"/> (user-supplied artwork, own viewBox), scaled to
/// the control's <see cref="FrameworkElement.Width"/>/<see cref="FrameworkElement.Height"/>.
/// Solid glyphs fill with <see cref="Control.Foreground"/>; outline glyphs
/// stroke with it (round caps/joins). Custom icons always render in their
/// native style and ignore the auto-outline size rule.
/// </summary>
public class HeroIcon : Control
{
    private const double ViewBox = 24;
    private const double OutlineStroke = 1.5;

    /// <summary>
    /// Auto switches to outline at or above this rendered size, so tab-bar and
    /// button glyphs stay solid while big decorative glyphs go outline.
    /// </summary>
    private const double OutlineAtOrAbove = 21;

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(AppIcon), typeof(HeroIcon),
        new FrameworkPropertyMetadata(AppIcon.Grid, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(HeroVariant), typeof(HeroIcon),
        new FrameworkPropertyMetadata(HeroVariant.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    static HeroIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(HeroIcon), new FrameworkPropertyMetadata(false));
    }

    public AppIcon Kind
    {
        get => (AppIcon)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public HeroVariant Variant
    {
        get => (HeroVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Foreground isn't AffectsRender for custom OnRender; repaint so
        // DynamicResource theme changes (TextBrush/SubTextBrush/...) apply live.
        if (e.Property == ForegroundProperty) InvalidateVisual();
    }

    private bool UseOutline()
    {
        // Customs keep their native style at every size (Variant is ignored for them).
        if (HeroCustomIcons.All.TryGetValue(Kind.ToString(), out var custom))
            return custom.Style == HeroCustomStyle.Stroke;
        return Variant switch
        {
            HeroVariant.Solid => false,
            HeroVariant.Outline => true,
            _ => ActualWidth >= OutlineAtOrAbove || ActualHeight >= OutlineAtOrAbove,
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var outline = UseOutline();
        var resolved = Resolve(Kind.ToString(), outline);
        if (resolved is null) return;

        var (geometry, viewBox, strokeWidth) = resolved.Value;
        dc.PushTransform(new ScaleTransform(ActualWidth / viewBox, ActualHeight / viewBox));
        try
        {
            if (outline)
            {
                var pen = new Pen(Foreground, strokeWidth)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                dc.DrawGeometry(Brushes.Transparent, pen, geometry);
            }
            else
            {
                dc.DrawGeometry(Foreground, null, geometry);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private static (Geometry Geometry, double ViewBox, double StrokeWidth)? Resolve(string member, bool outline)
    {
        // Custom artwork first: fixed style, own viewBox.
        if (HeroCustomIcons.All.TryGetValue(member, out var custom))
        {
            var geometry = GetOrParse($"custom:{member}", custom.Data);
            return geometry is null ? null : (geometry, custom.ViewBox, custom.StrokeWidth);
        }

        var viewBox = ViewBox;
        var geometry2 = GetGeometry(member, outline) ?? GetGeometry(member, false);
        return geometry2 is null ? null : (geometry2, viewBox, OutlineStroke);
    }

    internal static Geometry? GetGeometry(string member, bool outline)
    {
        lock (Cache)
        {
            var key = $"hero:{member}:{(outline ? "o" : "s")}";
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (!HeroIconsData.PathData.TryGetValue((member, outline), out var data)) return null;
            var geometry = GetOrParse(key, data);
            return geometry;
        }
    }

    private static Geometry? GetOrParse(string key, string data)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            Geometry geometry;
            try
            {
                geometry = Geometry.Parse(data);
            }
            catch
            {
                return null;
            }
            geometry.Freeze();
            Cache[key] = geometry;
            return geometry;
        }
    }
}
