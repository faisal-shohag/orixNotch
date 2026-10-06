using System.ComponentModel;
using OrixNotch.Shell;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OrixNotch.Services;

namespace OrixNotch.Tools;

public partial class TimerView : UserControl
{
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
            foreach (var s in PomodoroService.SoundOptions) SoundBox.Items.Add(s);

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
                    foreach (var m in CountdownService.Presets) DurationBox.Items.Add(m == 1 ? "1 min" : $"{m} min");
                    DurationBox.SelectedIndex = Math.Max(0, Array.IndexOf(CountdownService.Presets, App.Settings.TimerMinutes));
                    SoundBox.SelectedItem = App.Settings.TimerSound;
                    TimerPresetText.Text = $"{App.Settings.TimerMinutes} minute countdown";
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
            Ctd.SetMinutes(CountdownService.Presets[DurationBox.SelectedIndex]);
            TimerPresetText.Text = $"{App.Settings.TimerMinutes} minute countdown";
        }
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

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e) => Update();

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
        Arc.Data = BuildArc(progress);
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

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="progress"/> of the ring.</summary>
    private static Geometry BuildArc(double progress)
    {
        const double size = 118, inset = 5.5;
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
}
