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
    // In DIPs (see docs/ui-measurements.md):
    // open panel 726×323, header 56, tab bar 64, 18 side padding.
    private const double HeaderH = 56;
    private const double TabBarH = 64;
    private const double SidePad = 18;
    /// <summary>Gap kept below the tallest notch so it never touches the screen's bottom edge.</summary>
    private const double ScreenMargin = 48;
    /// <summary>Spare transparent room under the notch inside the window (stroke, rounding).</summary>
    private const double WindowSlack = 16;

    // Silhouette: concave "ears" where the notch meets the top edge, rounded bottom corners
    private const double EarCollapsed = 5;
    private const double EarExpanded = 7;
    private const double RadiusCollapsed = 10;
    private const double RadiusExpanded = 26;

    /// <summary>Island and Pill float this far from the screen edge; Notch and Tab touch it.</summary>
    private const double IslandGap = 8;

    private static bool Floating => App.Settings.NotchShape is "Island" or "Pill";

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
            UpdateConceal();
            FollowPointerScreen();
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
        ThemeService.FollowAlbumArt();
        ThemeService.WatchWallpaper();
        PomodoroService.Instance.PropertyChanged += OnActivityChanged;
        CountdownService.Instance.PropertyChanged += OnActivityChanged;
        StopwatchService.Instance.PropertyChanged += OnActivityChanged;
        NotchActivityService.Instance.PropertyChanged += (_, _) => UpdateActivity();
        EventReminderService.Instance.Alert += _ => ShowReminderPeek();
        NotificationWatcher.Instance.Arrived += ShowNotificationPeek;
        AiLimitWatcher.Instance.Alert += ShowAiLimitPeek;
        AiLimitWatcher.Instance.Reconfigure();
        PhoneTransferService.Instance.Received += ShowPhoneReceivedPeek;
        UpdateService.Instance.Available += ShowUpdatePeek;
        PowerWatcher.Instance.Alert += ShowPowerPeek;
        VolumeService.Instance.Changed += ShowVolumePeek;
        VolumeService.Instance.MicChanged += muted =>
            EnqueuePeek(new PeekRequest("mic", () => GlyphTile(muted ? AppIcon.MicrophoneOff : AppIcon.Microphone),
                muted ? "Microphone muted" : "Microphone on", muted ? "No app can hear you" : "", TimeSpan.FromSeconds(2)));
        KeepAwakeService.Changed += on =>
            EnqueuePeek(new PeekRequest("awake", () => GlyphTile(AppIcon.Coffee),
                on ? "Keep awake on" : "Keep awake off", on ? "The PC and screen won't sleep" : "", TimeSpan.FromSeconds(2)));
        BluetoothWatcher.Instance.Changed += ShowBluetoothPeek;
        BluetoothWatcher.Instance.Battery += (device, level) =>
        {
            // Only refresh the connect alert still on screen or waiting; a late reading shouldn't pop a new one.
            var key = "bt:" + device;
            if (!App.Settings.BluetoothAlerts || (_currentPeek?.Key != key && _peekQueue.All(r => r.Key != key))) return;
            EnqueuePeek(new PeekRequest(key, () => GlyphTile(AppIcon.Bluetooth), device, $"Connected · {level}% battery",
                TimeSpan.FromSeconds(3)));
        };
        DownloadWatcher.Instance.Finished += ShowDownloadPeek;
        ScreenshotWatcher.Instance.Taken += ShowScreenshotPeek;
        AutomationService.Instance.Acted += (title, sub) =>
            EnqueuePeek(new PeekRequest("automation", () => GlyphTile(AppIcon.Bolt), title, sub, TimeSpan.FromSeconds(4)));
        SubscriptionService.Instance.Renewing += (sub, days) =>
        {
            if (!App.Settings.SubscriptionAlerts) return;
            EnqueuePeek(new PeekRequest("sub:" + sub.Id, () => GlyphTile(AppIcon.CreditCard),
                days == 0 ? $"{sub.Name} renews today" : days == 1 ? $"{sub.Name} renews tomorrow" : $"{sub.Name} renews in {days} days",
                $"{sub.PriceLabel} · cancel before then if you don't need it", TimeSpan.FromSeconds(6), OnClick: () => OpenTool("subscriptions")));
        };
        ScreenTimeService.Instance.Summary += (seconds, top) =>
            EnqueuePeek(new PeekRequest("screentime", () => GlyphTile(AppIcon.Hourglass), $"{ScreenTimeService.Format(seconds)} on screen today",
                top is null ? "Click to see your day" : $"Most of it in {top} · click to see your day", TimeSpan.FromSeconds(6), OnClick: ShowWrap));
        PomodoroService.Instance.StreakReached += days =>
        {
            if (!App.Settings.Celebrations) return;
            EnqueuePeek(new PeekRequest("streak", () => GlyphTile(AppIcon.Fire), $"{days}-day focus streak",
                days >= 7 ? "A focus session every day. Impressive. Keep it going!" : "Focus every day, and it adds up. Keep going!", TimeSpan.FromSeconds(6)));
        };
        AutomationService.Instance.Birthday += title =>
            EnqueuePeek(new PeekRequest("birthday:" + title, () => GlyphTile(AppIcon.Cake), title, "Today. Don't forget to say happy birthday!",
                TimeSpan.FromSeconds(8), OnClick: () => OpenTool("calendar")));
        BreakService.Instance.Due += kind =>
            EnqueuePeek(new PeekRequest("break:" + kind.Id, () => GlyphTile(kind.Icon), kind.Title, kind.Sub, TimeSpan.FromSeconds(6)));
        BreakService.Instance.Reconfigure();
        ScreenTimeService.Instance.GoalPassed += (app, minutes) =>
            EnqueuePeek(new PeekRequest("goal:" + app, () => GlyphTile(AppIcon.Hourglass), app,
                $"{CommandRegistry.FormatMinutes(minutes)} today, your daily goal. Maybe take a break?", TimeSpan.FromSeconds(6), OnClick: () => OpenTool("screentime")));
        OcrService.Instance.Done += (title, sub) =>
            EnqueuePeek(new PeekRequest("ocr", () => GlyphTile(AppIcon.DocText), title, sub, TimeSpan.FromSeconds(4)));
        NotificationHistory.Instance.Start();
        IdleInfoService.Instance.Changed += OnIdleChanged;
        _idleRotate.Tick += (_, _) => OnIdleRotate();
        ProfileService.Changed += () => _idleAvatarLoaded = false;
        _peekTimer.Tick += (_, _) => EndPeek();
        _trackTimer.Tick += (_, _) => ShowTrackPeek();
        // A reminder already due at launch fired its alert before we subscribed: show it once we're up.
        Dispatcher.BeginInvoke(ShowReminderPeek, DispatcherPriority.ContextIdle);
        Dispatcher.BeginInvoke(ShowWhatsNewPeek, DispatcherPriority.ContextIdle);
        SettingsService.Changed += OnSettingsChanged;
        HotkeyService.Triggered += OnHotkey;

        BuildEqualizer();
        MakeRound(PeekDismiss);
        TabBar.RenderTransform = _tabRise;
        NowPlayingService.Instance.PropertyChanged += (_, _) => UpdateGlow();
        PomodoroService.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PomodoroService.IsRunning)) UpdateGlow(); };
        ThemeService.Changed += () => { _glowColor = null; UpdateGlow(); };
        _eqLive.Tick += (_, _) => TickEqualizer();
        Tabs.LayoutUpdated += (_, _) =>
        {
            if (TabIndicator.Opacity < 0.5 && _expanded && _activeTool is not (SettingsId or PaletteId or BriefId or WrapId)) MoveTabIndicator(_activeTool, animate: false);
        };
        WireTabScroll();
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
        ThemeService.Changed += ApplyGlass;
        ApplyGlass();
        ApplyCaptureAffinity();
        VolumeService.Instance.Start();
        BluetoothWatcher.Instance.Start();
        DownloadWatcher.Instance.Start();
        ScreenshotWatcher.Instance.Start();
        ScreenTimeService.Instance.Start();
        AutomationService.Instance.Start();
        Dispatcher.BeginInvoke(SubscriptionService.Instance.Start, DispatcherPriority.ContextIdle); // its alerts once the notch is up
        ForegroundAppWatcher.Instance.Changed += OnForegroundAppChanged;
        ForegroundAppWatcher.Instance.Start();
        // Morning brief: a few seconds after the first unlock of the day, or after starting at sign-in.
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock) Dispatcher.BeginInvoke(() => After(TimeSpan.FromSeconds(3), TryMorningBrief));
        };
        After(TimeSpan.FromSeconds(20), TryMorningBrief);
        _guardTimer.Start();
        _repelTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        HotkeyService.UnregisterAll();
        ForegroundAppWatcher.Instance.Stop();
        Win32.RemoveClipboardFormatListener(_hwnd);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_CLIPBOARDUPDATE:
                OcrService.Instance.OnClipboardUpdate();
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
            case "mic":
                VolumeService.Instance.ToggleMic();
                break;
            case "awake":
                KeepAwakeService.Toggle();
                break;
            case "ocr":
                OcrService.Instance.Start();
                break;
            case "focus":
                if (PomodoroService.Instance.Focusing) PomodoroService.Instance.ToggleCommand.Execute(null); // pause
                else PomodoroService.Instance.StartFocus();
                break;
            case "explain":
                _ = ExplainSelectionAsync();
                break;
            case "palette":
                if (_expanded && _activeTool == PaletteId) Collapse();
                else ShowPalette();
                break;
            default:
                OpenTool(action);
                break;
        }
    }

    private static Forms.Screen CurrentScreen()
    {
        var screens = Forms.Screen.AllScreens;
        var index = App.Settings.MonitorIndex;
        if (index == -2) return Forms.Screen.FromPoint(Forms.Cursor.Position);
        return index >= 0 && index < screens.Length ? screens[index] : Forms.Screen.PrimaryScreen ?? screens[0];
    }

    private string? _placedOn; // device name of the display the notch sits on

    /// <summary>Settings → Display "Follow the pointer": the closed notch hops to the display the pointer moves to.</summary>
    private void FollowPointerScreen()
    {
        if (App.Settings.MonitorIndex != -2 || _expanded || _peeking) return;
        if (Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName != _placedOn) Place();
    }

    /// <summary>Pins the window to the top-center of the chosen monitor (physical pixels).</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var screen = CurrentScreen();
        _placedOn = screen.DeviceName;
        var dpi = VisualTreeHelper.GetDpi(this);
        var (x, y, w, h, maxShove) = EdgePlacement(screen, dpi);
        (_homeX, _homeY) = (x, y);
        _maxShove = maxShove;
        ApplyEdgeLayout();
        _shovePx = (int)Math.Round(_shove.Value * (Vertical ? dpi.DpiScaleY : dpi.DpiScaleX));
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x + (Vertical ? 0 : _shovePx), y + (Vertical ? _shovePx : 0), w, h, Win32.SWP_NOACTIVATE);
    }

    /// <summary>Keeps the notch above other topmost windows and out of the way of fullscreen apps.</summary>
    private void Guard()
    {
        if (_hwnd == IntPtr.Zero) return;
        PowerWatcher.Instance.Poll();
        ForegroundAppWatcher.Instance.Recheck(); // raises Changed (which re-enters Guard) only when the app in front changed
        // A full-screen window (video, F11) fades the notch out but the top edge brings it back;
        // an exclusive game hides it outright (a window over it can knock it out of full screen).
        var fullscreen = App.Settings.HideInFullscreen && !_expanded ? Win32.FullscreenKind() : 0;
        _fullscreen = fullscreen == 1;
        _exclusive = fullscreen == 2;
        // A listed app set to "Hide completely" hides it too, except while an alert it lets through is showing.
        var hide = _exclusive || HiddenForApp && App.Settings.HideForAppsMode == "Hidden" && !_peeking;
        var target = hide ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != target) Visibility = target;
        if (!hide)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        CheckQuietEnded();
        if (!_peeking && _peekQueue.Count > 0 && !AlertsHeld()) ShowNextPeek(); // full screen ended: held alerts play now
    }

    // ---------------------------------------------------------------- quiet hours

    private int _quietMissed;
    private bool _wasQuiet;

    /// <summary>Whether <paramref name="now"/> falls in [from, to); a window like 22:00–07:00 crosses midnight.</summary>
    internal static bool InWindow(TimeOnly now, TimeOnly from, TimeOnly to) =>
        from <= to ? now >= from && now < to : now >= from || now < to;

    /// <summary>
    /// Quiet hours are on now: the schedule in Settings → General, the tray's "Quiet for 1 hour", or (Automations)
    /// a call, meaning the microphone is in use.
    /// </summary>
    public static bool QuietActive() => QuietSnoozed() || QuietScheduled() || (App.Settings.AutoQuietOnMic && InCall)
        || (App.Settings.FocusQuiet && PomodoroService.Instance.Focusing);

    /// <summary>Microphone or camera in use. Refreshed by Guard every couple of seconds (reading it is a registry walk).</summary>
    public static bool InCall { get; private set; }

    public static bool QuietSnoozed() => App.Settings.QuietUntil is { } until && until > DateTime.Now;

    public static bool QuietScheduled()
    {
        var s = App.Settings;
        return s.QuietEnabled && TimeOnly.TryParse(s.QuietFrom, out var from) && TimeOnly.TryParse(s.QuietTo, out var to)
            && InWindow(TimeOnly.FromDateTime(DateTime.Now), from, to);
    }

    /// <summary>Tray: quiet for an hour, or end a quiet hour early.</summary>
    public static void ToggleQuietHour()
    {
        App.Settings.QuietUntil = QuietSnoozed() ? null : DateTime.Now.AddHours(1);
        SettingsService.Save(notify: false);
        Instance?.Guard();
    }

    /// <summary>Feedback for something the user just did (volume keys, mic mute) shows even while quiet.</summary>
    private static bool IsUserFeedback(string key) => key is "volume" or "mic" or "awake" or "ocr";

    private bool HeldForQuiet(PeekRequest request)
    {
        if (IsUserFeedback(request.Key) || !QuietActive()) return false;
        if (request.Key == ReminderPeekKey && App.Settings.QuietLetRemindersThrough) return false;
        _quietMissed++;
        return true;
    }

    private void CheckQuietEnded()
    {
        InCall = SystemToggles.InCall();
        UpdateGlow();
        var quiet = QuietActive();
        if (_wasQuiet && !quiet && _quietMissed > 0)
        {
            var n = _quietMissed;
            _quietMissed = 0;
            EnqueuePeek(new PeekRequest("quiet", () => GlyphTile(AppIcon.Moon),
                n == 1 ? "1 alert while you were quiet" : $"{n} alerts while you were quiet", "Click to see notifications",
                TimeSpan.FromSeconds(6), OnClick: () => OpenTool("notifications")));
        }
        if (_wasQuiet && !quiet) ShowWhatsNewPeek();
        _wasQuiet = quiet;
    }

