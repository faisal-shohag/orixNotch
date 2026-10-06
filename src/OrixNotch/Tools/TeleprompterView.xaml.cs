using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// Teleprompter that sits right under the webcam. Text scrolls smoothly upward; click or Space
/// pauses, the wheel nudges, the grab handle resizes the notch, and the opacity slider makes the
/// notch see-through. While it is playing the notch stays open even when the pointer leaves.
/// </summary>
public partial class TeleprompterView : UserControl, IToolView
{
    private const double MinHeight = 120;
    private const double MaxHeight = 360;
    private const double LineSpacing = 1.35;
    private const double SideMargin = 24;

    private bool _editing;
    private bool _scrolling;
    private readonly System.Windows.Threading.DispatcherTimer _ticker = new(System.Windows.Threading.DispatcherPriority.Render);
    private readonly System.Windows.Threading.DispatcherTimer _watchdog = new(System.Windows.Threading.DispatcherPriority.Background);
    private double _watchOffset;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _clockOffset;
    private double _offset;
    private int _startRetries;
    private bool _dragging;    private double _dragStartY;
    private double _dragStartHeight;
    private IDisposable? _keepOpen;
    private PrompterSurface? _surface;
    private bool _surfaceShown;

    private static TeleprompterService Service => TeleprompterService.Instance;

    public TeleprompterView()
    {
        InitializeComponent();
        _ticker.Tick += OnTick;
        _watchdog.Interval = TimeSpan.FromMilliseconds(500);
        _watchdog.Tick += OnWatchdog;
        var s = App.Settings;
        SpeedSlider.Value = s.PrompterSpeed;
        SizeSlider.Value = s.PrompterFontSize;
        OpacitySlider.Value = s.PrompterOpacity;
        Mirror.ScaleX = s.PrompterMirror ? -1 : 1;

        Service.PropertyChanged += OnServiceChanged;
        Service.Restarted += () =>
        {
            var wasScrolling = _scrolling;
            if (wasScrolling) StopScroll();
            _offset = 0;
            ApplyOffset();
            if (wasScrolling) StartScroll();
        };
        Editor.Text = Service.Script;
        ThemeService.Changed += UpdateFades;
        UpdateFades();
        UpdateMode();
        IsVisibleChanged += (_, _) => UpdateSurface();
        // Listen from the start: the first chance to show the surface is when the opening animation settles.
        if (NotchWindow.Instance is { } notch)
        {
            notch.GeometryChanging += HideSurface;
            notch.GeometrySettled += UpdateSurface;
        }
    }

    // ---------------------------------------------------------------- solid scrolling surface

    private PrompterSurface Surface
    {
        get
        {
            if (_surface is not null) return _surface;
            var notch = NotchWindow.Instance!;
            _surface = new PrompterSurface(notch);
            _surface.ApplyBackground();
            _surface.Clicked += () => Service.Toggle();
            _surface.Wheel += Nudge;
            return _surface;
        }
    }

    /// <summary>Single-layer rendering: the WPF copy inside the notch is the only text.
    /// The WinForms overlay window caused double-text and Z-order issues (hidden behind
    /// the opaque notch, visible only when the notch turned translucent), so it stays
    /// hidden and the in-notch text scrolls at every opacity.</summary>
    private void UpdateSurface()
    {
        _surfaceShown = false;
        _surface?.Hide();
        // Make sure the in-notch copy is fully visible (recovers from the old dual-layer mode).
        ScriptText.Opacity = 1;
        FadeTop.Opacity = 1;
        FadeBottom.Opacity = 1;
        PausedHint.Opacity = 1;
        ApplyOffset();
    }

    private void HideSurface()
    {
        if (!_surfaceShown) return;
        _surfaceShown = false;
        _surface?.Hide();
        // Restore the in-notch copy that takes over while the surface is gone.
        ScriptText.Opacity = 1;
        FadeTop.Opacity = 1;
        FadeBottom.Opacity = 1;
        PausedHint.Opacity = 1;
        ApplyOffset(); // the in-notch copy takes over at the current position
    }

    public void OnShown()
    {
        NotchWindow.Instance?.SetToolSize(690, App.Settings.PrompterHeight);
        NotchWindow.Instance?.SetBackgroundOpacity(App.Settings.PrompterOpacity);
        UpdateMode();
    }

