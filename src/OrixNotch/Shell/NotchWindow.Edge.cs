using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrixNotch.Interop;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// Which screen edge the notch sits on (Settings › Display › Position). The springs stay screen-oriented (_width is
/// the notch's width, _height its height); this maps them to the edge: its "span" runs along the edge and its
/// "depth" reaches into the screen. On the left or right the closed notch is a narrow side tab of icons
/// (<see cref="RenderSide"/>), and the alert card and the open panel grow sideways out of the edge.
/// </summary>
public partial class NotchWindow
{
    private enum ScreenEdge { Top, Bottom, Left, Right }

    private static ScreenEdge Edge => App.Settings.NotchEdge switch
    {
        "Bottom" => ScreenEdge.Bottom,
        "Left" => ScreenEdge.Left,
        "Right" => ScreenEdge.Right,
        _ => ScreenEdge.Top,
    };

    /// <summary>On the left or right edge: the notch's span is vertical.</summary>
    private static bool Vertical => Edge is ScreenEdge.Left or ScreenEdge.Right;

    private double _expandedW = 720;

    /// <summary>The closed side tab's thickness: the same as the top notch's height.</summary>
    private double SideDepth => Math.Max(36, _collapsedH);

    /// <summary>How far the closed notch reaches into the screen on the current edge.</summary>
    private double ClosedDepth => Vertical ? SideDepth : _collapsedH; // the open panel's width, the depth of an open side notch

    /// <summary>Closed size for the current activity: a horizontal pill on top/bottom, a thin tab of icons on the sides.</summary>
    private void SetCollapsedTarget()
    {
        if (Vertical)
        {
            _width.Target = SideDepth;
            _height.Target = SideTabLength();
        }
        else
        {
            _width.Target = ActivityWidth();
            _height.Target = _collapsedH;
        }
    }

