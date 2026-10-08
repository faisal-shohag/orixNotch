using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Dashboard: greeting + clock, Now Playing card over blurred album art, and today's events + open to-dos.</summary>
public partial class HomeView : UserControl, IToolView
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private static NowPlayingService Player => NowPlayingService.Instance;
    private string _clockText = "";

    public HomeView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            Player.Tick();
            UpdateClock();
        };
        Player.PropertyChanged += OnPlayerChanged;
        ThemeService.Changed += UpdateCard;
        ProfileService.Changed += () =>
        {
            UpdateProfile();
            if (IsVisible) OnShown();
        };
        UpdateCard();
    }

    public void OnShown()
    {
        UpdateProfile();
        _clockText = "";
        UpdateClock();

        var today = Storage.Load<List<CalendarEvent>>("events.json")
            .Where(e => e.Date.Date == DateTime.Today)
            .OrderBy(e => e.Time)
            .ToList();
        Events.ItemsSource = today.Take(2).ToList();
        SetCount(EventCountPill, EventCount, today.Count);
        NoEvents.Visibility = today.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var open = Storage.Load<List<TodoItem>>("todos.json")
            .Where(t => !t.Done)
            .OrderByDescending(t => t.Starred)
            .ToList();
        Reminders.ItemsSource = open.Take(2).ToList();
        SetCount(ReminderCountPill, ReminderCount, open.Count);
        NoReminders.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Player.Tick();
        UpdateCard();
        _timer.Start();

        Rise(Card, 0);
        Rise(TodayCard, 60);
        Rise(RemindersCard, 120);
    }

    public void OnHidden() => _timer.Stop();

    private static void SetCount(Border pill, TextBlock label, int count)
    {
        label.Text = count.ToString(CultureInfo.CurrentCulture);
        pill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Clock + greeting; only touches the text when the minute changes.</summary>
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        var twelveHour = culture.DateTimeFormat.ShortTimePattern.Contains('h');
        var text = now.ToString(twelveHour ? "h:mm" : "HH:mm", culture);
        if (text == _clockText) return;
        _clockText = text;

        Clock.Text = text;
        AmPm.Text = twelveHour ? now.ToString("tt", culture) : "";
        AmPm.Visibility = AmPm.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var hour = now.Hour;
        var part = hour < 5 ? "evening" : hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";
        Greeting.Text = $"GOOD {part.ToUpperInvariant()}";
        GreetingIcon.Kind = hour is >= 6 and < 18 ? AppIcon.Sun : AppIcon.Moon;
        NameLine.Text = ProfileService.FirstName;
        DateLine.Text = now.ToString("ddd, MMM d", culture);
    }

    /// <summary>Cards fade in and rise a few pixels, staggered, each time Home opens.</summary>
    private static void Rise(UIElement element, int delayMs)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(320);
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ease });
        var shift = new TranslateTransform(0, 8);
        element.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { BeginTime = begin, EasingFunction = ease });
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingService.ArtTint) or nameof(NowPlayingService.IsPlaying)
            or nameof(NowPlayingService.Artist) or nameof(NowPlayingService.SourceApp) or nameof(NowPlayingService.Art)
            or nameof(NowPlayingService.HasSession))
            UpdateCard();
    }

    private void UpdateCard()
    {
        PlayIcon.Kind = Player.IsPlaying ? AppIcon.Pause : AppIcon.Play;
        ArtistLine.Text = string.Join(" · ", new[] { Player.Artist, Player.SourceApp }.Where(s => !string.IsNullOrWhiteSpace(s)));
        NowLabel.Text = Player.IsPlaying ? "NOW PLAYING" : Player.HasSession ? "PAUSED" : "NOTHING PLAYING";

        var surface = ((SolidColorBrush)ThemeService.Get("SurfaceBrush")).Color;
        var hasArt = Player.Art is not null;
        Backdrop.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;

        if (hasArt)
        {
            // Blurred art shows through on the left and top; the scrim deepens toward the controls for contrast.
            var scrim = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x40, surface.R, surface.G, surface.B), 0),
                    new GradientStop(Color.FromArgb(0xA8, surface.R, surface.G, surface.B), 0.55),
                    new GradientStop(Color.FromArgb(0xE0, surface.R, surface.G, surface.B), 1),
                },
            };
            scrim.Freeze();
            Scrim.Background = scrim;
            Card.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        }
        else if (Player.ArtTint is { } tint)
        {
            Scrim.Background = null;
            var brush = new LinearGradientBrush(Mix(tint, surface, 0.55), surface, 0);
            brush.Freeze();
            Card.Background = brush;
        }
        else
        {
            Scrim.Background = null;
            Card.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        }
    }

    /// <summary>Clip the card's contents (the blurred backdrop) to its rounded corners.</summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var inner = new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2));
        ((FrameworkElement)Card.Child).Clip = new RectangleGeometry(inner, 17, 17);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R * (1 - t) + b.R * t),
        (byte)(a.G * (1 - t) + b.G * t),
        (byte)(a.B * (1 - t) + b.B * t));

    private void UpdateProfile()
    {
        var first = ProfileService.FirstName;
        Initial.Text = first[..1].ToUpper(CultureInfo.CurrentCulture);
        var image = ProfileService.LoadAvatar();
        AvatarBrush.ImageSource = image;
        AvatarImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAvatarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        using (NotchWindow.Instance?.HoldOpen()) ProfileService.PickAvatar(NotchWindow.Instance);
    }

    private void OnOpenNowPlaying(object sender, RoutedEventArgs e) =>
        NotchWindow.Instance?.ShowTool("nowplaying", backTo: "home");

    private void OnOpenCalendar(object sender, RoutedEventArgs e) =>
        NotchWindow.Instance?.ShowTool("calendar", backTo: "home");

    private void OnOpenTodos(object sender, RoutedEventArgs e) =>
        NotchWindow.Instance?.ShowTool("todos", backTo: "home");
}
