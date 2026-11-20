using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OrixNotch.Interop;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// The "Bend" corner turn (Settings › Notch › Corner turn), the alternative to the tuck in NotchWindow.Edge.cs: chased
/// into a corner, the notch becomes one L-shaped body hugging both screen edges. The arm on the old edge shortens
/// while the arm on the new edge grows, the outer corner flush in the screen corner and the inner corner rounded,
/// until it's a normal notch on the new edge. An L doesn't fit the edge-aligned window, so for the bend the window
/// sits square in the screen corner and the notch's content hides until it's done.
/// </summary>
public partial class NotchWindow
{
    private const double BendSeconds = 0.45;
    private const double BendWindow = 860; // DIPs: the square window the L is drawn in

    private bool _bending;
    private double _bendT;
    private ScreenEdge _bendFrom, _bendTo;
    private int _bendEntry;
    private double _bendFromSpan, _bendToSpan, _bendFromDepth, _bendToDepth;
    private Vector _armOld, _armNew;   // unit vectors from the corner along the old and the new edge (window DIPs)
    private Point _corner;             // the screen corner, in window DIPs

    private static bool BendCorners => App.Settings.CornerStyle == "Bend";

    /// <summary>Starts the L-shaped turn at the <paramref name="dir"/> end of the current edge.</summary>
    private void StartBend(int dir)
    {
        var (to, entry) = NextEdge(Edge, dir);
        _bendFrom = Edge;
        _bendTo = to;
        _bendEntry = entry;
        _bendT = 0;
        _bendFromSpan = Vertical ? _height.Value : _width.Value;
        _bendFromDepth = ClosedDepth;
        var toVertical = to is ScreenEdge.Left or ScreenEdge.Right;
        _bendToDepth = toVertical ? SideDepth : _collapsedH;
        _bendToSpan = toVertical ? Math.Max(_sideLength, Math.Round(SideDepth * 2.6)) : ActivityWidth();
        _dodging = false;
        _dodgeSpeed = 0;

        // Which screen corner, and the directions away from it along each edge.
        var right = (_bendFrom, dir) is (ScreenEdge.Top or ScreenEdge.Bottom, > 0) || _bendFrom == ScreenEdge.Right || to == ScreenEdge.Right;
        var bottom = _bendFrom == ScreenEdge.Bottom || to == ScreenEdge.Bottom
                     || (_bendFrom is ScreenEdge.Left or ScreenEdge.Right && dir > 0);
        var alongHorizontal = new Vector(right ? -1 : 1, 0);
        var alongVertical = new Vector(0, bottom ? -1 : 1);
        var fromHorizontal = _bendFrom is ScreenEdge.Top or ScreenEdge.Bottom;
        _armOld = fromHorizontal ? alongHorizontal : alongVertical;
        _armNew = fromHorizontal ? alongVertical : alongHorizontal;

        // The window: a square whose corner is the screen corner (bottom and sides above the taskbar, like Place).
        var screen = CurrentScreen();
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        var top = _bendFrom == ScreenEdge.Top || to == ScreenEdge.Top ? screen.Bounds.Top : area.Top;
        var size = (int)Math.Round(BendWindow * dpi.DpiScaleX);
        var sizeY = (int)Math.Round(BendWindow * dpi.DpiScaleY);
        var x = right ? area.Right - size : area.Left;
        var y = bottom ? area.Bottom - sizeY : top;
        Width = Height = BendWindow;
        _corner = new Point(right ? BendWindow : 0, bottom ? BendWindow : 0);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, size, sizeY, Win32.SWP_NOACTIVATE);

