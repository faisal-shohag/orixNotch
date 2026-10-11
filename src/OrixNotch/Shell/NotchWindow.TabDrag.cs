using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrixNotch.Services;

namespace OrixNotch.Shell;

/// <summary>
/// Press and hold a tool tab to pick it up, drag it sideways, and drop it in a new spot: the other
/// tabs glide aside as it passes and the new order is saved. Settings stays last and can't be passed.
/// A quick click still just switches tools.
/// </summary>
public partial class NotchWindow
{
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(320);
    private const double HoldSlop = 6;      // moving this far before the hold fires means it's not a hold
    private const double LiftScale = 1.15;

    private readonly DispatcherTimer _holdTimer = new() { Interval = HoldDelay };
    private RadioButton? _holdTab;          // pressed, waiting for the hold
    private RadioButton? _dragTab;          // picked up
    private Point _pressAt;                 // pointer, in Tabs coordinates
    private List<RadioButton> _dragTabs = new();
    private List<double> _slotX = new();    // each tool tab's resting left edge
    private double _slotW;
    private int _dragFrom;
    private int _dragTo;
    private bool _dragSettling;

    private void WireTabDrag(RadioButton tab)
    {
        tab.RenderTransformOrigin = new Point(0.5, 0.5);
        tab.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        tab.PreviewMouseLeftButtonDown += OnTabPress;
        tab.PreviewMouseMove += OnTabMove;
        tab.PreviewMouseLeftButtonUp += OnTabRelease;
        tab.LostMouseCapture += (_, _) =>
        {
            if (_dragTab == tab && !_dragSettling) CancelTabDrag();
        };
        if (_holdTimer.Tag is null)
        {
            _holdTimer.Tag = true;
            _holdTimer.Tick += (_, _) => PickUpTab();
        }
    }

    private static ScaleTransform ScaleOf(UIElement tab) => (ScaleTransform)((TransformGroup)tab.RenderTransform).Children[0];
    private static TranslateTransform ShiftOf(UIElement tab) => (TranslateTransform)((TransformGroup)tab.RenderTransform).Children[1];

    private void OnTabPress(object sender, MouseButtonEventArgs e)
    {
        if (_dragTab is not null || _dragSettling) return;
        _holdTab = (RadioButton)sender;
        _pressAt = e.GetPosition(Tabs);
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void OnTabMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(Tabs);
        if (_dragTab is null)
        {
            // Wandering off before the hold fires: it's a click or a slip, not a drag.
            if (_holdTab is not null && (at - _pressAt).Length > HoldSlop) CancelHold();
            return;
        }
        DragTabTo(at.X - _pressAt.X);
    }

    private void OnTabRelease(object sender, MouseButtonEventArgs e)
    {
        if (_dragTab is null)
        {
            CancelHold(); // a normal click: the RadioButton checks itself as usual
            return;
        }
        e.Handled = true; // a drop isn't a click
        DropTab();
    }

    private void CancelHold()
    {
        _holdTimer.Stop();
        _holdTab = null;
    }

