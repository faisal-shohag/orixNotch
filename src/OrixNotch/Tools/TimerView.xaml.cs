using System.ComponentModel;
using OrixNotch.Shell;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OrixNotch.Services;

namespace OrixNotch.Tools;

public partial class TimerView : UserControl, IEscapeHandler, IToolView
{
    private const string CustomLabel = "Custom…";
    private readonly List<int> _timerOptions = new(); // minutes behind each Timer list item (before "Custom…")

    private enum Mode { Timer, Stopwatch, Pomodoro }

    private Mode _mode = Mode.Pomodoro;
    private bool _syncingBoxes;

    private static PomodoroService Pom => PomodoroService.Instance;
    private static CountdownService Ctd => CountdownService.Instance;
    private static StopwatchService Sw => StopwatchService.Instance;

    public TimerView()
    {
        InitializeComponent();

        TimerTab.Checked += (_, _) => SetMode(Mode.Timer);
        StopwatchTab.Checked += (_, _) => SetMode(Mode.Stopwatch);
        PomodoroTab.Checked += (_, _) => SetMode(Mode.Pomodoro);

        PrimaryButton.Click += (_, _) => ActiveToggle();
        SecondaryButton.Click += (_, _) => ActiveSecondary();
        ResetButton.Click += (_, _) => ActiveReset();

        DurationBox.SelectionChanged += OnDurationChanged;
        SoundBox.SelectionChanged += OnSoundChanged;

        Pom.PropertyChanged += OnServiceChanged;
        Ctd.PropertyChanged += OnServiceChanged;
        Sw.PropertyChanged += OnServiceChanged;

        BuildDots();
        SetMode(Mode.Pomodoro);
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        PomodoroInfo.Visibility = mode == Mode.Pomodoro ? Visibility.Visible : Visibility.Collapsed;
        TimerInfo.Visibility = mode == Mode.Timer ? Visibility.Visible : Visibility.Collapsed;
        StopwatchInfo.Visibility = mode == Mode.Stopwatch ? Visibility.Visible : Visibility.Collapsed;

        _syncingBoxes = true;
        try
        {
            DurationBox.Items.Clear();
            SoundBox.Items.Clear();
            foreach (var s in SoundService.Names) SoundBox.Items.Add(s);

            switch (mode)
            {
                case Mode.Pomodoro:
                    foreach (var (_, _, label) in PomodoroService.DurationOptions) DurationBox.Items.Add(label);
                    DurationBox.SelectedIndex = IndexOfDuration(App.Settings.PomodoroWorkMin, App.Settings.PomodoroBreakMin);
                    SoundBox.SelectedItem = App.Settings.PomodoroSound;
                    SecondaryIcon.Kind = AppIcon.Forward;
                    SecondaryButton.ToolTip = "Skip";
                    break;
                case Mode.Timer:
                    // The presets, plus the current length if it's a custom one, then "Custom…".
                    _timerOptions.Clear();
                    _timerOptions.AddRange(CountdownService.Presets.Append(App.Settings.TimerMinutes).Distinct().Order());
                    foreach (var m in _timerOptions) DurationBox.Items.Add(CommandRegistry.FormatMinutes(m));
                    DurationBox.Items.Add(CustomLabel);
                    DurationBox.SelectedIndex = _timerOptions.IndexOf(App.Settings.TimerMinutes);
                    SoundBox.SelectedItem = App.Settings.TimerSound;
                    TimerPresetText.Text = $"{CommandRegistry.FormatMinutes(App.Settings.TimerMinutes)} countdown";
                    SecondaryIcon.Kind = AppIcon.Plus;
                    SecondaryButton.ToolTip = "Add 1 minute";
                    break;
                case Mode.Stopwatch:
                    DurationBox.Items.Add("Laps");
                    DurationBox.SelectedIndex = 0;
                    DurationBox.IsEnabled = false;
                    SoundBox.Items.Add("No Sound");
                    SoundBox.SelectedIndex = 0;
                    SoundBox.IsEnabled = false;
                    SecondaryIcon.Kind = AppIcon.Flag;
                    SecondaryButton.ToolTip = "Lap";
                    break;
            }

            if (mode != Mode.Stopwatch)
            {
                DurationBox.IsEnabled = true;
                SoundBox.IsEnabled = true;
            }
        }
        finally
        {
            _syncingBoxes = false;
        }

        Update();
    }

    private static int IndexOfDuration(int work, int rest)
    {
        var opts = PomodoroService.DurationOptions;
        for (var i = 0; i < opts.Length; i++)
            if (opts[i].Work == work && opts[i].BreakMin == rest) return i;
        return 1; // 25 / 5
    }