        _bending = true;
        Body.Visibility = Visibility.Hidden; // the content waits for the new edge
        Pill.HorizontalAlignment = HorizontalAlignment.Left;
        Pill.VerticalAlignment = VerticalAlignment.Top;
        Pill.Margin = new Thickness(0);
        Pill.Width = Pill.Height = BendWindow;
        if (Motion.Reduced) _bendT = 1;
        DrawBend();
        StartAnimation();
    }

    private void StepBend(double dt)
    {
        _bendT = Math.Min(1, _bendT + dt / BendSeconds);
        DrawBend();
        if (_bendT >= 1) EndBend();
    }

    /// <summary>The L for the current progress: arm lengths trade from the old edge to the new one.</summary>
    private void DrawBend()
    {
        var e = _bendT < 0.5 ? 2 * _bendT * _bendT : 1 - Math.Pow(-2 * _bendT + 2, 2) / 2; // ease in-out
        var gap = Floating ? IslandGap : 0;
        // Each arm's length past the corner block (the block is as deep as the other arm is thick).
        var oldArm = Math.Max(0, (_bendFromSpan - _bendToDepth) * (1 - e));
        var newArm = Math.Max(0, (_bendToSpan - _bendFromDepth) * e);
        var radius = Floating ? Math.Min(_bendFromDepth, _bendToDepth) / 2 : RadiusCollapsed;
        Shape.Data = LGeometry(_corner + (_armOld + _armNew) * gap, _armOld, _armNew,
            oldArm, newArm, _bendFromDepth, _bendToDepth, radius, outerRadius: Floating ? radius * 0.6 : 0);
        if (GlassSheen.Visibility == Visibility.Visible) GlassSheen.Data = GlassGlow.Data = Shape.Data;
    }

    /// <summary>
    /// An L hugging a screen corner: from <paramref name="corner"/> one arm runs along <paramref name="a"/> (on the
    /// old edge, <paramref name="depthA"/> thick) and the other along <paramref name="b"/> (the new edge,
    /// <paramref name="depthB"/> thick). The outer corner sits in the screen corner, the inner corner is rounded, and
    /// each free end is rounded on its inner side (the side against the screen edge stays square).
    /// </summary>
    private static Geometry LGeometry(Point corner, Vector a, Vector b, double armA, double armB, double depthA, double depthB,
        double radius, double outerRadius)
    {
        // Canonical frame: u along a, v along b. Arm A covers u ∈ [0, depthB + armA], v ∈ [0, depthA];
        // arm B covers u ∈ [0, depthB], v ∈ [0, depthA + armB].
        Point P(double u, double v) => corner + a * u + b * v;
        var endA = depthB + armA;
        var endB = depthA + armB;
        var inner = Math.Min(Math.Min(armA, armB), Math.Min(depthA, depthB)) * 0.9;     // concave: shrinks away as an arm vanishes
        var rA = Math.Min(radius, Math.Min(depthA, Math.Max(0, endA - depthB - inner)));  // free end of arm A
        var rB = Math.Min(radius, Math.Min(depthB, Math.Max(0, endB - depthA - inner)));
        if (armB <= 0.5) rA = Math.Min(radius, depthA); // a plain bar on the old edge
        if (armA <= 0.5) rB = Math.Min(radius, depthB);
        // Mirroring (a × b < 0 on screen) flips which way the arcs turn.
        var clockwise = a.X * b.Y - a.Y * b.X > 0;
        var convex = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        var concave = clockwise ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(outerRadius, 0), true, true);
            c.LineTo(P(endA, 0), true, true);
            c.LineTo(P(endA, depthA - rA), true, true);
            if (rA > 0) c.ArcTo(P(endA - rA, depthA), new Size(rA, rA), 0, false, convex, true, true);
            if (armB > 0.5)
            {
                c.LineTo(P(depthB + inner, depthA), true, true);
                if (inner > 0) c.ArcTo(P(depthB, depthA + inner), new Size(inner, inner), 0, false, concave, true, true);
                c.LineTo(P(depthB, endB - rB), true, true);
            }
            else c.LineTo(P(depthB, depthA), true, true);
            if (armB > 0.5 && rB > 0) c.ArcTo(P(depthB - rB, endB), new Size(rB, rB), 0, false, convex, true, true);
            c.LineTo(P(0, endB), true, true);
            c.LineTo(P(0, outerRadius), true, true);
            if (outerRadius > 0) c.ArcTo(P(outerRadius, 0), new Size(outerRadius, outerRadius), 0, false, convex, true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The L has become a notch on the new edge: switch to it and let the content back in.</summary>
    private void EndBend()
    {
        _bending = false;
        App.Settings.NotchEdge = _bendTo.ToString();
        SettingsService.Save(notify: false);
        Pill.Width = double.NaN;
        Pill.Height = double.NaN;
        var dpi = VisualTreeHelper.GetDpi(this);
        var (_, _, _, _, maxShove) = EdgePlacement(CurrentScreen(), dpi);
        _maxShove = maxShove;
        var gap = Floating ? IslandGap : 0;
        _shove.Value = _shove.Target = _bendEntry * Math.Max(0, maxShove - gap - _bendToSpan / 2);
        _shove.Velocity = 0;
        var (w, h) = Vertical ? (_bendToDepth, _bendToSpan) : (_bendToSpan, _bendToDepth);
        _width.Value = _width.Target = w;
        _height.Value = _height.Target = h;
        _width.Velocity = _height.Velocity = 0;
        Place();
        _shapeDrawn = default;
        ApplyGeometry();
        Body.Visibility = Visibility.Visible;
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Base) { EasingFunction = Motion.Out });
        SetCollapsedTarget();
        SetShove(0); // settle in the middle of the new edge
        StartAnimation();
        if (!_expanded) ShowNextPeek();
    }

    /// <summary>Opening mid-bend: finish it at once.</summary>
    private void FinishBendNow()
    {
        if (!_bending) return;
        _bendT = 1;
        EndBend();
    }
}