    /// <summary>Called when the notch closes (Esc, hotkey, click elsewhere) or another tab opens.</summary>
    public void OnHidden()
    {
        if (_editing) FinishEditing();
        Service.IsRunning = false;
        HideSurface();
    }

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TeleprompterService.IsRunning)) UpdateRunning();
    }

    // ---------------------------------------------------------------- modes

    private void UpdateMode()
    {
        var hasScript = Service.HasScript;
        Placeholder.Visibility = !_editing && !hasScript ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        Stage.Visibility = !_editing && hasScript ? Visibility.Visible : Visibility.Collapsed;
        EditIcon.Kind = _editing ? AppIcon.Check : AppIcon.Pencil;
        EditButton.ToolTip = _editing ? "Done" : "Edit script";
        PlayButton.IsEnabled = !_editing && hasScript;
        ScriptText.Text = Service.Script;
        ApplyFont();
        UpdateRunning();
        Dispatcher.BeginInvoke(UpdateSurface, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyFont()
    {
        ScriptText.FontSize = App.Settings.PrompterFontSize;
        // LineHeight is in DIPs, not a multiplier.
        ScriptText.LineHeight = App.Settings.PrompterFontSize * LineSpacing;
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_editing)
        {
            FinishEditing();
            return;
        }
        Service.IsRunning = false;
        _editing = true;
        Editor.Text = Service.Script;
        UpdateMode();
        Editor.Focus();
        Editor.CaretIndex = Editor.Text.Length;
    }

    private void FinishEditing()
    {
        _editing = false;
        Service.Script = Editor.Text.TrimEnd();
        _offset = 0;
        UpdateMode();
        ApplyOffset();
        Keyboard.ClearFocus();
    }

    // ---------------------------------------------------------------- scrolling

    private void OnPlay(object sender, RoutedEventArgs e) => Service.Toggle();

    private void OnRestart(object sender, RoutedEventArgs e) => Service.Restart();

    private void UpdateRunning()
    {
        var running = Service.IsRunning && !_editing;
        if (running && !_scrolling && _offset >= EndOffset - 1)
        {
            // Pressing play at the end starts over.
            _offset = 0;
            ApplyOffset();
        }
        PlayIcon.Kind = running ? AppIcon.Pause : AppIcon.Play;
        PausedHint.Visibility = !running && !_editing && Service.HasScript && _offset > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_surface is not null) _surface.ShowPaused = PausedHint.Visibility == Visibility.Visible;

        // Keep the notch open while reading; the pointer is usually elsewhere.
        if (running && _keepOpen is null) _keepOpen = NotchWindow.Instance?.HoldOpen();
        else if (!running && _keepOpen is not null)
        {
            _keepOpen.Dispose();
            _keepOpen = null;
        }

        if (running && !_scrolling) StartScroll();
        else if (!running && _scrolling) StopScroll();
        if (running && !_watchdog.IsEnabled)
        {
            _watchOffset = _offset;
            _watchdog.Start();
        }
    }

    /// <summary>
    /// Steps the text one whole pixel at a time on a timer paced to the reading speed, redrawing only
    /// when it actually moves. The moving text is drawn by <see cref="PrompterSurface"/>, a normal
    /// window, so each step is cheap; the transparent notch is left alone while scrolling.
    /// </summary>
    private void StartScroll()
    {
        if (EndOffset - _offset <= 0)
        {
            // Play pressed before layout measured the script (opening animation
            // still running): wait for a layout pass and try again instead of
            // stopping playback. Genuinely at the end (or empty) stops below.
            if (Service.HasScript && !ContentMeasured() && _startRetries < 5)
            {
                _startRetries++;
                Dispatcher.BeginInvoke(() =>
                {
                    if (Service.IsRunning && !_scrolling) StartScroll();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
            _startRetries = 0;
            Service.IsRunning = false;
            return;
        }
        _startRetries = 0;
        _scrolling = true;
        _watchOffset = _offset;
        _watchdog.Start();
        _clock.Restart();
        _clockOffset = _offset;
        _ticker.Interval = TimeSpan.FromSeconds(Math.Clamp(1 / TeleprompterService.PixelsPerSecond, 1 / 60.0, 0.1));
        _ticker.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Position from elapsed time, so a late tick catches up instead of drifting.
        _offset = Math.Min(EndOffset, _clockOffset + _clock.Elapsed.TotalSeconds * TeleprompterService.PixelsPerSecond);
        ApplyOffset();
        if (_offset >= EndOffset) Service.IsRunning = false;
    }

    /// <summary>Freezes the text where it is right now.</summary>
    private void StopScroll()
    {
        _ticker.Stop();
        _watchdog.Stop();
        _clock.Stop();
        _scrolling = false;
        ApplyOffset();
    }

    /// <summary>
    /// Safety net: if playback is on but the offset hasn't moved (dead ticker)
    /// or the overlay was lost, recover instead of sitting frozen on pause.
    /// </summary>
    private void OnWatchdog(object? sender, EventArgs e)
    {
        if (!Service.IsRunning)
        {
            _watchdog.Stop();
            return;
        }
        if (_surfaceShown && (_surface is null || _surface.IsDisposed || !_surface.IsShown)) HideSurface();
        if (!_scrolling)
        {
            StartScroll();
            return;
        }
        if (EndOffset - _offset > 1 && Math.Abs(_offset - _watchOffset) < 0.5)
        {
            _ticker.Stop();
            _ticker.Start();
            _surface?.Invalidate();
            ApplyOffset();
        }
        _watchOffset = _offset;
    }

    /// <summary>Re-times a running scroll after speed, size or position changes.</summary>
    private void Retime()
    {
        if (!_scrolling) return;
        StopScroll();
        StartScroll();
    }

    /// <summary>Scrolled far enough that the last line has passed the reading line.</summary>
    private double EndOffset => Math.Max(0, ScriptText.ActualHeight);

    /// <summary>The reading line: the first line starts 40% down the stage and the text moves up from there.</summary>
    private double BaseY => Math.Round(Stage.ActualHeight * 0.4);

    /// <summary>Whether any copy of the script has a measurable height yet.</summary>
    private bool ContentMeasured() => ScriptText.ActualHeight > 0;

    /// <summary>Moves the in-notch text; the single rendering layer, visible at every opacity.</summary>
    private void ApplyOffset()
    {
        var y = Math.Round(BaseY - _offset);
        if (y != Scroll.Y) Scroll.Y = y;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScriptText.Width = Math.Max(50, e.NewSize.Width - 2 * SideMargin);
        if (_scrolling) Dispatcher.BeginInvoke(Retime); // after the text re-wraps
        else ApplyOffset();
        Dispatcher.BeginInvoke(UpdateSurface, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Gradients from the notch color (at its current opacity) to transparent over the text edges.</summary>
    private void UpdateFades()
    {
        var pill = ((SolidColorBrush)ThemeService.Get("PillBrush")).Color;
        var alpha = (byte)Math.Round(255 * Math.Clamp(App.Settings.PrompterOpacity, 0.15, 1));
        var solid = Color.FromArgb(alpha, pill.R, pill.G, pill.B);
        var clear = Color.FromArgb(0, pill.R, pill.G, pill.B);
        var top = new LinearGradientBrush(solid, clear, 90);
        var bottom = new LinearGradientBrush(clear, solid, 90);
        top.Freeze();
        bottom.Freeze();
        FadeTop.Fill = top;
        FadeBottom.Fill = bottom;
    }

    private void OnStageClick(object sender, MouseButtonEventArgs e) => Service.Toggle();

    /// <summary>Wheel nudges the script up/down, e.g. to back up a line while paused.</summary>
    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        Nudge(e.Delta);
        e.Handled = true;
    }

    private void Nudge(int delta)
    {
        var wasScrolling = _scrolling;
        if (wasScrolling) StopScroll();
        _offset = Math.Clamp(_offset - delta * 0.4, 0, EndOffset);
        ApplyOffset();
        if (wasScrolling) StartScroll();
        UpdateRunning();
    }

    // ---------------------------------------------------------------- controls

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Settings.PrompterSpeed = e.NewValue;
        SettingsService.Save(notify: false);
        Retime();
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Settings.PrompterFontSize = Math.Round(e.NewValue);
        ApplyFont();
        if (_surfaceShown) _surface!.SetContent(Service.Script, App.Settings.PrompterFontSize);
        if (_scrolling) Dispatcher.BeginInvoke(Retime); // after the text re-wraps
        SettingsService.Save(notify: false);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Settings.PrompterOpacity = e.NewValue;
        NotchWindow.Instance?.SetBackgroundOpacity(e.NewValue);
        UpdateFades();
        _surface?.ApplyBackground();
        SettingsService.Save(notify: false);
    }

    private void OnMirror(object sender, RoutedEventArgs e)
    {
        App.Settings.PrompterMirror = !App.Settings.PrompterMirror;
        Mirror.ScaleX = App.Settings.PrompterMirror ? -1 : 1;
        if (_surface is not null) _surface.Mirrored = App.Settings.PrompterMirror;
        SettingsService.Save(notify: false);
    }

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartY = PointToScreen(e.GetPosition(this)).Y;
        _dragStartHeight = App.Settings.PrompterHeight;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var dy = (PointToScreen(e.GetPosition(this)).Y - _dragStartY) / dpi;
        App.Settings.PrompterHeight = Math.Clamp(_dragStartHeight + dy, MinHeight, MaxHeight);
        NotchWindow.Instance?.SetToolSize(690, App.Settings.PrompterHeight);
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grip.ReleaseMouseCapture();
        SettingsService.Save(notify: false);
    }
}
