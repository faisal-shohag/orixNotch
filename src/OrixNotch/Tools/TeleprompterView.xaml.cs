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
/// <remarks>
/// Motion runs on the display's frame clock (<see cref="CompositionTarget.Rendering"/>) and moves the
/// cached text bitmap by fractional pixels, so it glides instead of hopping a pixel at a time. Speed
/// eases toward its target, which softens play, pause and slider changes; wheel nudges glide too.
/// The loop only runs while something is moving and snaps to a whole pixel when it stops, so paused
/// text is crisp and an idle prompter costs nothing.
/// </remarks>
public partial class TeleprompterView : UserControl, IToolView
{
    private const double MinPrompterHeight = 120;
    private const double MaxPrompterHeight = 360;
    private const double LineSpacing = 1.35;
    private const double SideMargin = 24;
    private const double SpeedEase = 0.18;   // seconds: time constant for speed changes
    private const double NudgeEase = 0.09;   // seconds: time constant for wheel nudges

    private bool _editing;
    private bool _looping;
    private TimeSpan _lastFrame;
    private double _offset;        // DIPs scrolled from the start
    private double _velocity;      // DIPs per second, eased toward the target speed
    private double _pendingNudge;  // DIPs still to glide from wheel input
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;
    private IDisposable? _keepOpen;

    private static TeleprompterService Service => TeleprompterService.Instance;

    public TeleprompterView()
    {
        InitializeComponent();
        var s = App.Settings;
        SpeedSlider.Value = s.PrompterSpeed;
        SizeSlider.Value = s.PrompterFontSize;
        OpacitySlider.Value = s.PrompterOpacity;
        Mirror.ScaleX = s.PrompterMirror ? -1 : 1;

        Service.PropertyChanged += OnServiceChanged;
        Service.Restarted += () =>
        {
            _offset = 0;
            _pendingNudge = 0;
            ApplyOffset();
            UpdateRunning();
        };
        Editor.Text = Service.Script;
        ScriptText.SizeChanged += OnScriptSizeChanged;
        ThemeService.Changed += UpdateFades;
        UpdateFades();
        UpdateMode();
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
        StopLoop();
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
        // The reading line depends on the stage height, known after layout.
        Dispatcher.BeginInvoke(ApplyOffset, System.Windows.Threading.DispatcherPriority.Loaded);
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
        _pendingNudge = 0;
        UpdateMode();
        ApplyOffset();
        Keyboard.ClearFocus();
    }

    // ---------------------------------------------------------------- scrolling

    private void OnPlay(object sender, RoutedEventArgs e) => Service.Toggle();

    private void OnRestart(object sender, RoutedEventArgs e) => Service.Restart();

    private bool Running => Service.IsRunning && !_editing;

    private void UpdateRunning()
    {
        var running = Running;
        if (running && !_looping && ContentMeasured() && _offset >= EndOffset - 1)
        {
            // Pressing play at the end starts over.
            _offset = 0;
            ApplyOffset();
        }
        PlayIcon.Kind = running ? AppIcon.Pause : AppIcon.Play;
        UpdatePausedHint();

        // Keep the notch open while reading; the pointer is usually elsewhere.
        if (running && _keepOpen is null) _keepOpen = NotchWindow.Instance?.HoldOpen();
        else if (!running && _keepOpen is not null)
        {
            _keepOpen.Dispose();
            _keepOpen = null;
        }

        // Pausing lets the loop ease the text to a stop; it ends itself once still.
        if (running) StartLoop();
    }

    private void UpdatePausedHint() =>
        PausedHint.Visibility = !Running && !_editing && Service.HasScript && _offset > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void StartLoop()
    {
        if (_looping) return;
        _looping = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Stops immediately and snaps to a whole pixel so the resting text is crisp.</summary>
    private void StopLoop()
    {
        if (_looping)
        {
            CompositionTarget.Rendering -= OnFrame;
            _looping = false;
        }
        _velocity = 0;
        _offset += _pendingNudge;
        _pendingNudge = 0;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        _offset = Math.Clamp(Math.Round(_offset * dpi) / dpi, 0, EndOffset);
        ApplyOffset();
        UpdatePausedHint();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering can fire more than once per frame
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        dt = Math.Clamp(dt, 0, 0.1); // a stalled frame shouldn't make the text jump

        var running = Running;
        if (running && !ContentMeasured())
        {
            // Play pressed before layout measured the script (opening animation still running).
            if (!Service.HasScript) Service.IsRunning = false;
            return;
        }

        var target = running ? TeleprompterService.PixelsPerSecond : 0;
        _velocity += (target - _velocity) * (1 - Math.Exp(-dt / SpeedEase));
        var nudge = _pendingNudge * (1 - Math.Exp(-dt / NudgeEase));
        _pendingNudge -= nudge;
        _offset = Math.Clamp(_offset + _velocity * dt + nudge, 0, EndOffset);
        ApplyOffset();

        if (running && _offset >= EndOffset)
        {
            Service.IsRunning = false; // reached the end
            StopLoop();
            return;
        }
        if (!running && _velocity < 0.5 && Math.Abs(_pendingNudge) < 0.25) StopLoop();
    }

    /// <summary>Scrolled far enough that the last line has passed the reading line.</summary>
    private double EndOffset => Math.Max(0, ScriptText.ActualHeight);

    /// <summary>The reading line: the first line starts 40% down the stage and the text moves up from there.</summary>
    private double BaseY => Math.Round(Stage.ActualHeight * 0.4);

    private bool ContentMeasured() => ScriptText.ActualHeight > 0;

    /// <summary>Moves the cached text; fractional while gliding, whole pixels at rest.</summary>
    private void ApplyOffset() => Scroll.Y = BaseY - _offset;

    /// <summary>Re-wrapping (text size, notch width) changes the script height: keep the same place in it.</summary>
    private void OnScriptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || e.PreviousSize.Height <= 0) return;
        var ratio = e.NewSize.Height / e.PreviousSize.Height;
        _offset = Math.Clamp(_offset * ratio, 0, EndOffset);
        _pendingNudge *= ratio;
        ApplyOffset();
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScriptText.Width = Math.Max(50, e.NewSize.Width - 2 * SideMargin);
        ApplyOffset();
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

    /// <summary>Wheel nudges the script up/down, e.g. to back up a line while paused. The move glides.</summary>
    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        _pendingNudge -= e.Delta * 0.4;
        // Don't queue travel past either end.
        _pendingNudge = Math.Clamp(_pendingNudge, -_offset, EndOffset - _offset);
        StartLoop();
        UpdatePausedHint();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- controls

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Settings.PrompterSpeed = e.NewValue; // a running scroll eases to the new speed
        SettingsService.Save(notify: false);
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Settings.PrompterFontSize = Math.Round(e.NewValue);
        ApplyFont();
        SettingsService.Save(notify: false);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Settings.PrompterOpacity = e.NewValue;
        NotchWindow.Instance?.SetBackgroundOpacity(e.NewValue);
        UpdateFades();
        SettingsService.Save(notify: false);
    }

    private void OnMirror(object sender, RoutedEventArgs e)
    {
        App.Settings.PrompterMirror = !App.Settings.PrompterMirror;
        Mirror.ScaleX = App.Settings.PrompterMirror ? -1 : 1;
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
        App.Settings.PrompterHeight = Math.Clamp(_dragStartHeight + dy, MinPrompterHeight, MaxPrompterHeight);
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
