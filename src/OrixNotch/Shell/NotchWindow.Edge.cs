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

    /// <summary>The closed side tab's thickness: a little more than the top notch's height, room for its values.</summary>
    private double SideDepth => Math.Max(46, _collapsedH + 10);

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

    private double _sideLength = 125;

    /// <summary>
    /// The side tab's length: what its cells need plus a little room at each end, but never shorter than 2.6× its
    /// width, so it's always a tall capsule (an empty tab at startup would otherwise be a near-circle).
    /// </summary>
    private double SideTabLength() => Math.Max(_sideLength, Math.Round(SideDepth * 2.6));

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
            var glyph = new HeroIcon { Kind = icon, Width = 19, Height = 19, Margin = new Thickness(0, 6, 0, 6) };
            glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
            SideLayer.Children.Add(glyph);
        }
        foreach (var item in IdleInfoService.Instance.Items.Where(i => i.Id is not ("timeline" or "avatar")).Take(6))
            SideLayer.Children.Add(SideCell(item));
        if (SideLayer.Children.Count == 0)
            SideLayer.Children.Add(new BrandMark { Variant = BrandVariant.Gradient, Width = 19, Height = 19, Margin = new Thickness(0, 6, 0, 6) });

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
            const double size = 32;
            var grid = new Grid { Width = size, Height = size };
            var track = new System.Windows.Shapes.Ellipse { Margin = new Thickness(3), StrokeThickness = 3 };
            track.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TrackBrush");
            grid.Children.Add(track);
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = BuildMiniArc(level, size, 4.5), Stroke = brush, StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var inner = IdleGlyph(item, 15);
            if (inner is Control glyph && item.Logo is null) glyph.Foreground = brush;
            inner.HorizontalAlignment = HorizontalAlignment.Center;
            inner.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(inner);
            cell = grid;
        }
        else
        {
            var stack = new StackPanel();
            var glyph = IdleGlyph(item, 18);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(glyph);
            if (item.Short is { Length: > 0 } text)
            {
                var value = new TextBlock
                {
                    Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
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

    // ---------------------------------------------------------------- corner turn
    //
    // Chased into the end of its edge, the closed notch turns the corner onto the next edge: it tucks into the corner
    // (shrinks to a square of its own depth, flush with both screen edges, with a big round on the inner corner),
    // switches edge while it's that square (identical either way round, so the switch is invisible), then stretches
    // out along the new edge. It stays there: Position in Settings becomes that edge, and the existing dodge spring
    // brings it to the middle of it once the pointer leaves.

    private const double TurnSeconds = 0.36;

    private ScreenEdge? _turnTo;   // non-null while turning
    private int _turnDir;          // the direction it was pushed along the old edge (+1 / -1)
    private int _turnEntry;        // which end of the new edge it comes out of (-1 start, +1 end)
    private double _turnT;         // 0–1
    private bool _turnSwapped;
    private double _turnFromSpan, _turnToSpan;

    private bool Turning => _turnTo is not null || _bending;

    /// <summary>The edge round the corner from <paramref name="edge"/> when pushed along it, and the end it enters at.</summary>
    private static (ScreenEdge To, int Entry) NextEdge(ScreenEdge edge, int dir) => (edge, dir) switch
    {
        (ScreenEdge.Top, > 0) => (ScreenEdge.Right, -1),
        (ScreenEdge.Top, _) => (ScreenEdge.Left, -1),
        (ScreenEdge.Bottom, > 0) => (ScreenEdge.Right, +1),
        (ScreenEdge.Bottom, _) => (ScreenEdge.Left, +1),
        (ScreenEdge.Left, > 0) => (ScreenEdge.Bottom, -1),
        (ScreenEdge.Left, _) => (ScreenEdge.Top, -1),
        (ScreenEdge.Right, > 0) => (ScreenEdge.Bottom, +1),
        _ => (ScreenEdge.Top, +1),
    };

    private double TurnGap => Floating ? IslandGap : 0;

    /// <summary>Starts turning the corner at the <paramref name="dir"/> end of the current edge.</summary>
    private void StartCornerTurn(int dir)
    {
        if (Turning) return;
        if (BendCorners)
        {
            StartBend(dir); // the L-shaped alternative (NotchWindow.Bend.cs)
            return;
        }
        var (to, entry) = NextEdge(Edge, dir);
        _turnTo = to;
        _turnDir = dir;
        _turnEntry = entry;
        _turnT = 0;
        _turnSwapped = false;
        _turnFromSpan = Vertical ? _height.Value : _width.Value;
        _dodging = false;
        _dodgeSpeed = 0;
        if (Motion.Reduced)
        {
            SwapEdge();
            EndCornerTurn();
            return;
        }
        StartAnimation();
    }

    /// <summary>Advances the turn by <paramref name="dt"/> seconds and sets the notch's size and place for it.</summary>
    private void StepCornerTurn(double dt)
    {
        _turnT = Math.Min(1, _turnT + dt / TurnSeconds);
        if (!_turnSwapped && _turnT >= 0.5) SwapEdge();
        var depth = ClosedDepth;
        double span;
        if (_turnT < 0.5) span = Lerp(_turnFromSpan, depth, Ease(_turnT / 0.5));
        else span = Lerp(depth, _turnToSpan, Ease((_turnT - 0.5) / 0.5));
        // The far side stays flush with the corner: on the old edge at the end it was pushed to, on the new one at
        // the end it came out of.
        var end = _turnSwapped ? _turnEntry : _turnDir;
        var along = end * Math.Max(0, _maxShove - TurnGap - span / 2);
        _shove.Value = _shove.Target = along;
        _shove.Velocity = 0;
        var (w, h) = Vertical ? (depth, span) : (span, depth);
        _width.Value = _width.Target = w;
        _height.Value = _height.Target = h;
        _width.Velocity = _height.Velocity = 0;
        if (_turnT >= 1) EndCornerTurn();

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        static double Ease(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2; // ease in-out
    }

    /// <summary>How tucked into the corner the outline is: 0 = its normal shape, 1 = the corner piece.</summary>
    private double CornerAmount => _turnTo is null ? 0 : _turnT < 0.5 ? _turnT / 0.5 : 1 - (_turnT - 0.5) / 0.5;

    /// <summary>Which end of the span the corner is at, while turning (+1 the end, -1 the start).</summary>
    private int CornerEnd => _turnSwapped ? _turnEntry : _turnDir;

    /// <summary>At the midpoint (a square, the same either way round): onto the new edge.</summary>
    private void SwapEdge()
    {
        if (_turnTo is not { } to) return;
        _turnSwapped = true;
        App.Settings.NotchEdge = to.ToString();
        SettingsService.Save(notify: false);
        // The new edge's half-length first, so the window lands with the square already at the corner.
        var dpi = VisualTreeHelper.GetDpi(this);
        var (_, _, _, _, maxShove) = EdgePlacement(CurrentScreen(), dpi);
        _maxShove = maxShove;
        _shove.Value = _shove.Target = _turnEntry * Math.Max(0, maxShove - TurnGap - ClosedDepth / 2);
        Place();
        _turnToSpan = Vertical ? SideTabLength() : ActivityWidth();
        _shapeDrawn = default;
    }

    private void EndCornerTurn()
    {
        if (!_turnSwapped) SwapEdge();
        _turnTo = null;
        _shapeDrawn = default;
        SetCollapsedTarget();
        SetShove(0); // settle in the middle of the new edge (the dodge takes over again if the pointer is still near)
        StartAnimation();
        if (!_expanded) ShowNextPeek(); // an alert that arrived mid-turn
    }

    /// <summary>Opening mid-turn: finish it at once, on the new edge.</summary>
    private void FinishCornerTurnNow()
    {
        if (_bending)
        {
            FinishBendNow();
            return;
        }
        if (!Turning) return;
        _turnT = 1;
        EndCornerTurn();
    }
}