    /// <summary>Puts the notch's pieces on the right edge: the pill hugs it, the hidden-notch hint marks it.</summary>
    private void ApplyEdgeLayout()
    {
        var edge = Edge;
        Pill.HorizontalAlignment = edge switch { ScreenEdge.Left => HorizontalAlignment.Left, ScreenEdge.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        Pill.VerticalAlignment = edge switch { ScreenEdge.Bottom => VerticalAlignment.Bottom, ScreenEdge.Top => VerticalAlignment.Top, _ => VerticalAlignment.Center };
        EdgeHint.HorizontalAlignment = Pill.HorizontalAlignment;
        EdgeHint.VerticalAlignment = Pill.VerticalAlignment;
        EdgeHint.Width = Vertical ? 3 : Math.Round(_idleW * 0.55);
        EdgeHint.Height = Vertical ? Math.Round(_idleW * 0.4) : 3;
        CollapsedLayer.VerticalAlignment = edge == ScreenEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        CollapsedLayer.Visibility = Vertical ? Visibility.Collapsed : CollapsedLayer.Visibility;
        if (Vertical) RenderSide();
        else SideLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>Window size and position for the edge, centred along it (bottom and sides stay off the taskbar).</summary>
    private (int X, int Y, int W, int H, double MaxShove) EdgePlacement(System.Windows.Forms.Screen screen, DpiScale dpi)
    {
        Width = 860;
        Height = Math.Max(Height, Vertical ? 860 : 600); // never shrink below what a tall panel (Settings) grew it to
        var w = (int)Math.Round(Width * dpi.DpiScaleX);
        var h = (int)Math.Round(Height * dpi.DpiScaleY);
        var b = screen.Bounds;
        var a = screen.WorkingArea;
        return Edge switch
        {
            ScreenEdge.Bottom => (a.Left + (a.Width - w) / 2, a.Bottom - h, w, h, a.Width / dpi.DpiScaleX / 2),
            ScreenEdge.Left => (a.Left, a.Top + (a.Height - h) / 2, w, h, a.Height / dpi.DpiScaleY / 2),
            ScreenEdge.Right => (a.Right - w, a.Top + (a.Height - h) / 2, w, h, a.Height / dpi.DpiScaleY / 2),
            _ => (b.Left + (b.Width - w) / 2, b.Top, w, h, b.Width / dpi.DpiScaleX / 2),
        };
    }

    /// <summary>
    /// The pointer relative to the notch's home: <c>Along</c> the edge from its centre, and <c>Away</c> from the edge
    /// into the screen, in DIPs. Read from screen coordinates (the window may be moving under us).
    /// </summary>
    private (double Along, double Away) EdgeLocal(Win32.POINT p)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var (sx, sy) = (dpi.DpiScaleX, dpi.DpiScaleY);
        return Edge switch
        {
            ScreenEdge.Bottom => ((p.X - (_homeX + Width * sx / 2)) / sx, (_homeY + Height * sy - p.Y) / sy),
            ScreenEdge.Left => ((p.Y - (_homeY + Height * sy / 2)) / sy, (p.X - _homeX) / sx),
            ScreenEdge.Right => ((p.Y - (_homeY + Height * sy / 2)) / sy, (_homeX + Width * sx - p.X) / sx),
            _ => ((p.X - (_homeX + Width * sx / 2)) / sx, (p.Y - _homeY) / sy),
        };
    }

    /// <summary>The notch body's length along the edge and its reach into the screen.</summary>
    private double BodySpan => Vertical ? Body.ActualHeight : Body.ActualWidth;
    private double BodyDepth => Vertical ? Body.ActualWidth : Body.ActualHeight;

    /// <summary>Maps the outline drawn for a top notch (span across, depth down) onto the actual edge.</summary>
    private static Transform EdgeTransform(double depth) => Edge switch
    {
        ScreenEdge.Bottom => new MatrixTransform(1, 0, 0, -1, 0, depth),
        ScreenEdge.Left => new MatrixTransform(0, 1, 1, 0, 0, 0),
        ScreenEdge.Right => new MatrixTransform(0, 1, -1, 0, depth, 0),
        _ => Transform.Identity,
    };

    // ---------------------------------------------------------------- side tab (left / right)

    private double _sideLength = 50;

    /// <summary>The side tab's length: what its cells need, plus a little room at each end.</summary>
    private double SideTabLength() => _sideLength;

    /// <summary>
    /// The closed side tab, top to bottom: the live activity's icon (music, timer or focus, reminder), then your idle
    /// items (up to six). Levels (AI usage, battery, CPU) are small rings filling in the item's colour around its icon or
    /// logo; the rest show their icon over a very short value ("23°", "4:12", "2.1M"). The full text is in the tooltip.
    /// </summary>
    private void RenderSide()
    {
        SideLayer.Children.Clear();
        var kind = NotchActivityService.Instance.Kind;
        AppIcon? activity = kind switch
        {
            NotchActivityKind.Music => AppIcon.MusicNote,
            NotchActivityKind.Focus => AppIcon.Bolt,
            NotchActivityKind.Break => AppIcon.Coffee,
            NotchActivityKind.Timer or NotchActivityKind.Stopwatch => AppIcon.Clock,
            NotchActivityKind.Reminder => AppIcon.CalendarDays,
            _ => null,
        };
        if (activity is { } icon)
        {
            var glyph = new HeroIcon { Kind = icon, Width = 16, Height = 16, Margin = new Thickness(0, 6, 0, 6) };
            glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
            SideLayer.Children.Add(glyph);
        }
        foreach (var item in IdleInfoService.Instance.Items.Where(i => i.Id is not ("timeline" or "avatar")).Take(6))
            SideLayer.Children.Add(SideCell(item));
        if (SideLayer.Children.Count == 0)
            SideLayer.Children.Add(new BrandMark { Variant = BrandVariant.Gradient, Width = 16, Height = 16, Margin = new Thickness(0, 6, 0, 6) });

        SideLayer.Measure(new Size(SideDepth, double.PositiveInfinity));
        var length = Math.Ceiling(SideLayer.DesiredSize.Height + 14);
        if (Math.Abs(length - _sideLength) > 0.5)
        {
            _sideLength = length;
            if (!_expanded && !_peeking)
            {
                SetCollapsedTarget();
                StartAnimation();
            }
        }
    }

    /// <summary>One item in the side tab: a ring for a level, else its icon over a short value.</summary>
    private static FrameworkElement SideCell(IdleItem item)
    {
        var brush = IdleBrush(item);
        FrameworkElement cell;
        if (item.Percent is { } level)
        {
            const double size = 26;
            var grid = new Grid { Width = size, Height = size };
            var track = new System.Windows.Shapes.Ellipse { Margin = new Thickness(2.5), StrokeThickness = 2.5 };
            track.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TrackBrush");
            grid.Children.Add(track);
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = BuildMiniArc(level, size, 3.75), Stroke = brush, StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var inner = IdleGlyph(item, 12);
            if (inner is Control glyph && item.Logo is null) glyph.Foreground = brush;
            inner.HorizontalAlignment = HorizontalAlignment.Center;
            inner.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(inner);
            cell = grid;
        }
        else
        {
            var stack = new StackPanel();
            var glyph = IdleGlyph(item, 15);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(glyph);
            if (item.Short is { Length: > 0 } text)
            {
                var value = new TextBlock
                {
                    Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0), Foreground = item.ValueBrush == "SubTextBrush" ? (Brush)Application.Current.Resources["SubTextBrush"] : brush,
                };
                stack.Children.Add(value);
            }
            cell = stack;
        }
        cell.Margin = new Thickness(0, 5, 0, 5);
        cell.HorizontalAlignment = HorizontalAlignment.Center;
        cell.ToolTip = item.Value;
        return cell;
    }
}
