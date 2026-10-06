using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using OrixNotch.Interop;
using OrixNotch.Services;
using OrixNotch.Settings;
using Forms = System.Windows.Forms;

namespace OrixNotch.Shell;

public partial class NotchWindow : Window
{
    // Expanded chrome around the active tool's content
    // Measured from OmniNotch's demo videos (≈1.15 px per pt), in DIPs:
    // open panel 726×323, header 56, tab bar 64, 18 side padding.
    private const double HeaderH = 56;
    private const double TabBarH = 64;
    private const double SidePad = 18;

    // Silhouette: concave "ears" where the notch meets the top edge, rounded bottom corners
    private const double EarCollapsed = 5;
    private const double EarExpanded = 7;
    private const double RadiusCollapsed = 10;
    private const double RadiusExpanded = 26;

    public const string SettingsId = "settings";

    private readonly Spring _width = new(207);
    private readonly Spring _height = new(32);
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _guardTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, FrameworkElement> _views = new();
    private readonly Storyboard _eq = new() { RepeatBehavior = RepeatBehavior.Forever };
    private readonly BlurEffect _blur = new() { Radius = 0, KernelType = KernelType.Gaussian };

    private IntPtr _hwnd;
    private bool _expanded;
    private bool _animating;
    private bool _eqRunning;
    private bool _pinned;
    private TimeSpan _lastFrame;
    private DateTime? _outsideSince;

    // Opened by hotkey/tray while the cursor is elsewhere: stay open until the cursor
    // visits the pill or the window loses focus.
    private bool _awaitingCursor;
    private int _holdOpen;
    private string _activeTool;
    private string? _backTarget;
    private double _expandedH = 220;
    private double _toolW = 560;
    private double _toolH = 120;

    // Closed-notch size, from Settings → Display
    private double _collapsedH = 38;
    private double _idleW = 207;
    private double _timerW = 251;
    private double _musicW = 357;

    public static NotchWindow? Instance { get; private set; }

    /// <summary>The notch started growing/shrinking/moving; overlays should hide.</summary>
    public event Action? GeometryChanging;

    /// <summary>The open notch stopped animating (or moved); overlays can be placed.</summary>
    public event Action? GeometrySettled;

    /// <summary>Open and not animating.</summary>
    public bool IsSettled => _expanded && !_animating;

    public NotchWindow()
    {
        InitializeComponent();
        Instance = this;
        _activeTool = App.Settings.LastTool;
        ApplyNotchSize();

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (Pill.IsMouseOver && !_expanded) Expand(activate: false);
        };
        _leaveTimer.Tick += (_, _) => CheckLeave();
        _guardTimer.Tick += (_, _) => Guard();

        Pill.MouseEnter += OnPillMouseEnter;
        Pill.MouseLeave += (_, _) => _hoverTimer.Stop();
        Pill.MouseLeftButtonUp += (_, _) =>
        {
            if (!_expanded) Expand(activate: true);
        };
        Pill.DragEnter += OnPillDragEnter;
        Pill.Drop += OnPillDrop;

        PreviewKeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            if (_expanded && !_pinned && _holdOpen == 0 && !IsCursorOverPill() && Mouse.Captured is null) Collapse();
        };

        NowPlayingService.Instance.PropertyChanged += OnActivityChanged;
        PomodoroService.Instance.PropertyChanged += OnActivityChanged;
        CountdownService.Instance.PropertyChanged += OnActivityChanged;
        StopwatchService.Instance.PropertyChanged += OnActivityChanged;
        NotchActivityService.Instance.PropertyChanged += (_, _) => UpdateActivity();
        SettingsService.Changed += OnSettingsChanged;
        HotkeyService.Triggered += OnHotkey;

        BuildEqualizer();
        Tabs.LayoutUpdated += (_, _) =>
        {
            if (TabIndicator.Opacity < 0.5 && _expanded && _activeTool != SettingsId) MoveTabIndicator(_activeTool, animate: false);
        };
        BuildTabs();
        UpdateActivity();
        ApplyGeometry();
    }

    // ---------------------------------------------------------------- window plumbing

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: no taskbar button, no Alt+Tab entry.
        var ex = (long)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        ex = (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        ClipboardService.Instance.Attach(_hwnd);
        HotkeyService.Attach(_hwnd);

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Place();
        _guardTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        HotkeyService.UnregisterAll();
        Win32.RemoveClipboardFormatListener(_hwnd);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_CLIPBOARDUPDATE:
                ClipboardService.Instance.OnClipboardUpdate();
                break;
            case Win32.WM_HOTKEY:
                handled = HotkeyService.Handle(wParam.ToInt32());
                break;
        }
        return IntPtr.Zero;
    }

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case "toggle":
                Toggle();
                break;
            case "teleprompter":
                if (_expanded && _activeTool == "teleprompter") TeleprompterService.Instance.Toggle();
                else OpenTool("teleprompter");
                break;
            default:
                OpenTool(action);
                break;
        }
    }

    /// <summary>Pins the window to the top-center of the chosen monitor (physical pixels).</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var screens = Forms.Screen.AllScreens;
        var index = App.Settings.MonitorIndex;
        var screen = index >= 0 && index < screens.Length ? screens[index] : Forms.Screen.PrimaryScreen ?? screens[0];

        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(Width * dpi.DpiScaleX);
        var h = (int)Math.Round(Height * dpi.DpiScaleY);
        var x = screen.Bounds.Left + (screen.Bounds.Width - w) / 2;
        var y = screen.Bounds.Top;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
        if (IsSettled) Dispatcher.BeginInvoke(() => GeometrySettled?.Invoke(), DispatcherPriority.Loaded);
    }

    /// <summary>Keeps the notch above other topmost windows and out of the way of fullscreen apps.</summary>
    private void Guard()
    {
        if (_hwnd == IntPtr.Zero) return;
        var hide = App.Settings.HideInFullscreen && !_expanded && Win32.IsSomethingFullscreen();
        var target = hide ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != target) Visibility = target;
        if (!hide)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void ApplyNotchSize()
    {
        (_idleW, _collapsedH) = App.Settings.NotchSize switch
        {
            "Small" => (180.0, 34.0),
            "Large" => (236.0, 42.0),
            "ExtraLarge" => (266.0, 46.0),
            _ => (207.0, 38.0), // OmniNotch's closed notch, ≈ a MacBook hardware notch
        };
        _timerW = _idleW + 44;
        _musicW = _idleW + 150;
        CollapsedLayer.Height = _collapsedH;
        if (!_expanded)
        {
            _width.Target = ActivityWidth();
            _height.Target = _collapsedH;
            StartAnimation();
        }
    }

    // ---------------------------------------------------------------- open / close

    public void Toggle()
    {
        if (_expanded) Collapse();
        else Expand(activate: true);
    }

    /// <summary>Opens the notch on a tool (or settings).</summary>
    public void OpenTool(string id)
    {
        Expand(activate: true);
        if (id == SettingsId) ShowSettings();
        else ShowTool(id);
    }

    public void Expand(bool activate)
    {
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        if (!_expanded)
        {
            _expanded = true;
            ShowTool(_activeTool == SettingsId ? App.Settings.LastTool : _activeTool);
            _outsideSince = null;
            _awaitingCursor = activate && !IsCursorOverPill();
            _leaveTimer.Start();
            SetEqualizer(false);
        }
        if (activate) Activate();
    }

    /// <summary>Keeps the notch open while a file dialog or similar is showing: <c>using (HoldOpen()) …</c></summary>
    public IDisposable HoldOpen()
    {
        _holdOpen++;
        return new Release(() =>
        {
            _holdOpen = Math.Max(0, _holdOpen - 1);
            _outsideSince = null;
            _awaitingCursor = true; // don't snap shut the moment the dialog closes
        });
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _leaveTimer.Stop();
        _height.Target = _collapsedH;
        _width.Target = ActivityWidth();
        if (ToolHost.Content is IToolView view) view.OnHidden();
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
        StartAnimation();
        UpdateActivity();
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        if (_expanded || App.Settings.OpenMode != OpenMode.Hover) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, App.Settings.HoverDelayMs));
        _hoverTimer.Start();
    }

    private void CheckLeave()
    {
        if (!_expanded || _pinned || _holdOpen > 0) return;
        if (IsCursorOverPill()) _awaitingCursor = false;
        if (_awaitingCursor) return;

        if (IsCursorOverPill() || Mouse.Captured is not null || IsTyping())
        {
            _outsideSince = null;
            return;
        }

        _outsideSince ??= DateTime.Now;
        if ((DateTime.Now - _outsideSince.Value).TotalMilliseconds > 350)
        {
            _outsideSince = null;
            Collapse();
        }
    }

    private bool IsTyping() => IsActive && Keyboard.FocusedElement is TextBox tb && tb.IsDescendantOf(Pill);

    private bool IsCursorOverPill()
    {
        if (!Win32.GetCursorPos(out var p) || PresentationSource.FromVisual(Body) is null) return false;
        var local = Body.PointFromScreen(new Point(p.X, p.Y));
        const double slack = 8;
        return local.X >= -slack && local.Y >= -slack &&
               local.X <= Body.ActualWidth + slack && local.Y <= Body.ActualHeight + slack;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_expanded) return;
        if (e.Key == Key.Escape)
        {
            if (_backTarget is not null) GoBack();
            else Collapse();
            e.Handled = true;
            return;
        }
        if (IsTyping() || _activeTool == SettingsId) return;

        if (e.Key == Key.Space && _activeTool == "teleprompter")
        {
            TeleprompterService.Instance.Toggle();
            e.Handled = true;
            return;
        }

        // Keyboard navigation: 1–9 jump to a tab, ←/→ step through tabs.
        var tools = ToolRegistry.Ordered();
        var index = tools.FindIndex(t => t.Id == _activeTool);
        if (e.Key is >= Key.D1 and <= Key.D9 && Keyboard.Modifiers == ModifierKeys.None)
        {
            var n = e.Key - Key.D1;
            if (n < tools.Count) ShowTool(tools[n].Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && index >= 0)
        {
            ShowTool(tools[(index + 1) % tools.Count].Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && index >= 0)
        {
            ShowTool(tools[(index - 1 + tools.Count) % tools.Count].Id);
            e.Handled = true;
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinIcon.Kind = _pinned ? AppIcon.PinSlash : AppIcon.Pin;
        PinButton.SetResourceReference(ForegroundProperty, _pinned ? "AccentBrush" : "SubTextBrush");
        PinButton.ToolTip = _pinned ? "Unpin" : "Keep open";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        var target = _backTarget ?? App.Settings.LastTool;
        _backTarget = null;
        ShowTool(target == SettingsId ? ToolRegistry.Ordered()[0].Id : target);
    }

    // ---------------------------------------------------------------- animation

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        GeometryChanging?.Invoke();
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0) return;
        dt = Math.Min(dt, 0.05);

        // Sub-step for stability on slow frames.
        var steps = (int)Math.Ceiling(dt / (1 / 240.0));
        var sub = dt / steps;
        var moving = false;
        for (var i = 0; i < steps; i++)
        {
            moving |= _width.Step(sub);
            moving |= _height.Step(sub);
        }

        ApplyGeometry();
        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            if (_expanded) Dispatcher.BeginInvoke(() => GeometrySettled?.Invoke(), DispatcherPriority.Loaded);
        }
    }

    private void ApplyGeometry()
    {
        var w = Math.Max(60, _width.Value);
        var h = Math.Max(20, _height.Value);

        // 0 = collapsed, 1 = fully open (to whatever size the current tool wants)
        var p = Math.Clamp((h - _collapsedH) / Math.Max(1, _expandedH - _collapsedH), 0, 1);
        var ear = EarCollapsed + (EarExpanded - EarCollapsed) * p;
        var radius = RadiusCollapsed + (RadiusExpanded - RadiusCollapsed) * p;

        Pill.Width = w + 2 * EarExpanded;
        Pill.Height = h;
        Body.Margin = new Thickness(EarExpanded, 0, EarExpanded, 0);
        Body.Width = w;
        Body.Height = h;
        Body.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        Shape.Data = NotchGeometry(w, h, ear, radius, EarExpanded - ear);

        // Content materialises out of a blur as the notch opens (and dissolves back on close).
        var reveal = Math.Clamp((p - 0.35) / 0.65, 0, 1);
        ExpandedLayer.Opacity = reveal;
        ExpandedLayer.Visibility = p > 0.3 ? Visibility.Visible : Visibility.Collapsed;
        _blur.Radius = (1 - reveal) * 14;
        ExpandedLayer.Effect = reveal < 0.999 && ExpandedLayer.Visibility == Visibility.Visible ? _blur : null;

        CollapsedLayer.Opacity = Math.Clamp(1 - p * 4, 0, 1);
        CollapsedLayer.Visibility = p < 0.25 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Notch silhouette: concave quarter-circle "ears" at the top-left and top-right that flow
    /// into the screen edge, straight sides, and rounded bottom corners.
    /// </summary>
    private static Geometry NotchGeometry(double bodyW, double h, double ear, double radius, double inset)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(bodyW / 2, h - ear)));
        var left = inset;
        var bodyL = inset + ear;
        var bodyR = bodyL + bodyW;
        var right = bodyR + ear;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(left, 0), true, true);
            c.ArcTo(new Point(bodyL, ear), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(bodyL, h - radius), true, true);
            c.ArcTo(new Point(bodyL + radius, h), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(bodyR - radius, h), true, true);
            c.ArcTo(new Point(bodyR, h - radius), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(bodyR, ear), true, true);
            c.ArcTo(new Point(right, 0), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    // ---------------------------------------------------------------- collapsed live activity

    private bool HasActivity() => NotchActivityService.Instance.HasActivity;

    /// <summary>Closed-notch width for the current state: compact for timers, wide for track text.</summary>
    private double ActivityWidth()
    {
        var timer = PomodoroService.Instance.IsRunning
            || CountdownService.Instance.IsRunning
            || StopwatchService.Instance.IsRunning;
        if (timer) return _timerW;
        var music = NowPlayingService.Instance.HasSession && NowPlayingService.Instance.IsPlaying;
        return music ? _musicW : _idleW;
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingService.IsPlaying) or nameof(NowPlayingService.HasSession)
            or nameof(PomodoroService.IsRunning) or nameof(CountdownService.IsRunning) or nameof(StopwatchService.IsRunning))
            UpdateActivity();
    }

    private void UpdateActivity()
    {
        var music = NowPlayingService.Instance.HasSession && NowPlayingService.Instance.IsPlaying;
        var timer = PomodoroService.Instance.IsRunning
            || CountdownService.Instance.IsRunning
            || StopwatchService.Instance.IsRunning;
        var act = NotchActivityService.Instance;
        act.Refresh();

        // Timer wins the text slot; music keeps the art on the left.
        // The timer ring shows only without music, like the old timer glyph.
        MiniArt.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        MiniTimerRing.Visibility = timer && !music ? Visibility.Visible : Visibility.Collapsed;
        if (timer && !music)
        {
            MiniTimerGlyph.Kind = act.Kind switch
            {
                NotchActivityKind.Break => AppIcon.Moon,
                NotchActivityKind.Timer => AppIcon.Clock,
                NotchActivityKind.Stopwatch => AppIcon.Clock,
                _ => AppIcon.Bolt,
            };
            MiniArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                act.Kind == NotchActivityKind.Break ? "GoodBrush" : "AccentBrush");
            MiniArc.Data = BuildMiniArc(act.Progress);
        }
        MiniTimer.Visibility = act.HasActivity ? Visibility.Visible : Visibility.Collapsed;
        if (music)
        {
            // Track text sits right beside the album art.
            MiniTimer.HorizontalAlignment = HorizontalAlignment.Left;
            MiniTimer.Margin = new Thickness(36, 0, 0, 0);
        }
        else
        {
            MiniTimer.HorizontalAlignment = HorizontalAlignment.Right;
            MiniTimer.Margin = new Thickness(0, 0, 12, 0);
        }
        MiniTimer.SetResourceReference(TextBlock.ForegroundProperty, act.Kind switch
        {
            NotchActivityKind.Break => "GoodBrush",
            NotchActivityKind.Music => "TextBrush",
            _ => "AccentBrush",
        });
        if (act.Kind == NotchActivityKind.Music)
        {
            MiniTimer.FontSize = 13;
            MiniTimer.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            MiniTimer.FontSize = 14;
            MiniTimer.FontWeight = FontWeights.Bold;
        }
        EqBars.Visibility = music && !timer ? Visibility.Visible : Visibility.Collapsed;
        SetEqualizer(!_expanded && music && !timer);

        if (!_expanded)
        {
            _width.Target = ActivityWidth();
            StartAnimation();
        }
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="progress"/> of the mini ring.</summary>
    private static Geometry BuildMiniArc(double progress)
    {
        const double size = 20, inset = 2.5;
        var radius = size / 2 - inset;
        var center = new Point(size / 2, size / 2);
        progress = Math.Clamp(progress, 0, 0.9999);
        if (progress <= 0.001) return Geometry.Empty;

        var angle = progress * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, progress > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void BuildEqualizer()
    {
        var bars = new[] { Bar1, Bar2, Bar3, Bar4 };
        var periods = new[] { 0.42, 0.55, 0.37, 0.6 };
        for (var i = 0; i < bars.Length; i++)
        {
            var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(periods[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, bars[i]);
            Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.ScaleY"));
            _eq.Children.Add(anim);
        }
    }

    /// <summary>Only animate while visible, so the idle app does not keep WPF rendering at 60 fps.</summary>
    private void SetEqualizer(bool on)
    {
        if (on == _eqRunning) return;
        _eqRunning = on;
        if (on) _eq.Begin(this, true);
        else _eq.Stop(this);
    }

    // ---------------------------------------------------------------- tools

    private void BuildTabs()
    {
        var tools = ToolRegistry.Ordered();
        if (tools.Count == 0) tools = [ToolRegistry.All[0]];

        var previousActive = _activeTool;
        if (_activeTool != SettingsId && tools.All(t => t.Id != _activeTool)) _activeTool = tools[0].Id;

        var wanted = tools.Select(t => t.Id).Append(SettingsId).ToList();
        var byId = tools.ToDictionary(t => t.Id);
        var changed = false;

        // Drop tabs for hidden tools. Live buttons are never recreated.
        for (var i = Tabs.Children.Count - 1; i >= 0; i--)
        {
            if (Tabs.Children[i] is RadioButton rb && rb.Tag is string tag && !wanted.Contains(tag))
            {
                Tabs.Children.RemoveAt(i);
                changed = true;
            }
        }

        // Insert missing tabs and shuffle existing ones into order in place.
        for (var i = 0; i < wanted.Count; i++)
        {
            var id = wanted[i];
            var existing = Tabs.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == id);
            if (existing is null)
            {
                Tabs.Children.Insert(i, MakeTab(id,
                    byId.TryGetValue(id, out var def) ? def.Name : "Settings",
                    byId.TryGetValue(id, out var iconDef) ? iconDef.Icon : AppIcon.Gear));
                changed = true;
            }
            else if (Tabs.Children.IndexOf(existing) != i)
            {
                Tabs.Children.Remove(existing);
                Tabs.Children.Insert(i, existing);
                changed = true;
            }
        }

        if (!changed) return;
        // Only re-present when the active tool vanished; otherwise glide the
        // indicator over after layout instead of rebuilding the content.
        if (_expanded && _activeTool != previousActive) ShowTool(_activeTool);
        else if (_expanded) Dispatcher.BeginInvoke(() => MoveTabIndicator(_activeTool), DispatcherPriority.Loaded);
    }

    private RadioButton MakeTab(string id, string name, AppIcon icon)
    {
        var tab = new RadioButton
        {
            Style = (Style)FindResource("TabButton"),
            Content = new HeroIcon { Kind = icon, Width = 17, Height = 17 },
            ToolTip = name,
            GroupName = "tools",
            Tag = id,
            IsChecked = id == _activeTool,
        };
        tab.Checked += (_, _) =>
        {
            if (id == SettingsId) ShowSettings();
            else ShowTool(id);
        };
        return tab;
    }

    /// <summary>Shows a tool. <paramref name="backTo"/> makes it a sub-page with a back chevron.</summary>
    public void ShowTool(string id, string? backTo = null)
    {
        if (id == SettingsId)
        {
            ShowSettings();
            return;
        }

        var def = ToolRegistry.All.FirstOrDefault(t => t.Id == id) ?? ToolRegistry.All[0];
        _backTarget = backTo;
        Present(def.Id, def.Name, def.Width, def.Height, () => def.Create(), showTabs: true);

        if (App.Settings.LastTool != def.Id)
        {
            App.Settings.LastTool = def.Id;
            SettingsService.Save(notify: false);
        }
    }

    public void ShowSettings()
    {
        if (_activeTool != SettingsId) _backTarget = _activeTool;
        // Settings opens taller than the tools (≈750×512 in OmniNotch).
        Present(SettingsId, "Settings", 714, 438, () => new SettingsView(), showTabs: false);
    }

    private void Present(string id, string title, double w, double h, Func<FrameworkElement> create, bool showTabs)
    {
        if (!_views.TryGetValue(id, out var view))
        {
            view = create();
            _views[id] = view;
        }

        var oldIndex = TabIndex(_activeTool);
        var newIndex = TabIndex(id);
        var switched = !ReferenceEquals(ToolHost.Content, view);
        if (switched)
        {
            if (ToolHost.Content is IToolView old) old.OnHidden();
            ToolHost.Content = view;
        }

        _activeTool = id;
        TitleText.Text = title;
        BackButton.Visibility = _backTarget is not null && id != SettingsId ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = id == SettingsId ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = id == SettingsId ? Visibility.Collapsed : Visibility.Visible;
        TabBar.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;

        // Teleprompter controls the notch background opacity while it is on screen.
        Shape.Opacity = id == "teleprompter" ? App.Settings.PrompterOpacity : 1;

        SetToolSize(w, h, showTabs);

        if (_expanded)
        {
            if (view is IToolView shown) shown.OnShown();
            if (switched) SlideIn(ToolHost, Math.Sign(newIndex - oldIndex));
        }

        foreach (RadioButton tab in Tabs.Children)
            if ((string)tab.Tag == id && tab.IsChecked != true) tab.IsChecked = true;
        MoveTabIndicator(id);
    }

    /// <summary>Tools call this to grow/shrink the notch live (e.g. teleprompter resize handle).</summary>
    public void SetToolSize(double w, double h, bool? showTabs = null)
    {
        _toolW = w;
        _toolH = h;
        var tabs = showTabs ?? TabBar.Visibility == Visibility.Visible;
        ToolHost.Width = w;
        ToolHost.Height = h;
        _expandedH = HeaderH + h + (tabs ? TabBarH : SidePad);
        if (!_expanded) return;
        _width.Target = w + 2 * SidePad;
        _height.Target = _expandedH;
        StartAnimation();
    }

    /// <summary>Background opacity for the notch silhouette (teleprompter "see-through" mode).</summary>
    public void SetBackgroundOpacity(double opacity) => Shape.Opacity = Math.Clamp(opacity, 0.15, 1);

    private int TabIndex(string id)
    {
        for (var i = 0; i < Tabs.Children.Count; i++)
            if ((string)((RadioButton)Tabs.Children[i]).Tag == id) return i;
        return -1;
    }

    private void MoveTabIndicator(string id, bool animate = true)
    {
        var tab = Tabs.Children.OfType<RadioButton>().FirstOrDefault(t => (string)t.Tag == id);
        if (tab is null || tab.ActualWidth <= 0 || !TabBar.IsVisible)
        {
            TabIndicator.Opacity = 0; // re-snapped by Tabs.LayoutUpdated once laid out
            return;
        }

        var x = tab.TranslatePoint(new Point(0, 0), TabStrip).X;
        if (!animate || TabIndicator.Opacity < 0.5)
        {
            TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorShift.X = x;
            TabIndicator.Opacity = 1;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    /// <summary>New content slides in from the side of the tab it came from and fades up.</summary>
    private static void SlideIn(UIElement element, int direction)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        if (direction == 0)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        else
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18 * direction, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    private void OnSettingsChanged()
    {
        ApplyNotchSize();
        BuildTabs();
        Place();
    }

    // ---------------------------------------------------------------- drag & drop → shelf

    private void OnPillDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(DataFormats.UnicodeText)) return;
        if (!_expanded || _activeTool != "shelf")
        {
            Expand(activate: false);
            ShowTool("shelf");
        }
    }

    private void OnPillDrop(object sender, DragEventArgs e)
    {
        if (_activeTool is "teleprompter" or "ask" && e.Data.GetDataPresent(DataFormats.UnicodeText)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            ShelfService.Instance.AddFiles(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } text)
            ShelfService.Instance.AddText(text);
        else
            return;

        ShowTool("shelf");
        e.Handled = true;
    }
}