    private void PickUpTab()
    {
        _holdTimer.Stop();
        var tab = _holdTab;
        _holdTab = null;
        if (tab is null || Mouse.LeftButton != MouseButtonState.Pressed || !TabBar.IsVisible) return;

        _dragTabs = Tabs.Children.OfType<RadioButton>().Where(t => (string)t.Tag != SettingsId).ToList();
        _dragFrom = _dragTo = _dragTabs.IndexOf(tab);
        if (_dragFrom < 0 || _dragTabs.Count < 2) return;
        _slotX = _dragTabs.Select(t => t.TranslatePoint(new Point(0, 0), Tabs).X).ToList();
        _slotW = _dragTabs.Count > 1 ? _slotX[1] - _slotX[0] : tab.ActualWidth + tab.Margin.Left + tab.Margin.Right;

        _dragTab = tab;
        if (!tab.IsMouseCaptured) tab.CaptureMouse();
        Panel.SetZIndex(tab, 10);
        ToolTipService.SetIsEnabled(tab, false);
        if (tab.ToolTip is ToolTip { IsOpen: true } open) open.IsOpen = false;
        Mouse.OverrideCursor = GrabCursor.Value; // wins over the hand cursor of whatever is under the pointer
        Animate(ScaleOf(tab), ScaleTransform.ScaleXProperty, LiftScale, 160);
        Animate(ScaleOf(tab), ScaleTransform.ScaleYProperty, LiftScale, 160);
        tab.BeginAnimation(OpacityProperty, new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(160)));
    }

    private void DragTabTo(double dx)
    {
        var tab = _dragTab!;
        // Stay within the tool tabs (Settings keeps the last spot).
        dx = Math.Clamp(dx, _slotX[0] - _slotX[_dragFrom], _slotX[^1] - _slotX[_dragFrom]);
        var shift = ShiftOf(tab);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = dx;

        var target = (int)Math.Round((_slotX[_dragFrom] + dx - _slotX[0]) / _slotW);
        target = Math.Clamp(target, 0, _dragTabs.Count - 1);
        if (target != _dragTo)
        {
            _dragTo = target;
            // Tabs between the start and the target step one slot toward the gap; the rest go home.
            for (var i = 0; i < _dragTabs.Count; i++)
            {
                if (i == _dragFrom) continue;
                var offset = i > _dragFrom && i <= _dragTo ? -_slotW
                    : i < _dragFrom && i >= _dragTo ? _slotW
                    : 0;
                Animate(ShiftOf(_dragTabs[i]), TranslateTransform.XProperty, offset, 140);
            }
        }
        FollowIndicator();
    }

    /// <summary>Keeps the selected-tab highlight under the active tab while tabs move.</summary>
    private void FollowIndicator()
    {
        var active = _dragTabs.FirstOrDefault(t => (string)t.Tag == _activeTool);
        if (active is null) return;
        var i = _dragTabs.IndexOf(active);
        var x = active == _dragTab
            ? _slotX[i] + ShiftOf(active).X
            : _slotX[i] + (i > _dragFrom && i <= _dragTo ? -_slotW : i < _dragFrom && i >= _dragTo ? _slotW : 0);
        var origin = Tabs.TranslatePoint(new Point(0, 0), TabStrip).X;
        if (active == _dragTab)
        {
            TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorShift.X = origin + x;
        }
        else
        {
            Animate(TabIndicatorShift, TranslateTransform.XProperty, origin + x, 140);
        }
    }

    private void DropTab()
    {
        var tab = _dragTab!;
        _dragSettling = true;
        Animate(ScaleOf(tab), ScaleTransform.ScaleXProperty, 1, 150);
        Animate(ScaleOf(tab), ScaleTransform.ScaleYProperty, 1, 150);
        tab.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        Animate(ShiftOf(tab), TranslateTransform.XProperty, _slotX[_dragTo] - _slotX[_dragFrom], 150, () => FinishTabDrag(commit: true));
    }

    private void CancelTabDrag()
    {
        if (_dragTab is not { } tab) return;
        _dragTo = _dragFrom;
        _dragSettling = true;
        foreach (var t in _dragTabs.Where(t => t != tab)) Animate(ShiftOf(t), TranslateTransform.XProperty, 0, 140);
        Animate(ScaleOf(tab), ScaleTransform.ScaleXProperty, 1, 150);
        Animate(ScaleOf(tab), ScaleTransform.ScaleYProperty, 1, 150);
        tab.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        Animate(ShiftOf(tab), TranslateTransform.XProperty, 0, 150, () => FinishTabDrag(commit: false));
    }

    /// <summary>Esc while dragging puts the tab back. Returns true when it handled the key.</summary>
    private bool CancelTabDragOnEscape()
    {
        if (_dragTab is null || _dragSettling) return false;
        CancelTabDrag();
        return true;
    }

    private void FinishTabDrag(bool commit)
    {
        var tab = _dragTab;
        if (tab is null) return;
        var moved = commit && _dragTo != _dragFrom;

        foreach (var t in _dragTabs)
        {
            ShiftOf(t).BeginAnimation(TranslateTransform.XProperty, null);
            ShiftOf(t).X = 0;
            ScaleOf(t).BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ScaleOf(t).BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ScaleOf(t).ScaleX = ScaleOf(t).ScaleY = 1;
            t.BeginAnimation(OpacityProperty, null);
        }
        Panel.SetZIndex(tab, 0);
        ToolTipService.SetIsEnabled(tab, true);
        Mouse.OverrideCursor = null;

        if (moved)
        {
            // Place the button where it was dropped (Settings is never in _dragTabs, so it stays last).
            var targetChild = Tabs.Children.IndexOf(_dragTabs[_dragTo]);
            Tabs.Children.Remove(tab);
            Tabs.Children.Insert(targetChild, tab);
        }

        _dragTab = null;
        _dragSettling = false;
        if (tab.IsMouseCaptured) tab.ReleaseMouseCapture(); // also clears the button's pressed state
        Tabs.UpdateLayout();
        MoveTabIndicator(_activeTool, animate: false);
        if (moved) SaveTabOrder();
    }

    /// <summary>
    /// Saves the bar's order. Hidden tools keep their place in the full list: each visible slot
    /// in the saved order takes the next tool from the bar.
    /// </summary>
    private void SaveTabOrder()
    {
        var bar = new Queue<string>(Tabs.Children.OfType<RadioButton>().Select(t => (string)t.Tag).Where(id => id != SettingsId));
        var visible = bar.ToHashSet();
        var order = ToolRegistry.Ordered(includeHidden: true)
            .Select(t => visible.Contains(t.Id) ? bar.Dequeue() : t.Id)
            .ToList();
        App.Settings.ToolOrder = order;
        SettingsService.Save();
    }

    /// <summary>Closed-hand "grabbing" cursor (Windows has none built in), drawn once at first use.</summary>
    private static readonly Lazy<Cursor> GrabCursor = new(CreateGrabCursor);

    /// <summary>
    /// Draws a grabbing hand like the system cursors (white, dark outline) and wraps it as a 32×32 .cur
    /// with PNG data, hotspot in the middle. Falls back to the sideways arrow if anything fails.
    /// </summary>
    private static Cursor CreateGrabCursor()
    {
        try
        {
            const int size = 32;
            // Phosphor "hand-grabbing" (light, 256 box): the outline glyph, plus its outer contour
            // filled white underneath so the hand reads on any background.
            const string glyph = "M188,82a25.85,25.85,0,0,0-14.59,4.49A26,26,0,0,0,128,75.41,26,26,0,0,0,82,92v22H68a26,26,0,0,0-26,26v12a86,86,0,0,0,172,0V108A26,26,0,0,0,188,82Zm14,70a74,74,0,0,1-148,0V140a14,14,0,0,1,14-14H82v26a6,6,0,0,0,12,0V92a14,14,0,0,1,28,0v28a6,6,0,0,0,12,0V92a14,14,0,0,1,28,0v28a6,6,0,0,0,12,0V108a14,14,0,0,1,28,0Z";
            var outline = Geometry.Parse("F1 " + glyph);
            var silhouette = Geometry.Parse(glyph[..(glyph.IndexOf('Z') + 1)]);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
                dc.DrawGeometry(Brushes.White, null, silhouette);
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), null, outline);
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            var data = png.ToArray();

            var cur = new MemoryStream();
            using (var w = new BinaryWriter(cur, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write((short)0); w.Write((short)2); w.Write((short)1);       // reserved, type = cursor, one image
                w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)(size / 2)); w.Write((short)(size / 2));          // hotspot
                w.Write(data.Length); w.Write(22);                               // image size, offset
                w.Write(data);
            }
            cur.Position = 0;
            return new Cursor(cur);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return Cursors.SizeWE;
        }
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, int ms, Action? done = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done is not null) animation.Completed += (_, _) => done();
        target.BeginAnimation(property, animation);
    }
}