    private void OnDurationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBoxes || DurationBox.SelectedIndex < 0) return;
        if (_mode == Mode.Pomodoro)
        {
            var (work, breakMin, _) = PomodoroService.DurationOptions[DurationBox.SelectedIndex];
            Pom.SetDurations(work, breakMin);
        }
        else if (_mode == Mode.Timer)
        {
            if (DurationBox.SelectedIndex >= _timerOptions.Count)
            {
                ShowCustom(true); // "Custom…"
                return;
            }
            Ctd.SetMinutes(_timerOptions[DurationBox.SelectedIndex]);
            TimerPresetText.Text = $"{CommandRegistry.FormatMinutes(App.Settings.TimerMinutes)} countdown";
        }
    }

    private IDisposable? _customHold; // the pick was made below the notch: keep it open while the length is typed

    private void ShowCustom(bool on)
    {
        _customHold?.Dispose();
        _customHold = on ? NotchWindow.Instance?.HoldOpen() : null;
        CustomInput.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DurationBox.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            CustomInput.Clear();
            Dispatcher.BeginInvoke(() => CustomInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else SetMode(_mode); // rebuild the list on the current length
    }

    private void OnCustomKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (CommandRegistry.ParseMinutes(CustomInput.Text) is not { } minutes)
        {
            Field.ShowError(CustomInput);
            return;
        }
        Ctd.SetMinutes(minutes);
        ShowCustom(false);
    }

    private void OnCustomLostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (CustomInput.Visibility == Visibility.Visible) ShowCustom(false);
    }

    /// <summary>Esc while typing a custom length goes back to the list instead of closing the notch.</summary>
    public bool OnEscape()
    {
        if (CustomInput.Visibility != Visibility.Visible) return false;
        ShowCustom(false);
        return true;
    }

    private void OnSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBoxes || SoundBox.SelectedItem is not string sound) return;
        if (_mode == Mode.Pomodoro) Pom.SelectedSound = sound;
        else if (_mode == Mode.Timer) Ctd.SelectedSound = sound;
    }

    private void ActiveToggle()
    {
        switch (_mode)
        {
            case Mode.Pomodoro: Pom.ToggleCommand.Execute(null); break;
            case Mode.Timer: Ctd.ToggleCommand.Execute(null); break;
            case Mode.Stopwatch: Sw.ToggleCommand.Execute(null); break;
        }
    }

    private void ActiveSecondary()
    {
        switch (_mode)
        {
            case Mode.Pomodoro: Pom.SkipCommand.Execute(null); break;
            case Mode.Timer: Ctd.AddMinuteCommand.Execute(null); break;
            case Mode.Stopwatch: Sw.LapCommand.Execute(null); break;
        }
    }

    private void ActiveReset()
    {
        switch (_mode)
        {
            case Mode.Pomodoro: Pom.ResetCommand.Execute(null); break;
            case Mode.Timer: Ctd.ResetCommand.Execute(null); break;
            case Mode.Stopwatch: Sw.ResetCommand.Execute(null); break;
        }
    }

    private bool _shown;

    public void OnShown()
    {
        _shown = true;
        Update();
    }

    public void OnHidden() => _shown = false;

    // Services tick while the tool is hidden (and for modes not on screen): only the visible one redraws.
    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        object active = _mode switch { Mode.Pomodoro => Pom, Mode.Timer => Ctd, _ => Sw };
        if (_shown && sender == active) Update();
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        for (var i = 0; i < 4; i++)
            Dots.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) });
    }

    private void Update()
    {
        var running = _mode switch
        {
            Mode.Pomodoro => Pom.IsRunning,
            Mode.Timer => Ctd.IsRunning,
            _ => Sw.IsRunning,
        };
        PrimaryIcon.Kind = running ? AppIcon.Pause : AppIcon.Play;
        PrimaryButton.ToolTip = running ? "Pause" : "Start";

        string text;
        double progress;
        switch (_mode)
        {
            case Mode.Timer:
                text = Ctd.RemainingText;
                progress = Ctd.Progress;
                Arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                break;
            case Mode.Stopwatch:
                text = Sw.ElapsedText;
                progress = 0;
                Arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                LapsText.Text = Sw.LapsText;
                break;
            default:
                text = Pom.RemainingText;
                progress = Pom.Progress;
                ModeText.Text = Pom.ModeText;
                ModeIcon.Kind = Pom.IsBreak ? AppIcon.Moon : AppIcon.Bolt;
                SessionText.Text = Pom.SessionText;
                SessionsTodayText.Text = Pom.SessionsTodayText;
                Arc.SetResourceReference(Shape.StrokeProperty, Pom.IsBreak ? "GoodBrush" : "AccentBrush");
                UpdateDots();
                break;
        }

        RingText.Text = text;
        Arc.Data = NotchWindow.BuildMiniArc(progress, 118, 5.5);
    }

    private void UpdateDots()
    {
        var filled = Pom.SessionNumber - 1;
        if (Pom.Completed > 0 && filled == 0) filled = 4; // just finished a full cycle
        for (var i = 0; i < Dots.Children.Count; i++)
        {
            if (Dots.Children[i] is Ellipse dot)
                dot.SetResourceReference(Shape.FillProperty, i < filled ? "AccentBrush" : "TrackBrush");
        }
    }

}
