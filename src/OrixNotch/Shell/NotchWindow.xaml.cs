using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // Dodge: the closed notch runs from a pointer coming at it from the left or right, always
    // keeping DodgeGap away from it, and drifts home when the pointer backs off. It can only be
    // caught from below. The whole window slides (the notch can cross the screen; the window
    // itself stays small). Stiff, slightly under-damped spring: it keeps up with a quick flick
    // and wobbles a little when it stops. Smoothness: while the pointer is near, the dodge runs
    // on every render frame (vsync), and only the window moves (DWM composites that for free);
    // the notch's own pixels aren't redrawn, so the motion is as smooth as the pointer itself.
    private const double DodgeGap = 28;      // DIPs kept between the pointer and the notch's side
    private const double DodgeBand = 24;     // DIPs below the notch that still count as "beside" it
    private const double DodgeEdge = 6;      // DIPs kept clear of the screen edges
    private readonly Spring _shove = new(0) { Stiffness = 900, Damping = 40 };
    private readonly DispatcherTimer _repelTimer = new() { Interval = TimeSpan.FromMilliseconds(40) }; // idle watch only
    private double _dodgeSpeed;  // smoothed escape speed (DIP/s), handed to the spring as momentum
    private double _pointerX = double.NaN, _pointerSpeed; // last pointer x (DIP from home) and its smoothed speed
    private (double W, double H, double Ear, double Radius, double P, double Peek, bool Peeking, bool Expanded) _shapeDrawn;
    private bool _dodging;       // the pointer came from a side; keep running until it leaves the band
    private int _dodgeSide;      // -1: pointer is on the notch's left, +1: on its right (kept while it overlaps mid-flick)
    private int _homeX, _homeY;  // window position (px) with the notch centred
    private int _shovePx;        // offset currently applied to the window (px)
    private double _maxShove = 600; // DIPs the notch may travel each way before hitting the screen edge
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
    private bool _typedInFocus; // the user has typed into the focused field (auto-focus alone doesn't hold the notch open)

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


    public NotchWindow()
    {
        InitializeComponent();
        Instance = this;
        _activeTool = App.Settings.LastTool;
        ApplyNotchSize();

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            // Resting on the reminder's dismiss button shouldn't pop the notch open under it.
            if (Pill.IsMouseOver && !_expanded && !_peeking && !ReminderDismiss.IsMouseOver) Expand(activate: false);
        };
        _leaveTimer.Tick += (_, _) => CheckLeave();
        _repelTimer.Tick += (_, _) =>
        {
            if (!(_dodging && _animating)) Repel(_repelTimer.Interval.TotalSeconds); // the frame loop owns it while dodging
        };
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
        PreviewGotKeyboardFocus += (_, _) => _typedInFocus = false;
        PreviewTextInput += (_, _) => _typedInFocus = true;
        Deactivated += (_, _) =>
        {
            if (_expanded && !_pinned && _holdOpen == 0 && !IsCursorOverPill() && Mouse.Captured is null) Collapse();
        };

        NowPlayingService.Instance.PropertyChanged += OnActivityChanged;
        PomodoroService.Instance.PropertyChanged += OnActivityChanged;
        CountdownService.Instance.PropertyChanged += OnActivityChanged;
        StopwatchService.Instance.PropertyChanged += OnActivityChanged;
        NotchActivityService.Instance.PropertyChanged += (_, _) => UpdateActivity();
        EventReminderService.Instance.Alert += _ => ShowReminderPeek();
        NotificationWatcher.Instance.Arrived += ShowNotificationPeek;
        IdleInfoService.Instance.Changed += OnIdleChanged;
        _idleRotate.Tick += (_, _) => OnIdleRotate();
        ProfileService.Changed += () => _idleAvatarLoaded = false;
        _peekTimer.Tick += (_, _) => EndPeek();
        // A reminder already due at launch fired its alert before we subscribed: show it once we're up.
        Dispatcher.BeginInvoke(ShowReminderPeek, DispatcherPriority.ContextIdle);
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
        _repelTimer.Start();
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
        (_homeX, _homeY) = (x, y);
        _maxShove = screen.Bounds.Width / dpi.DpiScaleX / 2;
        _shovePx = (int)Math.Round(_shove.Value * dpi.DpiScaleX);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x + _shovePx, y, w, h, Win32.SWP_NOACTIVATE);
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
            if (_peeking)
            {
                _peeking = false;
                _currentPeek = null;
                _peekTimer.Stop();
                PeekLayer.Visibility = Visibility.Collapsed;
            }
            _expanded = true;
            _idleRotate.Stop();
            _repelTimer.Stop();
            SetShove(0);
            if (EventReminderService.Instance.IsActive) ShowTool("calendar");
            else ShowTool(_activeTool == SettingsId ? App.Settings.LastTool : _activeTool);
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
        _repelTimer.Start();
        _height.Target = _collapsedH;
        _width.Target = ActivityWidth();
        if (ToolHost.Content is IToolView view) view.OnHidden();
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
        StartAnimation();
        UpdateActivity();
        // Alerts that arrived while the notch was open play once it has closed.
        if (_peekQueue.Count > 0)
        {
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                if (!_peeking) ShowNextPeek();
            };
            later.Start();
        }
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

    /// <summary>An editable field in the notch has keyboard focus (read-only chat text doesn't count).</summary>
    private bool IsEditing() => IsActive && Keyboard.FocusedElement is (TextBox { IsReadOnly: false } or PasswordBox)
        && ((Visual)Keyboard.FocusedElement).IsDescendantOf(Pill);

    /// <summary>Holds the notch open: the user is mid-edit in a field, not just a field that was focused for them.</summary>
    private bool IsTyping() => IsEditing() && _typedInFocus;

    /// <summary>
    /// Polled (the transparent window gets no mouse events beside the pill). A pointer that comes
    /// at the closed notch from the left or right is out-run: the notch slides so its near side
    /// stays <see cref="DodgeGap"/> ahead of the pointer, follows it back toward home as it
    /// retreats, and stops at the screen edge. Coming from below is how you catch it: a pointer
    /// that reaches the notch without approaching from a side doesn't move it, so hover-open works.
    /// </summary>
    /// <returns>True while the pointer is being dodged (keeps the per-frame loop running).</returns>
    private bool Repel(double dt)
    {
        if (!App.Settings.RepelCursor || _expanded || _peeking || !IsVisible || !SystemParameters.ClientAreaAnimation
            || (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0 // mid-drag: dropping onto the notch must stay easy
            || !Win32.GetCursorPos(out var p) || _hwnd == IntPtr.Zero)
        {
            _dodging = false;
            SetShove(0);
            return false;
        }

        // Pointer relative to the notch's home centre, in DIPs, from screen coordinates: the window
        // is moving under us, and its cached position (PointFromScreen) can trail the last move.
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (p.X - (_homeX + Width * dpi.DpiScaleX / 2)) / dpi.DpiScaleX;
        var y = (p.Y - _homeY) / dpi.DpiScaleY;
        var half = Body.ActualWidth / 2;
        var inBand = y >= -8 && y <= Body.ActualHeight + DodgeBand;
        var beside = Math.Abs(x - _shove.Value) > half;

        if (!inBand)
        {
            _pointerX = double.NaN;
            _pointerSpeed = 0;
            _dodging = false;
            SetShove(0);
            return false;
        }
        if (beside)
        {
            _dodging = true;
            // Only decided while the pointer is clear of the notch: a flick that overlaps it
            // mustn't flip the direction and send the notch back through the pointer.
            _dodgeSide = x < _shove.Value ? -1 : 1;
        }
        // Pointer speed, smoothed: used to aim a frame ahead, since the window lands a frame
        // after we read the pointer and a fast pointer would otherwise eat into the gap.
        if (!double.IsNaN(_pointerX))
            _pointerSpeed += ((x - _pointerX) / Math.Max(dt, 1 / 240.0) - _pointerSpeed) * 0.5;
        _pointerX = x;

        if (!_dodging) return false; // reached it from below: hold still and let it be caught
        StartAnimation(); // from here on, follow the pointer every frame

        // Keep the near side DodgeGap past the pointer; drift home once the pointer is far enough away.
        var limit = Math.Max(0, _maxShove - half - DodgeEdge);
        // Lead only in the direction of the chase (never toward the pointer).
        var lead = Math.Clamp(_pointerSpeed * 0.022, -60, 60);
        var aimX = _dodgeSide < 0 ? x + Math.Max(0, lead) : x + Math.Min(0, lead);
        var target = DodgeTarget(aimX, half);
        if (Math.Abs(target) > limit)
        {
            // Cornered against the screen edge: hop over the pointer to its other side in one
            // frame (sliding would pass under it) and fade back in there, then run home.
            _dodgeSide = -_dodgeSide;
            _shove.Value = Math.Clamp(DodgeTarget(x, half, toward: false), -limit, limit);
            _shove.Velocity = 0;
            _dodgeSpeed = 0;
            target = DodgeTarget(x, half);
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Timeline.SetDesiredFrameRate(fadeIn, 60);
            Pill.BeginAnimation(OpacityProperty, fadeIn);
        }
        target = Math.Clamp(target, -limit, limit);

        // Running away can't lag: a spring chasing a moving target trails it, and a quick pointer
        // would catch up. Jump straight to the escape position and carry the pointer's speed, so
        // the spring only adds the overshoot and wobble once the pointer stops.
        if (_dodgeSide < 0 ? target > _shove.Value : target < _shove.Value)
        {
            // Momentum = smoothed pointer speed, so the stop-and-settle doesn't jerk with one noisy frame.
            var speed = Math.Clamp((target - _shove.Value) / Math.Max(dt, 1 / 240.0), -2000, 2000);
            _dodgeSpeed += (speed - _dodgeSpeed) * 0.35;
            _shove.Value = target;
            _shove.Velocity = _dodgeSpeed * 0.4;
            _shove.Target = target;
            return true;
        }
        _dodgeSpeed *= 0.8;
        SetShove(target);
        return true;
    }

    /// <summary>
    /// Where the notch's centre goes to stay <see cref="DodgeGap"/> clear of a pointer at
    /// <paramref name="x"/> on the current dodge side (0 = home, once the pointer is far enough
    /// away). With <paramref name="toward"/> false: the exact spot on that side, even past home.
    /// </summary>
    private double DodgeTarget(double x, double half, bool toward = true)
    {
        var spot = _dodgeSide < 0 ? x + half + DodgeGap : x - half - DodgeGap;
        if (!toward) return spot;
        return _dodgeSide < 0 ? Math.Max(0, spot) : Math.Min(0, spot);
    }

    private void SetShove(double target)
    {
        if (Math.Abs(_shove.Target - target) < 0.5 && !(target == 0 && _shove.Target != 0)) return;
        _shove.Target = target;
        StartAnimation();
    }

    /// <summary>Slides the whole window <paramref name="dip"/> DIPs from its centred home position.</summary>
    private void MoveWindowBy(double dip)
    {
        if (_hwnd == IntPtr.Zero) return;
        var px = (int)Math.Round(dip * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (px == _shovePx) return;
        _shovePx = px;
        // Position only: no z-order, activation, size or owner-redraw work, so a move costs DWM a recomposite and nothing more.
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, _homeX + px, _homeY, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOREDRAW);
    }

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
        if (e.Key is not (Key.Escape or Key.Tab or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl))
            _typedInFocus |= IsEditing(); // Backspace, Delete, arrows… count as typing too
        if (e.Key == Key.Escape)
        {
            // Esc unwinds one step at a time: the tool's own state (e.g. an open editor),
            // then a filled search field, then back/collapse.
            if (ToolHost.Content is IEscapeHandler handler && handler.OnEscape())
            {
                e.Handled = true;
                return;
            }
            if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } field && Field.GetClearable(field) && field.Text.Length > 0)
            {
                field.Clear();
                e.Handled = true;
                return;
            }
            if (_backTarget is not null) GoBack();
            else Collapse();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox || _activeTool == SettingsId) return;

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

        var moving = _dodging && Repel(dt);

        // Sub-step for stability on slow frames.
        var steps = (int)Math.Ceiling(dt / (1 / 240.0));
        var sub = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            moving |= _width.Step(sub);
            moving |= _height.Step(sub);
            moving |= _shove.Step(sub);
        }

        ApplyGeometry();
        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    private void ApplyGeometry()
    {
        var w = Math.Max(60, _width.Value);
        var h = Math.Max(20, _height.Value);

        // 0 = collapsed, 1 = fully open (to whatever size the current tool wants).
        // While an alert is showing the notch only grows to PeekH: track that separately.
        var peek = _peeking && !_expanded ? Math.Clamp((h - _collapsedH) / (PeekH - _collapsedH), 0, 1) : 0;
        var p = peek > 0 || (_peeking && !_expanded) ? 0 : Math.Clamp((h - _collapsedH) / Math.Max(1, _expandedH - _collapsedH), 0, 1);
        var ear = EarCollapsed + (EarExpanded - EarCollapsed) * p;
        var radius = RadiusCollapsed + (RadiusExpanded - RadiusCollapsed) * p + (RadiusPeek - RadiusCollapsed) * peek;

        MoveWindowBy(_shove.Value);

        // A pure slide changes nothing inside the window: skip the visual updates so WPF doesn't
        // re-render (and re-upload) the whole transparent window every frame.
        var shape = (w, h, ear, radius, p, peek, _peeking, _expanded);
        if (shape == _shapeDrawn) return;
        _shapeDrawn = shape;

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

        CollapsedLayer.Opacity = Math.Clamp(1 - p * 4 - peek * 3, 0, 1);
        CollapsedLayer.Visibility = p < 0.25 && peek < 0.34 ? Visibility.Visible : Visibility.Collapsed;
        PeekLayer.Opacity = Math.Clamp((peek - 0.5) * 2, 0, 1);
        PeekLayer.Visibility = _peeking && peek > 0.5 ? Visibility.Visible : Visibility.Collapsed;
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
    private bool _reminderPulsing;

    /// <summary>Closed-notch width for the current state: compact for timers and reminders, wide for track text.</summary>
    private double ActivityWidth()
    {
        if (EventReminderService.Instance.IsActive) return _timerW;
        var timer = PomodoroService.Instance.IsRunning
            || CountdownService.Instance.IsRunning
            || StopwatchService.Instance.IsRunning;
        if (timer) return _timerW;
        var music = NowPlayingService.Instance.HasSession && NowPlayingService.Instance.IsPlaying;
        return music ? _musicW : IdleActive ? IdleWidth() : _idleW;
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingService.IsPlaying) or nameof(NowPlayingService.HasSession)
            or nameof(NowPlayingService.Title) or nameof(NowPlayingService.Artist) or nameof(NowPlayingService.ArtTint)
            or nameof(PomodoroService.IsRunning) or nameof(CountdownService.IsRunning) or nameof(StopwatchService.IsRunning))
            UpdateActivity();
    }

    private void UpdateActivity()
    {
        var act = NotchActivityService.Instance;
        act.Refresh();
        var kind = act.Kind;
        var music = kind == NotchActivityKind.Music;
        var reminder = kind == NotchActivityKind.Reminder;
        var timer = kind is NotchActivityKind.Timer or NotchActivityKind.Stopwatch or NotchActivityKind.Focus or NotchActivityKind.Break;

        MusicPart.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        TimerPart.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        ReminderPart.Visibility = reminder ? Visibility.Visible : Visibility.Collapsed;
        var idle = kind == NotchActivityKind.None && IdleInfoService.Instance.Items.Count > 0;
        IdlePart.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        if (idle) RenderIdle();
        UpdateIdleRotation();

        if (music)
        {
            var np = NowPlayingService.Instance;
            TrackTitle.Text = string.IsNullOrWhiteSpace(np.Title) ? "Playing" : np.Title;
            TrackArtist.Text = string.IsNullOrWhiteSpace(np.Artist) ? "" : "  " + np.Artist;
            var bars = new SolidColorBrush(WaveColor(np.ArtTint));
            bars.Freeze();
            foreach (var bar in new[] { Bar1, Bar2, Bar3, Bar4, Bar5 }) bar.Fill = bars;
            Dispatcher.BeginInvoke(UpdateMarquee, DispatcherPriority.Loaded);
        }
        else
        {
            TrackShift.BeginAnimation(TranslateTransform.XProperty, null);
        }

        if (timer)
        {
            var brush = kind == NotchActivityKind.Break ? "GoodBrush" : "AccentBrush";
            MiniTimerGlyph.Kind = kind switch
            {
                NotchActivityKind.Break => AppIcon.Moon,
                NotchActivityKind.Timer or NotchActivityKind.Stopwatch => AppIcon.Clock,
                _ => AppIcon.Bolt,
            };
            MiniTimerGlyph.SetResourceReference(ForegroundProperty, brush);
            MiniArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
            MiniArc.Data = BuildMiniArc(act.Progress);
            TimerText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        }

        // At start time the reminder tile gently pulses so it catches the eye.
        var pulse = reminder && EventReminderService.Instance.IsNow;
        if (pulse != _reminderPulsing)
        {
            _reminderPulsing = pulse;
            ReminderTile.BeginAnimation(OpacityProperty, pulse
                ? new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }
        if (!reminder)
        {
            _peekQueue.RemoveAll(r => r.Key == ReminderPeekKey);
            if (_currentPeek?.Key == ReminderPeekKey) EndPeek();
        }

        SetEqualizer(!_expanded && music);

        if (!_expanded && !_peeking)
        {
            _width.Target = ActivityWidth();
            StartAnimation();
        }
    }

    /// <summary>Waveform colour from the album art: the art's tint, lifted until it reads on black.</summary>
    private Color WaveColor(Color? tint)
    {
        if (tint is not { } c) return ((SolidColorBrush)ThemeService.Get("AccentBrush")).Color;
        static double Lum(Color x) => (0.299 * x.R + 0.587 * x.G + 0.114 * x.B) / 255;
        for (var i = 0; i < 6 && Lum(c) < 0.55; i++)
            c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));
        return c;
    }

    /// <summary>Long track text drifts left to reveal the rest, pauses, and drifts back.</summary>
    private void UpdateMarquee()
    {
        TrackShift.BeginAnimation(TranslateTransform.XProperty, null);
        TrackShift.X = 0;
        TrackText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var overflow = TrackText.DesiredSize.Width - TitleSlot.ActualWidth;
        if (overflow <= 2 || _expanded) return;
        var travel = TimeSpan.FromSeconds(overflow / 28);
        var hold = TimeSpan.FromSeconds(1.6);
        var slide = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(hold + travel), new SineEase()));
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(hold + travel + hold)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold + travel + hold + travel), new SineEase()));
        TrackShift.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void OnDismissReminder(object sender, RoutedEventArgs e)
    {
        _hoverTimer.Stop();
        if (_currentPeek?.Key == ReminderPeekKey) EndPeek();
        EventReminderService.Instance.Dismiss();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- idle items

    private readonly DispatcherTimer _idleRotate = new();
    private int _idleIndex;
    private const double IdleRowMax = 640;

    private bool IdleActive => !NotchActivityService.Instance.HasActivity && IdleInfoService.Instance.Items.Count > 0;

    /// <summary>Closed-notch width for the idle items under the current layout.</summary>
    private double IdleWidth()
    {
        var items = IdleInfoService.Instance.Items;
        if (items.Count == 0) return _idleW;
        switch (App.Settings.IdleLayout)
        {
            case "Row":
                IdleRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return Math.Clamp(IdleRow.DesiredSize.Width + 30, _idleW, IdleRowMax);
            case "Split" when items.Count > 1:
                return _musicW;
            default:
                var current = items[_idleIndex % items.Count];
                var longest = Math.Max(current.Value.Length,
                    IdleWidthTemplates.TryGetValue(TemplateKey(current.Id), out var template) ? template.Length : 0);
                return longest > 9 ? _musicW : _timerW;
        }
    }

    /// <summary>Rebuilds the idle slots from the latest values (cheap; runs once a second).</summary>
    private void RenderIdle()
    {
        var items = IdleInfoService.Instance.Items;
        IdleSlotA.Content = null;
        IdleSlotB.Content = null;
        IdleRow.Children.Clear();
        if (items.Count == 0) return;

        switch (App.Settings.IdleLayout)
        {
            case "Row":
                // Add whole items until the row would outgrow the widest notch; never clip one in half.
                for (var i = 0; i < items.Count; i++)
                {
                    var dot = i > 0 ? IdleDot() : null;
                    var chip = IdleChip(items[i]);
                    if (dot is not null) IdleRow.Children.Add(dot);
                    IdleRow.Children.Add(chip);
                    IdleRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (IdleRow.DesiredSize.Width + 30 > IdleRowMax && i > 0)
                    {
                        IdleRow.Children.Remove(chip);
                        IdleRow.Children.Remove(dot);
                        break;
                    }
                }
                break;
            case "Split" when items.Count > 1:
                IdleSlotA.Content = IdleChip(items[0]);
                var rest = items.Skip(1).ToList();
                IdleSlotB.Content = IdleChip(rest[_idleIndex % rest.Count]);
                break;
            default:
                // Apple-style: glyph in the left ear, value in the right.
                var item = items[_idleIndex % items.Count];
                IdleSlotA.Content = IdleGlyph(item, 15);
                IdleSlotB.Content = IdleValue(item);
                break;
        }
    }

    private void OnIdleChanged()
    {
        if (_expanded || !IdleActive)
        {
            if (!NotchActivityService.Instance.HasActivity && IdleInfoService.Instance.Items.Count == 0) UpdateActivity();
            return;
        }
        if (IdlePart.Visibility != Visibility.Visible)
        {
            UpdateActivity();
            return;
        }
        RenderIdle();
        UpdateIdleRotation();
        if (!_peeking)
        {
            var w = IdleWidth();
            if (Math.Abs(_width.Target - w) > 0.5)
            {
                _width.Target = w;
                StartAnimation();
            }
        }
    }

    /// <summary>Rotation runs only when there is more than one thing to cycle through.</summary>
    private void UpdateIdleRotation()
    {
        var count = IdleInfoService.Instance.Items.Count;
        var cycling = App.Settings.IdleLayout switch
        {
            "Row" => false,
            "Split" => count > 2,
            _ => count > 1,
        };
        var interval = TimeSpan.FromSeconds(Math.Max(2, App.Settings.IdleRotateSec));
        if (cycling && !_expanded && IdleActive)
        {
            if (_idleRotate.Interval != interval) _idleRotate.Interval = interval;
            if (!_idleRotate.IsEnabled) _idleRotate.Start();
        }
        else _idleRotate.Stop();
    }

    private void OnIdleRotate()
    {
        _idleIndex++;
        RenderIdle();
        // New item rises into place and fades in; the right slot trails a beat for a softer change.
        foreach (var (slot, delay) in new[] { (IdleSlotA, 0), (IdleSlotB, 60) })
        {
            if (App.Settings.IdleLayout == "Split" && slot == IdleSlotA) continue; // left item stays put
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(delay);
            slot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
            ((TranslateTransform)slot.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
        }
        if (!_expanded && !_peeking)
        {
            _width.Target = IdleWidth();
            StartAnimation();
        }
    }

    private static ImageSource? _idleAvatar;
    private static bool _idleAvatarLoaded;

    private static ImageSource? IdleAvatar()
    {
        if (!_idleAvatarLoaded)
        {
            _idleAvatar = ProfileService.LoadAvatar();
            _idleAvatarLoaded = true;
        }
        return _idleAvatar;
    }

    private static FrameworkElement IdleGlyph(IdleItem item, double size)
    {
        if (item.Logo is { } logo) return BrandLogos.Create(logo, size); // the AI tool's real mark, in its own colours
        if (item.Avatar)
        {
            var grid = new Grid { Width = size + 5, Height = size + 5, VerticalAlignment = VerticalAlignment.Center };
            var disc = new Ellipse();
            disc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentSoftBrush");
            grid.Children.Add(disc);
            var initial = new TextBlock
            {
                Text = ProfileService.FirstName[..1].ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            initial.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            grid.Children.Add(initial);
            if (IdleAvatar() is { } image)
                grid.Children.Add(new Ellipse { Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } });
            return grid;
        }
        var icon = new HeroIcon { Kind = item.Glyph ?? AppIcon.Info, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, item.ValueBrush == "TextBrush" ? "SubTextBrush" : item.ValueBrush);
        return icon;
    }

    /// <summary>
    /// Widest value each live-changing item can show (digits are tabular, so "0" stands for any digit).
    /// Values get at least this much room, so the notch doesn't twitch as numbers tick.
    /// </summary>
    private static readonly Dictionary<string, string> IdleWidthTemplates = new()
    {
        ["network"] = "00.0 MB/s",
        ["cpu"] = "100% · 100%",
        ["clock"] = "00:00 PM",
        ["battery"] = "100%",
        ["todos"] = "00 open",
        ["streak"] = "00 today",
        ["clipboard"] = "000 clips",
        ["shelf"] = "00 files",
        ["aiUsage"] = "Antigravity 100%",
        ["weather"] = "-00°",
    };

    private static readonly Dictionary<(string, double), double> IdleWidthCache = new();

    /// <summary>Per-tool AI items ("aiUsage:cursor") share the AI usage template.</summary>
    private static string TemplateKey(string id) => id.StartsWith("aiUsage", StringComparison.Ordinal) ? "aiUsage" : id;

    /// <summary>Reserved width for an item's value at a font size (0 = no reservation).</summary>
    private static double IdleReservedWidth(IdleItem item, double fontSize)
    {
        if (!IdleWidthTemplates.TryGetValue(TemplateKey(item.Id), out var template)) return 0;
        if (IdleWidthCache.TryGetValue((template, fontSize), out var cached)) return cached;
        var probe = new TextBlock { Text = template, FontSize = fontSize, FontWeight = FontWeights.SemiBold };
        probe.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        System.Windows.Documents.Typography.SetNumeralAlignment(probe, FontNumeralAlignment.Tabular);
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(probe.DesiredSize.Width) + 1;
        IdleWidthCache[(template, fontSize)] = width;
        return width;
    }

    private static TextBlock IdleValue(IdleItem item, double fontSize = 13.5, TextAlignment align = TextAlignment.Right)
    {
        var text = new TextBlock
        {
            Text = item.Value,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = align,
            MinWidth = IdleReservedWidth(item, fontSize),
            MaxWidth = 230,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        text.SetResourceReference(TextBlock.ForegroundProperty, item.ValueBrush);
        return text;
    }

    /// <summary>Glyph + value side by side (split and row layouts).</summary>
    private static StackPanel IdleChip(IdleItem item)
    {
        var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        chip.Children.Add(IdleGlyph(item, 13));
        var value = IdleValue(item, 13, TextAlignment.Left);
        value.Margin = new Thickness(6, 0, 0, 0);
        value.MaxWidth = 160;
        chip.Children.Add(value);
        return chip;
    }

    private static FrameworkElement IdleDot()
    {
        var dot = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TrackBrush");
        return dot;
    }

    // ---------------------------------------------------------------- alert (peek)

    private const double PeekH = 76;
    private const double RadiusPeek = 24;
    private const string ReminderPeekKey = "reminder";
    private bool _peeking;
    private readonly DispatcherTimer _peekTimer = new();

    /// <summary>One alert: what to draw, and what dismiss / click do.</summary>
    private sealed record PeekRequest(string Key, Func<FrameworkElement> Icon, string Title, string Sub,
        TimeSpan Duration, Action? OnDismiss = null, Action? OnClick = null)
    {
        public DateTime QueuedAt { get; } = DateTime.Now;
    }

    // Reminder alerts and Windows notifications share one queue and show one after another.
    private readonly List<PeekRequest> _peekQueue = new();
    private PeekRequest? _currentPeek;

    private double PeekW => Math.Max(_musicW + 40, 400);

    private void EnqueuePeek(PeekRequest request)
    {
        // A newer alert with the same key (same app / same reminder) replaces the waiting one.
        _peekQueue.RemoveAll(r => r.Key == request.Key);
        _peekQueue.Add(request);
        if (_peekQueue.Count > 4) _peekQueue.RemoveAt(0);
        if (!_peeking) ShowNextPeek();
    }

    private void ShowNextPeek()
    {
        if (_expanded || Visibility != Visibility.Visible) return; // stays queued until the notch is closed again
        _peekQueue.RemoveAll(r => DateTime.Now - r.QueuedAt > TimeSpan.FromMinutes(1)); // stale while the notch was open
        if (_peekQueue.Count == 0) return;
        var next = _peekQueue[0];
        _peekQueue.RemoveAt(0);

        _currentPeek = next;
        PeekIconHost.Content = next.Icon();
        PeekTitle.Text = next.Title;
        PeekSub.Text = next.Sub;
        PeekSub.Visibility = string.IsNullOrWhiteSpace(next.Sub) ? Visibility.Collapsed : Visibility.Visible;
        if (_peeking)
        {
            // Already open: swap the content with a quick fade instead of shrinking and growing again.
            PeekLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)));
        }
        _peeking = true;
        PeekLayer.Height = PeekH;
        _peekTimer.Stop();
        _peekTimer.Interval = next.Duration;
        _peekTimer.Start();
        _width.Target = PeekW;
        _height.Target = PeekH;
        StartAnimation();
    }

    /// <summary>Ends the current alert; the next queued one (if any) takes its place.</summary>
    private void EndPeek()
    {
        _peekTimer.Stop();
        if (!_peeking) return;
        _currentPeek = null;
        if (!_expanded && _peekQueue.Count > 0)
        {
            ShowNextPeek();
            if (_currentPeek is not null) return;
        }
        _peeking = false;
        PeekLayer.BeginAnimation(OpacityProperty, null);
        PeekLayer.Visibility = Visibility.Collapsed;
        if (_expanded) return;
        _width.Target = ActivityWidth();
        _height.Target = _collapsedH;
        StartAnimation();
    }

    private void OnPeekDismiss(object sender, RoutedEventArgs e)
    {
        _hoverTimer.Stop();
        var dismiss = _currentPeek?.OnDismiss;
        EndPeek();
        dismiss?.Invoke();
        e.Handled = true;
    }

    private void OnPeekClick(object sender, MouseButtonEventArgs e)
    {
        var click = _currentPeek?.OnClick;
        EndPeek();
        click?.Invoke();
        e.Handled = true;
    }

    private void ShowReminderPeek()
    {
        var r = EventReminderService.Instance;
        if (!r.IsActive) return;
        EnqueuePeek(new PeekRequest(ReminderPeekKey, () => GlyphTile(AppIcon.CalendarDays), r.Title, r.SubText,
            TimeSpan.FromSeconds(5),
            OnDismiss: () => EventReminderService.Instance.Dismiss(),
            OnClick: () => Expand(activate: true))); // a live reminder routes this to Calendar
    }

    private void ShowNotificationPeek(NotchNotification n)
    {
        var sub = string.IsNullOrWhiteSpace(n.Body) ? n.AppName : $"{n.AppName} · {n.Body}";
        EnqueuePeek(new PeekRequest("app:" + n.AppId, () => AppTile(n), n.Title, sub, TimeSpan.FromSeconds(4)));
    }

    /// <summary>Accent-soft rounded tile with a glyph (reminders).</summary>
    private static FrameworkElement GlyphTile(AppIcon kind)
    {
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11) };
        tile.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var icon = new HeroIcon { Kind = kind, Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, "AccentBrush");
        tile.Child = icon;
        return tile;
    }

    /// <summary>The sending app's own icon, or its first letter on a tile when Windows has none.</summary>
    private static FrameworkElement AppTile(NotchNotification n)
    {
        if (n.Icon is not null)
        {
            var image = new Image { Source = n.Icon, Width = 34, Height = 34, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var frame = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Child = image };
            frame.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            return frame;
        }
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11) };
        tile.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        var letter = new TextBlock
        {
            Text = n.AppName.Length > 0 ? n.AppName[..1].ToUpperInvariant() : "?",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        letter.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        tile.Child = letter;
        return tile;
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="progress"/> of the mini ring.</summary>
    private static Geometry BuildMiniArc(double progress)
    {
        const double size = 22, inset = 2.5;
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
        // Bars breathe around their centre like a voice waveform, each at its own pace.
        var bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5 };
        var periods = new[] { 0.46, 0.33, 0.52, 0.38, 0.6 };
        var lows = new[] { 0.3, 0.45, 0.25, 0.4, 0.3 };
        for (var i = 0; i < bars.Length; i++)
        {
            var anim = new DoubleAnimation(lows[i], 1.0, TimeSpan.FromSeconds(periods[i]))
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
        // Tabs reordered while the bar was hidden (e.g. from Settings) still report their old
        // positions until the next layout pass; measure now so the indicator lands on the right tab.
        if (tab is not null && TabBar.IsVisible && !Tabs.IsArrangeValid) Tabs.UpdateLayout();
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
