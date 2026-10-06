using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Player page: art + track on the left, time-synced lyrics (or big controls) on the right.</summary>
public partial class NowPlayingView : UserControl, IToolView
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<LyricLine>? _lines;
    private string _trackKey = "";
    private int _current = -2;
    private static NowPlayingService Service => NowPlayingService.Instance;

    public NowPlayingView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Service.PropertyChanged += OnServiceChanged;
        UpdatePlayGlyph();
    }

    public void OnShown()
    {
        Service.Tick();
        _ = LoadLyricsAsync();
        UpdateTint();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingService.IsPlaying):
                UpdatePlayGlyph();
                break;
            case nameof(NowPlayingService.Title) or nameof(NowPlayingService.Artist) or nameof(NowPlayingService.Duration):
                if (IsVisible) _ = LoadLyricsAsync();
                break;
            case nameof(NowPlayingService.ArtTint):
                UpdateTint();
                break;
        }
    }

    private void Tick()
    {
        Service.Tick();
        if (_lines is null) return;
        var index = LyricsService.IndexAt(_lines, Service.Position);
        if (index != _current) Highlight(index);
    }

    private async Task LoadLyricsAsync()
    {
        var key = $"{Service.Artist}|{Service.Title}";
        if (key == _trackKey && _lines is not null) return;
        _trackKey = key;
        _lines = null;
        _current = -2;
        LyricsLines.Children.Clear();
        ShowLyrics(false);

        if (!App.Settings.LyricsEnabled || !Service.HasSession)
        {
            LyricsStatus.Text = "";
            return;
        }

        LyricsStatus.Text = "Looking for lyrics…";
        var lines = await LyricsService.GetAsync(Service.Title, Service.Artist, Service.Duration);
        if (key != _trackKey) return; // track changed meanwhile
        if (lines is null)
        {
            LyricsStatus.Text = "No synced lyrics for this track";
            return;
        }

        _lines = lines;
        foreach (var line in lines)
        {
            LyricsLines.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(line.Text) ? "♪" : line.Text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 3),
                Opacity = 0.35,
            });
        }
        ShowLyrics(true);
        Tick();
    }

    private void ShowLyrics(bool on)
    {
        LyricsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ControlsPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Brightens the current line and glides it to the vertical center.</summary>
    private void Highlight(int index)
    {
        _current = index;
        for (var i = 0; i < LyricsLines.Children.Count; i++)
        {
            var tb = (TextBlock)LyricsLines.Children[i];
            var target = i == index ? 1.0 : Math.Abs(i - index) == 1 ? 0.5 : 0.3;
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(250)));
        }
        if (index < 0) return;

        LyricsLines.UpdateLayout();
        var line = (FrameworkElement)LyricsLines.Children[index];
        var y = line.TranslatePoint(new Point(0, 0), LyricsLines).Y + LyricsLines.Margin.Top;
        var offset = Math.Max(0, y - (LyricsScroll.ViewportHeight - line.ActualHeight) / 2);
        AnimateScroll(offset);
    }

    private void AnimateScroll(double to)
    {
        var from = LyricsScroll.VerticalOffset;
        var start = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / 320);
            var eased = 1 - Math.Pow(1 - t, 3);
            LyricsScroll.ScrollToVerticalOffset(from + (to - from) * eased);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private void UpdatePlayGlyph() =>
        PlayIcon.Kind = Service.IsPlaying ? AppIcon.Pause : AppIcon.Play;

    private void UpdateTint()
    {
        var surface = ((SolidColorBrush)ThemeService.Get("SurfaceBrush")).Color;
        if (Service.ArtTint is { } tint)
        {
            var mixed = Color.FromRgb((byte)((tint.R + surface.R * 2) / 3), (byte)((tint.G + surface.G * 2) / 3), (byte)((tint.B + surface.B * 2) / 3));
            var brush = new LinearGradientBrush(mixed, surface, 0);
            brush.Freeze();
            Card.Background = brush;
        }
        else
        {
            Card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        }
    }

    private async void OnSeek(object sender, MouseButtonEventArgs e)
    {
        if (Service.Duration <= 0 || Progress.ActualWidth <= 0) return;
        var fraction = e.GetPosition(Progress).X / Progress.ActualWidth;
        await Service.SeekAsync(Math.Clamp(fraction, 0, 1) * Service.Duration);
    }
}