#if DEBUG
    static NotchWindow()
    {
        static TimeOnly T(int h, int m = 0) => new(h, m);
        System.Diagnostics.Debug.Assert(InWindow(T(23), T(22), T(7)));
        System.Diagnostics.Debug.Assert(InWindow(T(3), T(22), T(7)));
        System.Diagnostics.Debug.Assert(!InWindow(T(7), T(22), T(7)));
        System.Diagnostics.Debug.Assert(!InWindow(T(12), T(22), T(7)));
        System.Diagnostics.Debug.Assert(InWindow(T(13), T(13), T(14)));
        System.Diagnostics.Debug.Assert(!InWindow(T(14), T(13), T(14)));
        System.Diagnostics.Debug.Assert(!InWindow(T(9), T(9), T(9))); // equal ends: no window
    }
#endif

    // ---------------------------------------------------------------- auto-hide

    private bool _fullscreen;          // a full-screen window is up (see Guard)
    private bool _exclusive;           // an exclusive full-screen game: the window is hidden outright
    private bool _revealed;            // the pointer touched the top edge over the hidden notch
    private double _opacityTarget = 1;

    /// <summary>One of the apps in Settings → General "Hide for these apps" is in front.</summary>
    private bool HiddenForApp =>
        !_expanded && ForegroundAppWatcher.Instance.Current is { } app && App.Settings.HideForApps.Contains(app);

    /// <summary>
    /// Whether alerts wait in the queue. A listed app's own choice wins over the general full-screen rule;
    /// an exclusive game always holds them (a window over it can knock it out of full screen).
    /// </summary>
    private bool AlertsHeld() =>
        _exclusive || (HiddenForApp ? App.Settings.HideForAppsHoldAlerts : _fullscreen);

    private void OnForegroundAppChanged()
    {
        Guard();
        UpdateConceal();
        if (!_peeking && !AlertsHeld() && _peekQueue.Count > 0) ShowNextPeek(); // held alerts play now
    }

    /// <summary>Whether the closed notch should be faded out now (Settings → Show notch, or full screen).</summary>
    private bool Concealable()
    {
        if (_expanded || _peeking) return false;
        if (_fullscreen || HiddenForApp) return true; // before the Show-notch mode: these win over "When active"
        return App.Settings.NotchVisibility switch
        {
            "HoverOnly" => true,
            "WhenActive" => NotchActivityService.Instance.Kind == NotchActivityKind.None,
            _ => false,
        };
    }

    /// <summary>
    /// Polled with the dodge. Fades the pill out when it should hide (fully transparent, so clicks pass
    /// through to what's below) and back in once the pointer touches the top edge above it; it stays
    /// until the pointer wanders off.
    /// </summary>
    private void UpdateConceal()
    {
        var conceal = Concealable();
        if (conceal && Win32.GetCursorPos(out var p))
        {
            var (along, y) = EdgeLocal(p); // touching the notch's own edge brings it back
            var x = Math.Abs(along);
            var half = _idleW / 2 + 40;
            if (y <= 1 && x <= half) _revealed = true;
            else if (y > _collapsedH + DodgeBand || x > half + 40) _revealed = false;
        }
        else _revealed = false;

        var target = conceal && !_revealed ? 0.0 : 1.0;
        if (_opacityTarget == target) return;
        _opacityTarget = target;
        var fade = target > 0 ? Motion.Fast : Motion.Scale(300);
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(target, fade));
        // Hidden by choice, mark the spot; hidden for a full-screen video, stay out of sight entirely.
        if (!Vertical) EdgeHint.Width = Math.Round(_idleW * 0.55);
        EdgeHint.BeginAnimation(OpacityProperty, new DoubleAnimation(target == 0 && !_fullscreen && !HiddenForApp ? 0.7 : 0, fade));
    }

    private void ApplyCaptureAffinity()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.SetWindowDisplayAffinity(_hwnd, App.Settings.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    private void ApplyNotchSize()
    {
        (_idleW, _collapsedH) = App.Settings.NotchSize switch
        {
            "Small" => (180.0, 34.0),
            "Large" => (236.0, 42.0),
            "ExtraLarge" => (266.0, 46.0),
            _ => (207.0, 38.0), // ≈ a MacBook hardware notch
        };
        if (App.Settings.NotchShape == "Pill") _collapsedH -= 6; // the minimal pill is slimmer while closed
        _timerW = _idleW + 44;
        _musicW = _idleW + 150;
        CollapsedLayer.Height = _collapsedH;
        if (!_expanded)
        {
            SetCollapsedTarget();
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
            UpdateConceal();
            _idleRotate.Stop();
            _repelTimer.Stop();
            SetShove(0);
            if (EventReminderService.Instance.IsActive) ShowTool("calendar");
            else if (ContextTool() is { } matched) ShowTool(matched, remember: false);
            else ShowTool(_activeTool is SettingsId or PaletteId or BriefId or WrapId ? App.Settings.LastTool : _activeTool);
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
        SetCollapsedTarget();
        HideDropChooser(restore: false);
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
            || Concealable() // hidden notch: reaching the top edge reveals it, it mustn't run from that pointer
            || (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0 // mid-drag: dropping onto the notch must stay easy
            || !Win32.GetCursorPos(out var p) || _hwnd == IntPtr.Zero)
        {
            _dodging = false;
            SetShove(0);
            return false;
        }

        // Pointer relative to the notch's home centre, in DIPs, from screen coordinates: the window
        // is moving under us, and its cached position (PointFromScreen) can trail the last move.
        var (x, y) = EdgeLocal(p); // along the edge, and away from it
        var half = BodySpan / 2;
        var inBand = y >= -8 && y <= BodyDepth + DodgeBand;
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
            var fadeIn = new DoubleAnimation(0, 1, Motion.Fast)
            {
                EasingFunction = Motion.Out,
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
        var dpi = VisualTreeHelper.GetDpi(this);
        var px = (int)Math.Round(dip * (Vertical ? dpi.DpiScaleY : dpi.DpiScaleX));
        if (px == _shovePx) return;
        _shovePx = px;
        // Position only: no z-order, activation, size or owner-redraw work, so a move costs DWM a recomposite and nothing more.
        // The notch slides along its edge: sideways on the top or bottom, up and down on the left or right.
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, _homeX + (Vertical ? 0 : px), _homeY + (Vertical ? px : 0), 0, 0,
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
        if (e.Key == Key.Escape && CancelTabDragOnEscape())
        {
            e.Handled = true;
            return;
        }
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
        var w = Math.Max(Vertical ? 20 : 60, _width.Value); // the side tab is narrower than any top notch
        var h = Math.Max(20, _height.Value);

        // 0 = collapsed, 1 = fully open (to whatever size the current tool wants).
        // While an alert is showing the notch only grows to PeekH: track that separately.
        // Progress is measured by how far the notch reaches into the screen (its depth), on any edge.
        var depth = Vertical ? w : h;
        var span = Vertical ? h : w;
        var peekDepth = Vertical ? PeekW : _peekH;
        // Fully open once it has grown 200 px (or reached its size): switching between open tools of different sizes
        // (a tool → the taller Settings) then just resizes, instead of re-running the open morph mid-way.
        var openDepth = Math.Min(Vertical ? _expandedW : _expandedH, ClosedDepth + 200);
        var peek = _peeking && !_expanded ? Math.Clamp((depth - ClosedDepth) / Math.Max(1, peekDepth - ClosedDepth), 0, 1) : 0;
        var p = peek > 0 || (_peeking && !_expanded) ? 0 : Math.Clamp((depth - ClosedDepth) / Math.Max(1, openDepth - ClosedDepth), 0, 1);
        var floating = Floating;
        var ear = App.Settings.NotchShape == "Notch" ? EarCollapsed + (EarExpanded - EarCollapsed) * p : 0;
        // A floating island is a capsule while closed (radius = half its height), rounding like the others as it opens.
        var closedRadius = floating ? depth / 2 : RadiusCollapsed;
        var radius = closedRadius + (RadiusExpanded - closedRadius) * p + (RadiusPeek - closedRadius) * peek;

        MoveWindowBy(_shove.Value);

        // A pure slide changes nothing inside the window: skip the visual updates so WPF doesn't
        // re-render (and re-upload) the whole transparent window every frame.
        var shape = (w, h, ear, radius, p, peek, _peeking, _expanded);
        if (shape == _shapeDrawn) return;
        _shapeDrawn = shape;

        // The ears run along the edge: room for them on both ends of the span.
        Pill.Width = Vertical ? w : w + 2 * EarExpanded;
        Pill.Height = Vertical ? h + 2 * EarExpanded : h;
        Body.Margin = Vertical ? new Thickness(0, EarExpanded, 0, EarExpanded) : new Thickness(EarExpanded, 0, EarExpanded, 0);
        Body.Width = w;
        Body.Height = h;
        Body.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        var gap = floating ? IslandGap : 0;
        Pill.Margin = Edge switch
        {
            ScreenEdge.Bottom => new Thickness(0, 0, 0, gap),
            ScreenEdge.Left => new Thickness(gap, 0, 0, 0),
            ScreenEdge.Right => new Thickness(0, 0, gap, 0),
            _ => new Thickness(0, gap, 0, 0),
        };
        var r = Math.Min(radius, depth / 2);
        Geometry outline = floating
            ? new RectangleGeometry(Vertical ? new Rect(0, EarExpanded, w, h) : new Rect(EarExpanded, 0, w, h), r, r)
            : NotchGeometry(span, depth, ear, radius, EarExpanded - ear); // drawn for a top notch...
        if (!floating && Edge != ScreenEdge.Top)
        {
            outline = outline.Clone(); // ...then turned onto the actual edge
            outline.Transform = EdgeTransform(depth);
            outline.Freeze();
        }
        Shape.Data = outline;
        if (GlassSheen.Visibility == Visibility.Visible) GlassSheen.Data = GlassGlow.Data = Shape.Data;

        // Content materialises out of a blur as the notch opens (and dissolves back on close).
        var reveal = Math.Clamp((p - 0.35) / 0.65, 0, 1);
        ExpandedLayer.Opacity = reveal;
        // Staggered: the header leads, the tab bar follows and rises a few pixels into place.
        Header.Opacity = Math.Clamp(reveal / 0.7, 0, 1);
        var tabs = Math.Clamp((reveal - 0.3) / 0.7, 0, 1);
        TabBar.Opacity = tabs;
        _tabRise.Y = Motion.Reduced ? 0 : (1 - tabs) * 6;
        ExpandedLayer.Visibility = p > 0.3 ? Visibility.Visible : Visibility.Collapsed;
        _blur.Radius = (1 - reveal) * 14;
        ExpandedLayer.Effect = reveal < 0.999 && ExpandedLayer.Visibility == Visibility.Visible ? _blur : null;

        // Depth: a soft shadow that grows as the notch opens, so it lifts off bright wallpapers. Not on glass (it would
        // show through and darken it), and removed when closed so the idle notch costs no effect pass.
        _openP = p;
        ApplyShapeEffect();

        // The minimal Pill keeps its closed content quieter.
        CollapsedLayer.Opacity = Math.Clamp(1 - p * 4 - peek * 3, 0, 1) * (App.Settings.NotchShape == "Pill" ? 0.75 : 1);
        CollapsedLayer.Visibility = !Vertical && p < 0.25 && peek < 0.34 ? Visibility.Visible : Visibility.Collapsed;
        SideLayer.Opacity = CollapsedLayer.Opacity;
        SideLayer.Visibility = Vertical && p < 0.25 && peek < 0.34 ? Visibility.Visible : Visibility.Collapsed;
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
        if (e.PropertyName == nameof(NowPlayingService.Title))
        {
            _trackTimer.Stop(); // art and artist land a moment after the title
            _trackTimer.Start();
        }
    }

    // ---------------------------------------------------------------- new-song alert

    private readonly DispatcherTimer _trackTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DateTime _createdAt = DateTime.Now;
    private string? _announcedTrack;

    /// <summary>Settings → Now Playing "Announce new songs": a short alert with the art when the track changes.</summary>
    private void ShowTrackPeek()
    {
        _trackTimer.Stop();
        var np = NowPlayingService.Instance;
        var key = np.Title + "\n" + np.Artist;
        if (key == _announcedTrack) return;
        _announcedTrack = key;
        // Not the song already playing at launch, and not while the panel shows it anyway.
        if (!App.Settings.TrackPeek || DateTime.Now - _createdAt < TimeSpan.FromSeconds(5)
            || !np.HasSession || !np.IsPlaying || _expanded || string.IsNullOrWhiteSpace(np.Title)) return;
        var art = np.Art;
        EnqueuePeek(new PeekRequest("track", () => ArtTile(art), np.Title, np.Artist, TimeSpan.FromSeconds(3),
            OnClick: () => OpenTool("nowplaying")));
    }

    private static FrameworkElement ArtTile(ImageSource? art)
    {
        if (art is null) return GlyphTile(AppIcon.MusicNote);
        var image = new Image { Source = art, Width = 40, Height = 40, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return new Border { Width = 40, Height = 40, Child = image, Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 11, 11) };
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
            ReminderTile.BeginAnimation(OpacityProperty, pulse && !Motion.Reduced // a looping pulse stays still with animations off
                ? new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }
        if (!reminder)
        {
            _peekQueue.RemoveAll(r => r.Key == ReminderPeekKey);
            if (_currentPeek?.Key == ReminderPeekKey) EndPeek();
        }

        SetEqualizer(!_expanded && music);

        if (Vertical) RenderSide();
        if (!_expanded && !_peeking)
        {
            SetCollapsedTarget();
            StartAnimation();
        }
    }

    /// <summary>Waveform colour from the album art: the art's tint, lifted until it reads on black.</summary>
    private Color WaveColor(Color? tint)
    {
        if (tint is not { } c) return ThemeService.CurrentAccent.Mean;
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
                IdleSlotA.Content = IdleGlyph(item, 16);
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
            if (Vertical) RenderSide();
            var w = Vertical ? SideDepth : IdleWidth();
            if (Math.Abs(_width.Target - w) > 0.5)
            {
                SetCollapsedTarget();
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
            var ease = Motion.Out;
            var begin = Motion.Scale(delay);
            slot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Slow) { BeginTime = begin, EasingFunction = ease });
            ((TranslateTransform)slot.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(7, 0, Motion.Slow) { BeginTime = begin, EasingFunction = ease });
        }
        if (Vertical) RenderSide();
        if (!_expanded && !_peeking)
        {
            SetCollapsedTarget();
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
        var glyphBrush = !ThemeService.IsDefaultScheme && item.ValueBrush == "TextBrush"
            ? Application.Current.Resources["SubTextBrush"] as Brush // other schemes: a quiet grey icon beside the value
            : IdleBrush(item);
        return new HeroIcon { Kind = item.Glyph ?? AppIcon.Info, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Foreground = glyphBrush };
    }

    /// <summary>
    /// Each idle item has its own colour, shared by its icon and its value, like the Dynamic Island's live activities.
    /// Fixed, not the accent, so items stay recognisable at a glance. Status overrides it: red needs attention
    /// (low battery, mic muted, an AI tool near its limit), green is good (charging, a stock up).
    /// </summary>
    private static readonly Dictionary<string, Color> IdleColors = new()
    {
        ["clock"] = C(0xF5, 0xF5, 0xF7), ["clock2"] = C(0x64, 0xD2, 0xFF), ["date"] = C(0xFF, 0x45, 0x3A),
        ["weather"] = C(0x5A, 0xC8, 0xFA), ["sun"] = C(0xFF, 0xD6, 0x0A), ["nextEvent"] = C(0xFF, 0x45, 0x3A),
        ["timeline"] = C(0xFF, 0x9F, 0x0A), ["todos"] = C(0x30, 0xD1, 0x58), ["battery"] = C(0x30, 0xD1, 0x58),
        ["cpu"] = C(0xBF, 0x5A, 0xF2), ["network"] = C(0x40, 0x9C, 0xFF), ["stock"] = C(0x30, 0xD1, 0x58),
        ["aiUsage"] = C(0xFF, 0x9F, 0x0A), ["unread"] = C(0xFF, 0x37, 0x5F), ["clipboard"] = C(0x64, 0xD2, 0xFF),
        ["shelf"] = C(0xAC, 0x8E, 0xFF), ["streak"] = C(0xFF, 0x9F, 0x0A), ["screentime"] = C(0x5E, 0x5C, 0xE6),
        ["mic"] = C(0xFF, 0x45, 0x3A), ["call"] = C(0xFF, 0x45, 0x3A), ["awake"] = C(0xFF, 0x9F, 0x0A),
    };

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Brush IdleBrush(IdleItem item)
    {
        // Another scheme was picked: the idle notch takes that scheme's look (grey icon, its text colour) instead.
        if (!ThemeService.IsDefaultScheme)
            return Application.Current.Resources[item.ValueBrush] as Brush ?? Brushes.White;
        var color = item.ValueBrush switch
        {
            "BadBrush" => C(0xFF, 0x45, 0x3A),
            "GoodBrush" => C(0x30, 0xD1, 0x58),
            _ => IdleColors.GetValueOrDefault(item.Id.Split(':')[0], C(0xF5, 0xF5, 0xF7)),
        };
        if (ThemeService.CurrentScheme.IsLight) // bright colours wash out on a light notch: deepen them
            color = color == C(0xF5, 0xF5, 0xF7) ? C(0x1C, 0x1C, 0x1E) : Color.FromRgb((byte)(color.R * 0.72), (byte)(color.G * 0.72), (byte)(color.B * 0.72));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
        ["aiUsage"] = "100%",
        ["weather"] = "-00°",
        ["timeline"] = "0000000000", // the timeline is 112 px: give it the wide notch
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

    private static FrameworkElement IdleValue(IdleItem item, double fontSize = 13.5, TextAlignment align = TextAlignment.Right)
    {
        if (item.Id == "timeline") return new DayTimeline(IdleInfoService.Instance.TodayEvents);
        if (item.Id == "clock" && App.Settings.ClockStyle == "Flip") return FlipClock(IdleText(item, fontSize - 1, TextAlignment.Center));
        return IdleText(item, fontSize, align);
    }

    private static string? _flipShown; // the time the flip clock last showed: a new one flips in

    /// <summary>Settings › Display "Clock style: Flip": the time on a small split tile that flips when the minute changes.</summary>
    private static FrameworkElement FlipClock(TextBlock text)
    {
        var seam = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        seam.SetResourceReference(Border.BackgroundProperty, "PillBrush");
        var face = new Grid();
        face.Children.Add(text);
        face.Children.Add(seam);
        var tile = new Border
        {
            CornerRadius = Radii.S, Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = face, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        if (_flipShown is not null && _flipShown != text.Text)
        {
            var flip = new ScaleTransform(1, 1);
            tile.RenderTransform = flip;
            flip.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.05, 1, Motion.Scale(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
            });
        }
        _flipShown = text.Text;
        return tile;
    }

    private static TextBlock IdleText(IdleItem item, double fontSize, TextAlignment align)
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
        // The value takes the item's colour (dimmed when there's nothing to show: "No more events", "All done").
        if (item.ValueBrush == "SubTextBrush") text.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        else text.Foreground = IdleBrush(item);
        return text;
    }

    /// <summary>Glyph + value side by side (split and row layouts).</summary>
    private static StackPanel IdleChip(IdleItem item)
    {
        var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        chip.Children.Add(IdleGlyph(item, 14));
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
    /// <summary>Taller alert for a body that wraps to two lines.</summary>
    private const double PeekH2 = 92;
    private double _peekH = PeekH;
    private const double RadiusPeek = 24;
    private const string ReminderPeekKey = "reminder";
    private bool _peeking;
    private readonly DispatcherTimer _peekTimer = new();

    /// <summary>One alert: what to draw, and what dismiss / click do.</summary>
    private sealed record PeekAction(AppIcon Icon, string Tip, Action Run);

    private sealed record PeekRequest(string Key, Func<FrameworkElement> Icon, string Title, string Sub,
        TimeSpan Duration, Action? OnDismiss = null, Action? OnClick = null, PeekAction[]? Actions = null)
    {
        public DateTime QueuedAt { get; } = DateTime.Now;
    }

    // Reminder alerts and Windows notifications share one queue and show one after another.
    private readonly List<PeekRequest> _peekQueue = new();
    private PeekRequest? _currentPeek;

    private double PeekW => Math.Max(_musicW + 40, 400) + (_currentPeek?.Actions?.Length ?? 0) * 32;

    private void EnqueuePeek(PeekRequest request)
    {
        if (HeldForQuiet(request)) return;
        // A newer alert with the same key (same app / same reminder) replaces the waiting one.
        _peekQueue.RemoveAll(r => r.Key == request.Key);
        // The same alert already showing (volume steps): refresh it in place.
        if (_peeking && _currentPeek?.Key == request.Key)
        {
            _currentPeek = request;
            PeekIconHost.Content = request.Icon();
            PeekTitle.Text = OneLine(request.Title);
            PeekSub.Text = OneLine(request.Sub);
            _peekTimer.Stop();
            _peekTimer.Interval = request.Duration;
            _peekTimer.Start();
            return;
        }
        _peekQueue.Add(request);
        if (_peekQueue.Count > 4) _peekQueue.RemoveAt(0);
        if (!_peeking) ShowNextPeek();
        UpdatePeekMore();
    }

    private void UpdatePeekMore()
    {
        PeekMore.Text = $"+{_peekQueue.Count}";
        PeekMore.Visibility = _peekQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNextPeek()
    {
        if (_expanded || AlertsHeld()) return; // stays queued until the notch is closed / the app is left
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible; // a listed app hid it but lets alerts through
        _peekQueue.RemoveAll(r => DateTime.Now - r.QueuedAt > TimeSpan.FromMinutes(1)); // stale while the notch was open
        if (_peekQueue.Count == 0) return;
        var next = _peekQueue[0];
        _peekQueue.RemoveAt(0);

        _currentPeek = next;
        UpdatePeekMore();
        PeekIconHost.Content = next.Icon();
        PeekTitle.Text = OneLine(next.Title);
        PeekSub.Text = OneLine(next.Sub);
        PeekSub.Visibility = string.IsNullOrWhiteSpace(next.Sub) ? Visibility.Collapsed : Visibility.Visible;
        PeekActions.Children.Clear();
        foreach (var action in next.Actions ?? [])
        {
            var button = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 4, 0), ToolTip = action.Tip,
                Style = (Style)FindResource("IconButton"), Content = new HeroIcon { Kind = action.Icon, Width = 13, Height = 13 },
            };
            button.SetResourceReference(BackgroundProperty, "HoverBrush");
            button.SetResourceReference(ForegroundProperty, "TextBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, action.Tip);
            MakeRound(button);
            button.Click += (_, e) =>
            {
                e.Handled = true;
                EndPeek();
                action.Run();
            };
            PeekActions.Children.Add(button);
        }
        _peekH = SubWraps(PeekSub.Text) ? PeekH2 : PeekH;
        AnimatePeekIn();
        _peeking = true;
        UpdateConceal();
        PeekLayer.Height = _peekH;
        _peekTimer.Stop();
        _peekTimer.Interval = next.Duration;
        _peekTimer.Start();
        _width.Target = PeekW;
        _height.Target = _peekH;
        StartAnimation();
    }

    /// <summary>Multi-line text (chat messages, emails) as one line, so the alert's layout holds.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whether the alert body needs a second line at the alert's text width.</summary>
    private bool SubWraps(string text)
    {
        if (text.Length == 0) return false;
        // Text column = alert width − side margins (30) − icon (40) − gaps (23) − dismiss (28).
        var width = PeekW - 121;
        var measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(PeekSub.FontFamily, PeekSub.FontStyle, PeekSub.FontWeight, PeekSub.FontStretch),
            PeekSub.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return measured.WidthIncludingTrailingWhitespace > width;
    }

    /// <summary>Resting the pointer on an alert keeps it up; leaving gives a couple more seconds.</summary>
    private void OnPeekHover(object sender, MouseEventArgs e)
    {
        if (!_peeking) return;
        _peekTimer.Stop();
        if (PeekLayer.IsMouseOver) return;
        _peekTimer.Interval = TimeSpan.FromSeconds(2);
        _peekTimer.Start();
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
        SetCollapsedTarget();
        StartAnimation();
        if (HiddenForApp) Guard(); // "Hide completely" let this alert through; hide again now it's done
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

    /// <summary>Alert buttons are circles, like the alert's icon tile (the IconButton template is a rounded square).</summary>
    private static void MakeRound(Button button)
    {
        void Apply()
        {
            button.ApplyTemplate();
            if (button.Template?.FindName("Bd", button) is Border bd) bd.CornerRadius = new CornerRadius(button.Width / 2);
        }
        if (button.IsLoaded) Apply();
        else button.Loaded += (_, _) => Apply();
    }

    /// <summary>
    /// Each alert arrives: its icon pops in with a small overshoot and its text rises into place (also when it replaces
    /// one already on screen, so the swap reads as "next" instead of a flicker).
    /// </summary>
    private void AnimatePeekIn()
    {
        var pop = new ScaleTransform(0.7, 0.7);
        PeekIconHost.RenderTransform = pop;
        var grow = new DoubleAnimation(0.7, 1, Motion.Slow) { EasingFunction = Motion.Pop };
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var rise = new TranslateTransform(0, 8);
        PeekText.RenderTransform = rise;
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Motion.Base) { EasingFunction = Motion.Out });
        PeekText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Base) { EasingFunction = Motion.Out });
        PeekActions.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Slow) { EasingFunction = Motion.Out });
    }

    private void ShowReminderPeek()
    {
        var r = EventReminderService.Instance;
        if (!r.IsActive) return;
        // Automations › Join: a meeting with a link opens it from the reminder.
        var link = App.Settings.AutoJoinLink ? r.CurrentLink : null;
        EnqueuePeek(new PeekRequest(ReminderPeekKey, () => GlyphTile(AppIcon.CalendarDays), r.Title,
            link is null ? r.SubText : $"{r.SubText} · click to join",
            TimeSpan.FromSeconds(link is null ? 5 : 8),
            OnDismiss: () => EventReminderService.Instance.Dismiss(),
            OnClick: link is null
                ? () => Expand(activate: true) // a live reminder routes this to Calendar
                : () => OpenLink(link)));
    }

    private static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void ShowNotificationPeek(NotchNotification n)
    {
        var sub = string.IsNullOrWhiteSpace(n.Body) ? n.AppName : $"{n.AppName} · {n.Body}";
        // Long messages stay up a little longer (4 s + 1 s per 60 characters, at most 8 s).
        var seconds = Math.Min(8, 4 + OneLine(n.Body).Length / 60);
        EnqueuePeek(new PeekRequest("app:" + n.AppId, () => AppTile(n), n.Title, sub, TimeSpan.FromSeconds(seconds),
            OnClick: () => OpenTool("notifications")));
    }

    /// <summary>
    /// AI tool nearing its limit: the tool's logo, how full the window is and when it resets.
    /// 80% is a heads-up; 95% says it's almost out. Clicking opens AI Usage.
    /// </summary>
    private void ShowAiLimitPeek(AiLimitAlert a)
    {
        var window = a.Window switch { "session" => "session", "weekly" => "weekly", _ => "plan" };
        var title = a.Threshold >= AiLimitWatcher.Thresholds[^1] || a.Percent >= 95
            ? $"{a.ToolName} · {window} almost full"
            : $"{a.ToolName} · {window} {a.Percent:0}% used";
        var resets = a.Resets is not { } r ? null
            : r.Date == DateTime.Today ? $"Resets {r:t}"
            : r.Date == DateTime.Today.AddDays(1) ? $"Resets tomorrow {r:t}"
            : $"Resets {r:ddd h:mm tt}";
        var pace = a.Full is { } full ? $"Full by {full:t} at this pace"
            : a.Percent >= 95 ? $"{Math.Max(0, 100 - a.Percent):0}% left" : "Pace yourself";
        var sub = resets is null ? pace : $"{pace} · {resets}";
        EnqueuePeek(new PeekRequest($"ailimit:{a.ToolId}:{a.Window}", () => LogoTile(a.Logo), title, sub,
            TimeSpan.FromSeconds(6), OnClick: () => OpenTool("aiusage")));
    }

    private void ShowUpdatePeek(string version)
    {
        var sub = version.Length > 0 ? $"OrixNotch {version} · click to install" : "A new version is ready · click to install";
        EnqueuePeek(new PeekRequest("update", () => GlyphTile(AppIcon.ArrowDown), "Update available", sub,
            TimeSpan.FromSeconds(8), OnClick: () => _ = UpdateService.Instance.InstallAsync()));
    }

    /// <summary>First launch after an update: say so once, and link to the release notes.</summary>
    private void ShowWhatsNewPeek()
    {
        var version = UpdateService.CurrentVersion;
        var last = App.Settings.LastSeenVersion;
        if (last == version || QuietActive()) return; // while quiet: shown when quiet ends (CheckQuietEnded)
        App.Settings.LastSeenVersion = version;
        SettingsService.Save(notify: false);
        if (SettingsService.FirstRun || ShotsDir is not null) return; // a new install gets the welcome instead
        EnqueuePeek(new PeekRequest("whatsnew", () => GlyphTile(AppIcon.Sparkles), $"Updated to OrixNotch {version}",
            "Click to see what's new", TimeSpan.FromSeconds(8), OnClick: () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        $"https://github.com/faisal-shohag/orixNotch/releases/tag/v{version}") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }
            }));
    }

    private void ShowPowerPeek(string title, string sub, bool charging) =>
        // Plugged in: the battery fills to its level (sub is "NN%").
        EnqueuePeek(new PeekRequest("power",
            () => charging && int.TryParse(sub.TrimEnd('%'), out var pct) ? ChargingTile.Create(pct) : GlyphTile(AppIcon.BatteryFull),
            title, sub, TimeSpan.FromSeconds(3)));

    private void ShowVolumePeek(float level, bool muted)
    {
        if (!App.Settings.VolumeHud) return;
        var pct = (int)Math.Round(level * 100);
        EnqueuePeek(new PeekRequest("volume", () => GlyphTile(muted || pct == 0 ? AppIcon.SpeakerMuted : AppIcon.Speaker),
            "Volume", muted ? "Muted" : $"{pct}%", TimeSpan.FromSeconds(1.5)));
    }

    private void ShowBluetoothPeek(string device, bool connected)
    {
        if (!App.Settings.BluetoothAlerts) return;
        EnqueuePeek(new PeekRequest("bt:" + device, () => GlyphTile(AppIcon.Bluetooth), device,
            connected ? "Connected" : "Disconnected", TimeSpan.FromSeconds(3)));
    }

    /// <summary>An alert from a script: an orixnotch://peek link or the local peek API. Text arrives clipped.</summary>
    public void ShowExternalPeek(string title, string sub, string? icon)
    {
        var glyph = Enum.TryParse<AppIcon>(icon, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : AppIcon.Bolt;
        EnqueuePeek(new PeekRequest("ext:" + title, () => GlyphTile(glyph), title, sub, TimeSpan.FromSeconds(5)));
    }

    private void ShowDownloadPeek(string path)
    {
        if (!App.Settings.DownloadAlerts) return;
        void ShowInFolder() => Run("explorer.exe", $"/select,\"{path}\"");
        var actions = new List<PeekAction>
        {
            new(AppIcon.File, "Open", () => Run(path, null)),
            new(AppIcon.Folder, "Show in folder", ShowInFolder),
        };
        if (PhoneTransferService.Instance.IsRunning)
            actions.Add(new(AppIcon.DevicePhone, "Send to phone", () => PhoneTransferService.Instance.Send([path])));
        actions.Add(new(AppIcon.ArchiveBox, "Add to Shelf", () => ShelfService.Instance.AddFiles([path], owned: false)));
        EnqueuePeek(new PeekRequest("download:" + path, () => GlyphTile(AppIcon.ArrowDown), "Download finished",
            System.IO.Path.GetFileName(path), TimeSpan.FromSeconds(6), OnClick: ShowInFolder, Actions: actions.ToArray()));

        static void Run(string file, string? args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args ?? "") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
    }

    /// <summary>A screenshot was saved: its thumbnail; clicking parks it on the Shelf (drag it out or send it to the phone from there).</summary>
    private void ShowScreenshotPeek(string path)
    {
        if (!App.Settings.ScreenshotAlerts || OcrService.Instance.Waiting || !System.IO.File.Exists(path)) return;
        EnqueuePeek(new PeekRequest("screenshot", () => ThumbTile(path), "Screenshot saved",
            "Click to put it on the Shelf", TimeSpan.FromSeconds(5), OnClick: () =>
            {
                ShelfService.Instance.AddFiles([path], owned: false);
                OpenTool("shelf");
            }));
    }

    /// <summary>A small preview of an image file (decoded small, and not left locked).</summary>
    private static FrameworkElement ThumbTile(string path)
    {
        var frame = new Border { Width = 40, Height = 40, CornerRadius = Radii.M, ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelHeight = 80;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            frame.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException)
        {
            frame.Child = new HeroIcon { Kind = AppIcon.Photo, Width = 19, Height = 19 };
        }
        return frame;
    }

    private void ShowPhoneReceivedPeek(string peer, int count, bool text)
    {
        var what = text ? "text" : count == 1 ? "1 file" : $"{count} files";
        var detail = text
            ? "Open Phone to copy it"
            : $"From {peer} · in {System.IO.Path.GetFileName(PhoneTransferService.Instance.ReceiveFolder.TrimEnd('\\'))}";
        EnqueuePeek(new PeekRequest("phone:received", () => GlyphTile(AppIcon.DevicePhone), $"Received {what}",
            detail, TimeSpan.FromSeconds(4), OnClick: () => OpenTool("phone")));
    }

    /// <summary>Brand logo on a soft tile (AI usage alerts).</summary>
    private static FrameworkElement LogoTile(BrandLogo logo)
    {
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20) };
        tile.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        var mark = BrandLogos.Create(logo, 20);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        tile.Child = mark;
        return tile;
    }

    /// <summary>Accent-soft rounded tile with a glyph (reminders).</summary>
    private static FrameworkElement GlyphTile(AppIcon kind)
    {
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20) };
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
            var frame = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Child = image };
            frame.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            return frame;
        }
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20) };
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
    private static Geometry BuildMiniArc(double progress, double size = 22, double inset = 2.5)
    {
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

    private readonly TranslateTransform _tabRise = new();
    private readonly DropShadowEffect _depth = new() { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Color = Colors.Black, Opacity = 0 };
    private readonly DropShadowEffect _glow = new() { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.6 };
    private double _openP;          // how open the notch is, 0–1 (the spring's progress)
    private Color? _glowColor;
    private bool _glowBreathing;

    /// <summary>
    /// The notch's one effect: while open, a soft shadow that grows with it (not on glass, it would show through);
    /// while closed, the glow (Appearance › Glow): red on a call, accent in a focus session (both breathing slowly),
    /// the cover's colour while music plays. Nothing at all otherwise, so the idle notch costs no effect pass.
    /// </summary>
    private void ApplyShapeEffect()
    {
        var shadow = ThemeService.IsGlass ? 0 : _openP;
        _depth.Opacity = 0.45 * shadow;
        Shape.Effect = shadow > 0.02 ? _depth : _glowColor is not null ? _glow : null;
    }

    public void RefreshGlow() => UpdateGlow();

    private void UpdateGlow()
    {
        Color? color = null;
        var breathe = false;
        if (App.Settings.NotchGlow)
        {
            if (InCall) (color, breathe) = (Color.FromRgb(0xFF, 0x45, 0x3A), true);
            else if (PomodoroService.Instance.Focusing) (color, breathe) = (ThemeService.CurrentAccent.Mean, true);
            else if (NowPlayingService.Instance.IsPlaying && NowPlayingService.Instance.ArtTint is { } tint) color = tint;
        }
        if (color == _glowColor && breathe == _glowBreathing) return;
        _glowColor = color;
        _glowBreathing = breathe;
        if (color is { } c) _glow.Color = c;
        if (breathe && !Motion.Reduced)
        {
            var pulse = new DoubleAnimation(0.3, 0.8, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(pulse, 20); // a slow breath doesn't need 60 fps
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        }
        else
        {
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            _glow.Opacity = 0.6;
        }
        ApplyShapeEffect();
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
        _eqLive.Stop();
        if (!on)
        {
            _eq.Stop(this);
            return;
        }
        if (App.Settings.ReactivePill && !BatterySaver() && VolumeService.Instance.Peak() is not null) _eqLive.Start();
        else _eq.Begin(this, true);
    }

    // Live bars: the speakers' peak level ~30 times a second, each bar at its own phase so they don't move as one.
    private readonly DispatcherTimer _eqLive = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly double[] _eqLevels = new double[5];

    private void TickEqualizer()
    {
        var bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5 };
        var peak = Math.Sqrt(VolumeService.Instance.Peak() ?? 0); // square root: quiet passages still move
        var t = Environment.TickCount64 / 1000.0;
        for (var i = 0; i < bars.Length; i++)
        {
            var wobble = 0.65 + 0.35 * Math.Sin(t * (5 + i * 1.7) + i);
            var target = Math.Clamp(0.22 + peak * wobble, 0.22, 1);
            // Rise fast, fall slower, like a VU meter.
            _eqLevels[i] += (target - _eqLevels[i]) * (target > _eqLevels[i] ? 0.6 : 0.2);
            if (bars[i].RenderTransform is ScaleTransform scale) scale.ScaleY = _eqLevels[i];
        }
    }

    private static bool BatterySaver()
    {
        try
        {
            return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- tools

    private void BuildTabs()
    {
        var tools = ToolRegistry.Ordered();
        if (tools.Count == 0) tools = [ToolRegistry.All[0]];

        var previousActive = _activeTool;
        if (_activeTool is not (SettingsId or PaletteId or BriefId or WrapId) && tools.All(t => t.Id != _activeTool)) _activeTool = tools[0].Id;

        // Settings isn't in the scrolling strip: it's pinned at the right end (SettingsTabHost).
        SettingsTabHost.Child ??= MakeTab(SettingsId, "Settings", AppIcon.Gear);
        var wanted = tools.Select(t => t.Id).ToList();
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
                Tabs.Children.Insert(i, MakeTab(id, byId[id].Name, byId[id].Icon));
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
            Content = id == "notifications" ? WithUnreadBadge(new HeroIcon { Kind = icon, Width = 17, Height = 17 })
                : IsNewTool(id) ? WithNewDot(id, new HeroIcon { Kind = icon, Width = 17, Height = 17 })
                : new HeroIcon { Kind = icon, Width = 17, Height = 17 },
            ToolTip = name,
            GroupName = "tools",
            Tag = id,
            IsChecked = id == _activeTool,
        };
        tab.Checked += (_, _) =>
        {
            if (id == _activeTool) return; // Present() checks the tab of the tool it just showed
            if (id == SettingsId) ShowSettings();
            else ShowTool(id);
        };
        if (id != SettingsId) WireTabDrag(tab); // press and hold to reorder
        return tab;
    }

    // "New" dots on the tabs of tools added in this release, until each is opened (not on a first install: all is new).
    private readonly Dictionary<string, FrameworkElement> _newDots = new();

    private static bool IsNewTool(string id) =>
        !SettingsService.FirstRun && ToolRegistry.NewTools.Contains(id) && !App.Settings.SeenNew.Contains("tool|" + id);

    private FrameworkElement WithNewDot(string id, FrameworkElement icon)
    {
        var dot = new Ellipse
        {
            Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -5, 0), IsHitTestVisible = false,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        _newDots[id] = dot;
        var grid = new Grid();
        grid.Children.Add(icon);
        grid.Children.Add(dot);
        return grid;
    }

    private void MarkToolSeen(string id)
    {
        if (!_newDots.Remove(id, out var dot)) return;
        dot.Visibility = Visibility.Collapsed;
        App.Settings.SeenNew.Add("tool|" + id);
        SettingsService.Save(notify: false);
    }

    /// <summary>The Notifications tab's icon with a count of what arrived since it was last opened.</summary>
    private static FrameworkElement WithUnreadBadge(FrameworkElement icon)
    {
        var count = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        var badge = new Border
        {
            MinWidth = 13, Height = 13, CornerRadius = Radii.S, Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -9, 0), IsHitTestVisible = false, Child = count,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        void Update()
        {
            var n = NotificationHistory.Instance.Unread;
            count.Text = n > 9 ? "9+" : n.ToString();
            badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        NotificationHistory.Instance.Changed += Update;
        Update();
        var grid = new Grid();
        grid.Children.Add(icon);
        grid.Children.Add(badge);
        return grid;
    }

    /// <summary>Shows a tool. <paramref name="backTo"/> makes it a sub-page with a back chevron.</summary>
    /// <summary>Automations › "Match the app in front": the tool set for the app in front, if any.</summary>
    private static string? ContextTool()
    {
        var s = App.Settings;
        return s.ContextTools && ForegroundAppWatcher.Instance.Current is { } app && s.AppTools.TryGetValue(app, out var tool)
            && ToolRegistry.All.Any(t => t.Id == tool) ? tool : null;
    }

    /// <param name="remember">False for a tool picked for the app in front: the next plain open returns to the last one chosen.</param>
    public void ShowTool(string id, string? backTo = null, bool remember = true)
    {
        if (id == SettingsId)
        {
            ShowSettings();
            return;
        }

        var def = ToolRegistry.All.FirstOrDefault(t => t.Id == id) ?? ToolRegistry.All[0];
        MarkToolSeen(def.Id);
        _backTarget = backTo;
        Present(def.Id, def.Name, def.Width, def.Height, () => def.Create(), showTabs: true);

        if (remember && App.Settings.LastTool != def.Id)
        {
            App.Settings.LastTool = def.Id;
            SettingsService.Save(notify: false);
        }
    }

    /// <summary>Hotkey "Explain selected text": copy the selection while its app is still in front, then ask.</summary>
    private async Task ExplainSelectionAsync()
    {
        var text = await SelectionService.CopyAsync();
        OpenTool("ask");
        View<Tools.AskAiView>("ask")?.Explain(text);
    }

    /// <summary>An orixnotch://theme link: apply the look, with Undo on the alert.</summary>
    public void ApplyThemeLink(ExternalCommands.Link link)
    {
        var before = ThemeService.Current();
        var look = ThemeService.Parse(link.Get, before);
        if (look == before) return;
        ThemeService.ApplyLook(look);
        var name = ThemeService.Schemes.FirstOrDefault(s => s.Id == look.Scheme)?.Name ?? look.Scheme;
        EnqueuePeek(new PeekRequest("theme", () => GlyphTile(AppIcon.Palette), "New look applied",
            $"{name}{(look.Glass ? " glass" : "")}", TimeSpan.FromSeconds(8),
            Actions: [new PeekAction(AppIcon.Backward, "Undo", () => ThemeService.ApplyLook(before))]));
    }

    /// <summary>Appearance › Reset theme: back to the default look, with Undo on the alert.</summary>
    public void ResetTheme()
    {
        var before = ThemeService.Current();
        var look = ThemeService.DefaultLook();
        if (look == before) return;
        ThemeService.ApplyLook(look);
        EnqueuePeek(new PeekRequest("theme", () => GlyphTile(AppIcon.Palette), "Theme reset", "Back to the default look",
            TimeSpan.FromSeconds(8), Actions: [new PeekAction(AppIcon.Backward, "Undo", () => ThemeService.ApplyLook(before))]));
    }

    public const string PaletteId = "palette";
    public const string BriefId = "brief";
    public const string WrapId = "wrap";

    /// <summary>The end-of-day wrap: today's screen time, focus and to-dos, with "Save image".</summary>
    public void ShowWrap()
    {
        Expand(activate: true);
        _backTarget = null;
        Present(WrapId, $"Your day · {DateTime.Today:ddd, MMM d}", 720, 128, () => new Tools.WrapView(), showTabs: false);
        (_views[WrapId] as Tools.WrapView)?.Show();
    }

    private static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>
    /// Automations › Morning brief: once a day, at the first unlock (or at launch), the notch opens for a few seconds
    /// on the day's weather, next event, to-dos, AI usage and a stock. Never while quiet, in full screen or in use.
    /// </summary>
    private async void TryMorningBrief()
    {
        var s = App.Settings;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!s.MorningBrief || s.BriefShown == today || _expanded || _peeking || QuietActive() || AlertsHeld()) return;
        s.BriefShown = today;
        SettingsService.Save(notify: false);
        var items = await IdleInfoService.Instance.SnapshotAsync("weather", "nextEvent", "todos", "aiUsage", "stock");
        if (_expanded || _peeking || items.Count == 0) return;
        Expand(activate: false); // no focus taken: you may already be typing
        _awaitingCursor = true;   // stays open until the timer ends, or the pointer visits and leaves
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        Present(BriefId, $"{greeting}, {ProfileService.FirstName}", 720, 110, () => new Tools.MorningBriefView(), showTabs: false);
        (_views[BriefId] as Tools.MorningBriefView)?.Show(items, i => IdleGlyph(i, 14));
        var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            if (_expanded && _activeTool == BriefId && !IsCursorOverPill() && !_pinned) Collapse();
        };
        close.Start();
    }

    /// <summary>Opens the notch on the command palette ("timer 10", "todo …", "20 usd to eur", tool names…).</summary>
    public void ShowPalette()
    {
        Expand(activate: true);
        _backTarget = null;
        Present(PaletteId, "Command", 720, 220, () => new Tools.CommandPaletteView(), showTabs: false);
    }

    /// <summary>A tool's view when it has been created (opening it creates it).</summary>
    public T? View<T>(string id) where T : FrameworkElement => _views.GetValueOrDefault(id) as T;

    public void ShowSettings()
    {
        if (_activeTool != SettingsId) _backTarget = _activeTool;
        // Settings opens taller than the tools, so its whole sidebar fits (≈750×640).
        Present(SettingsId, "Settings", 714, 568, () => new SettingsView(), showTabs: false); // tall enough for the whole sidebar
    }

    /// <summary>Opens the notch on one Settings page ("tabs", "appearance"…).</summary>
    public void OpenSettings(string page)
    {
        OpenTool(SettingsId);
        if (_views.GetValueOrDefault(SettingsId) is SettingsView settings) settings.ShowPage(page);
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
        TitleLogo.Visibility = id == "home" ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Tools call this to grow/shrink the notch live (e.g. teleprompter resize handle).
    /// The height is capped so the notch fits the screen; returns the height actually applied.</summary>
    public double SetToolSize(double w, double h, bool? showTabs = null)
    {
        var tabs = showTabs ?? TabBar.Visibility == Visibility.Visible;
        var chrome = HeaderH + (tabs ? TabBarH : SidePad);
        var screenH = CurrentScreen().Bounds.Height / VisualTreeHelper.GetDpi(this).DpiScaleY;
        h = Math.Max(0, Math.Min(h, screenH - chrome - ScreenMargin));
        // The window is a fixed transparent canvas the notch draws into: grow it so a tall tool isn't cropped.
        var needed = Math.Ceiling(chrome + h + WindowSlack);
        if (needed > Height)
        {
            Height = needed;
            Place();
        }

        _toolW = w;
        _toolH = h;
        ToolHost.Width = w;
        ToolHost.Height = h;
        _expandedH = chrome + h;
        _expandedW = w + 2 * SidePad;
        if (!_expanded) return h;
        _width.Target = w + 2 * SidePad;
        _height.Target = _expandedH;
        StartAnimation();
        return h;
    }

    /// <summary>Background opacity for the notch silhouette (teleprompter "see-through" mode).</summary>
    public void SetBackgroundOpacity(double opacity) => Shape.Opacity = Math.Clamp(opacity, 0.15, 1);

    private int TabIndex(string id)
    {
        if (id == SettingsId) return Tabs.Children.Count; // pinned after the strip
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
        ScrollTabIntoView(x, tab.ActualWidth, animate);
        if (!animate || TabIndicator.Opacity < 0.5)
        {
            TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorShift.X = x;
            TabIndicator.Opacity = 1;
            return;
        }
        var ease = Motion.Out;
        TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, Motion.Base) { EasingFunction = ease });
    }

    /// <summary>New content slides in from the side of the tab it came from and fades up.</summary>
    private static void SlideIn(UIElement element, int direction)
    {
        var ease = Motion.Out;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Base) { EasingFunction = ease });
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        if (direction == 0)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Motion.Base) { EasingFunction = ease });
        else
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18 * direction, 0, Motion.Base) { EasingFunction = ease });
    }

    private void OnSettingsChanged()
    {
        ApplyNotchSize();
        BuildTabs();
        Place();
        _shapeDrawn = default; // the shape or edge may have changed: redraw even at the same size
        ApplyGeometry();
        ApplyCaptureAffinity();
    }

    // ---------------------------------------------------------------- drag & drop → shelf

    private void OnPillDragEnter(object sender, DragEventArgs e)
    {
        if (!DropPayload.CanAccept(e.Data)) return;
        // The Shelf and Phone tabs take drops themselves; Ask AI takes images and text, the teleprompter text.
        if (_expanded && _activeTool is "shelf" or "phone") return;
        if (_expanded && _activeTool == "ask" && (DropPayload.HasImage(e.Data) || !e.Data.GetDataPresent(DataFormats.FileDrop))) return;
        if (_expanded && _activeTool == "teleprompter" && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        ShowDropChooser();
    }

    private void OnPillDrop(object sender, DragEventArgs e)
    {
        if (!DropChooser.IsVisible)
        {
            // Ask AI declined it (e.g. the model can't read images): don't send it to the Shelf behind its back.
            if (_activeTool == "ask" && (DropPayload.HasImage(e.Data) || e.Data.GetDataPresent(DataFormats.UnicodeText))) return;
            if (_activeTool == "teleprompter" && e.Data.GetDataPresent(DataFormats.UnicodeText)) return;
        }
        if (DropPayload.Read(e.Data) is not { } payload) return;
        e.Handled = true;
        DeliverDrop(payload, DropTarget.Shelf);
    }

    public enum DropTarget
    {
        Shelf,
        Phone,
        Ask,
    }

    /// <summary>Puts a drop on the Shelf, offers it to the phone or hands it to Ask AI, then shows where it went.</summary>
    public async void DeliverDrop(DropPayload payload, DropTarget target)
    {
        HideDropChooser(restore: false);
        ShowTool(target switch { DropTarget.Phone => "phone", DropTarget.Ask => "ask", _ => "shelf" });
        if (target == DropTarget.Ask)
        {
            if (_views.GetValueOrDefault("ask") is Tools.AskAiView ask) await ask.AcceptDrop(payload);
            return;
        }
        var toPhone = target == DropTarget.Phone;
        var files = await payload.FilesAsync(Storage.Dir(toPhone ? "phone-out" : "shelf"));
        if (files is not null)
        {
            if (toPhone) PhoneTransferService.Instance.Send(files);
            else ShelfService.Instance.AddFiles(files, owned: payload.CreatesFiles);
        }
        else if (payload.Text is { } text)
        {
            if (toPhone) PhoneTransferService.Instance.SendText(text);
            else ShelfService.Instance.AddText(text);
        }
    }
}
