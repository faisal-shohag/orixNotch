using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// Notification history: everything that popped out of the notch, newest first, grouped by day.
/// Click a card to read a long message in full; × deletes one, Clear all deletes everything.
/// </summary>
public partial class NotificationsView : UserControl, IToolView
{
    private const int CollapsedBodyLines = 3;
    private readonly HashSet<string> _expanded = new();


    public NotificationsView()
    {
        InitializeComponent();
        NotificationHistory.Instance.Changed += () =>
        {
            if (IsVisible) Render();
        };
    }

    public void OnShown()
    {
        ApplyHeight();
        // Relative times ("5m") go stale while hidden, so redraw on every open.
        Render();
    }

    public void OnHidden()
    {
    }

    private void OnClearAll(object sender, RoutedEventArgs e) => NotificationHistory.Instance.Clear();

    private void Render()
    {
        var items = NotificationHistory.Instance.Items;
        Count.Text = items.Count switch { 0 => "", 1 => "1 notification", var n => $"{n} notifications" };
        ClearButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptySub.Text = !App.Settings.NotifyEnabled ? "Turn on notifications in Settings → General to collect them."
            : !App.Settings.NotifyKeepHistory ? "History is off. Turn on Keep history in Settings to save them here."
            : "Notifications that pop out of the notch are kept here for 30 days.";

        List.Children.Clear();
        DateTime? day = null;
        foreach (var n in items)
        {
            if (n.At.Date != day)
            {
                day = n.At.Date;
                List.Children.Add(DayHeader(n.At.Date));
            }
            List.Children.Add(Card(n));
        }
    }

    private FrameworkElement DayHeader(DateTime date)
    {
        var text = date == DateTime.Today ? "Today"
            : date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : date > DateTime.Today.AddDays(-7) ? date.ToString("dddd")
            : date.ToString("d MMMM");
        return new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("SubText"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, List.Children.Count == 0 ? 0 : 8, 0, 5),
        };
    }

    private FrameworkElement Card(StoredNotification n)
    {
        var expanded = _expanded.Contains(n.Id);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = AppIcon(n);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 11, 0);
        grid.Children.Add(icon);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // App name · time on the left; × on the right (on hover).
        var top = new Grid();
        top.Children.Add(new TextBlock
        {
            Text = $"{n.AppName} · {Time(n.At)}",
            Style = (Style)FindResource("SubText"),
            FontWeight = FontWeights.Normal,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 26, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var delete = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, -3, -4, -3),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Delete",
            Content = new HeroIcon { Kind = Shell.AppIcon.XMark, Width = 10, Height = 10 },
            Opacity = 0,
        };
        delete.SetResourceReference(ForegroundProperty, "SubTextBrush");
        delete.Click += (_, e) =>
        {
            _expanded.Remove(n.Id);
            NotificationHistory.Instance.Remove(n.Id);
            e.Handled = true;
        };
        top.Children.Add(delete);
        text.Children.Add(top);

        text.Children.Add(new TextBlock
        {
            Text = n.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = expanded ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var hasBody = !string.IsNullOrWhiteSpace(n.Body);
        var body = new TextBlock
        {
            Text = n.Body.Trim(),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis,
            LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = expanded ? double.PositiveInfinity : 17 * CollapsedBodyLines,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = hasBody ? Visibility.Visible : Visibility.Collapsed,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        text.Children.Add(body);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
            Cursor = hasBody ? Cursors.Hand : null,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.MouseEnter += (_, _) =>
        {
            card.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
            delete.Opacity = 1;
        };
        card.MouseLeave += (_, _) =>
        {
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            delete.Opacity = 0;
        };
        // Click toggles the full text of a long message.
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!hasBody) return;
            if (!_expanded.Add(n.Id)) _expanded.Remove(n.Id);
            var open = _expanded.Contains(n.Id);
            body.MaxHeight = open ? double.PositiveInfinity : 17 * CollapsedBodyLines;
            ((TextBlock)text.Children[1]).TextWrapping = open ? TextWrapping.Wrap : TextWrapping.NoWrap;
        };
        return card;
    }

    /// <summary>The app's own icon, the OrixNotch alert's chart glyph, or the app's first letter on a tile.</summary>
    private static FrameworkElement AppIcon(StoredNotification n)
    {
        var frame = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8) };
        frame.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        if (n.Source == "orixnotch")
        {
            var glyph = new HeroIcon { Kind = Shell.AppIcon.ChartBar, Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
            frame.Child = glyph;
            return frame;
        }
        if (NotificationHistory.Instance.IconFor(n.AppId) is { } source)
        {
            var image = new Image { Source = source, Width = 24, Height = 24, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            frame.Child = image;
            return frame;
        }
        frame.Child = new TextBlock
        {
            Text = n.AppName.Length > 0 ? n.AppName[..1].ToUpperInvariant() : "?",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return frame;
    }

    // ---------------------------------------------------------------- resize (same handle as Ask AI / Quick Note)

    private const double DefaultHeight = 220;
    private const double MinListHeight = 200;
    private const double MaxListHeight = 520;
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;

    private static void ApplyHeight() =>
        NotchWindow.Instance?.SetToolSize(720, Math.Clamp(App.Settings.NotificationsHeight, MinListHeight, MaxListHeight));

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Settings.NotificationsHeight = DefaultHeight;
            ApplyHeight();
            SettingsService.Save(notify: false);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartY = PointToScreen(e.GetPosition(this)).Y;
        _dragStartHeight = ActualHeight; // what's shown, which may be capped to fit the screen
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var dy = (PointToScreen(e.GetPosition(this)).Y - _dragStartY) / dpi;
        var want = Math.Clamp(_dragStartHeight + dy, MinListHeight, MaxListHeight);
        App.Settings.NotificationsHeight = NotchWindow.Instance?.SetToolSize(720, want) ?? want;
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grip.ReleaseMouseCapture();
        SettingsService.Save(notify: false);
    }

    /// <summary>"now", "5m", "2h" today; the time for older ones (the day header carries the date).</summary>
    private static string Time(DateTime at)
    {
        var age = DateTime.Now - at;
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (at.Date == DateTime.Today && age < TimeSpan.FromHours(6)) return $"{(int)age.TotalHours}h ago";
        return at.ToString("t");
    }
}
