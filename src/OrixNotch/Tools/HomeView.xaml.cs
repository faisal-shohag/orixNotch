using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Dashboard: greeting, Now Playing card and today's events + open to-dos.</summary>
public partial class HomeView : UserControl, IToolView
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private static NowPlayingService Player => NowPlayingService.Instance;

    public HomeView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Player.Tick();
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
        var hour = DateTime.Now.Hour;
        var part = hour < 5 ? "evening" : hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";
        Greeting.Text = $"Good {part}, {ProfileService.FirstName}!";
        DateLine.Text = DateTime.Now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);

        var events = Storage.Load<List<CalendarEvent>>("events.json")
            .Where(e => e.Date.Date == DateTime.Today)
            .OrderBy(e => e.Time)
            .Take(2)
            .ToList();
        Events.ItemsSource = events;
        NoEvents.Visibility = events.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        var todos = Storage.Load<List<TodoItem>>("todos.json")
            .Where(t => !t.Done)
            .OrderByDescending(t => t.Starred)
            .Take(2)
            .ToList();
        Reminders.ItemsSource = todos;
        NoReminders.Visibility = todos.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        Player.Tick();
        UpdateCard();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingService.ArtTint) or nameof(NowPlayingService.IsPlaying)
            or nameof(NowPlayingService.Artist) or nameof(NowPlayingService.SourceApp))
            UpdateCard();
    }

    private void UpdateCard()
    {
        PlayIcon.Kind = Player.IsPlaying ? AppIcon.Pause : AppIcon.Play;
        ArtistLine.Text = string.Join(" · ", new[] { Player.Artist, Player.SourceApp }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // Soft gradient from the album art's dominant color into the surface color.
        var surface = ((SolidColorBrush)ThemeService.Get("SurfaceBrush")).Color;
        if (Player.ArtTint is { } tint)
        {
            var brush = new LinearGradientBrush(Mix(tint, surface, 0.55), surface, 0);
            brush.Freeze();
            Card.Background = brush;
        }
        else
        {
            Card.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        }
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
        AvatarImage.Visibility = image is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    private void OnAvatarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        using (NotchWindow.Instance?.HoldOpen()) ProfileService.PickAvatar(NotchWindow.Instance);
    }

    private void OnOpenNowPlaying(object sender, System.Windows.RoutedEventArgs e) =>
        NotchWindow.Instance?.ShowTool("nowplaying", backTo: "home");

    private void OnOpenCalendar(object sender, System.Windows.RoutedEventArgs e) =>
        NotchWindow.Instance?.ShowTool("calendar", backTo: "home");

    private void OnOpenTodos(object sender, System.Windows.RoutedEventArgs e) =>
        NotchWindow.Instance?.ShowTool("todos", backTo: "home");
}
