using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// A button showing a date that opens a small month calendar below it (Subscriptions › Renews on). Past days can't
/// be picked when <see cref="FutureOnly"/> is set. The popup closes on a pick or a click elsewhere; while it's open
/// it holds the mouse, so the notch doesn't close under it.
/// </summary>
public sealed class DateButton : Button
{
    private readonly Popup _popup;
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 7 };
    private readonly TextBlock _month = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private DateTime _shown = DateTime.Today; // first day of the month on screen

    public DateTime? SelectedDate { get; private set; }

    public bool FutureOnly { get; set; } = true;

    private string _placeholder = "Pick a date";

    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value;
            UpdateLabel();
        }
    }

    public event Action<DateTime>? DatePicked;

    public DateButton()
    {
        SetResourceReference(StyleProperty, typeof(Button)); // the app's button look (a subclass doesn't get it by itself)
        HorizontalContentAlignment = HorizontalAlignment.Left;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new HeroIcon { Kind = AppIcon.CalendarDays, Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, "SubTextBrush");
        content.Children.Add(icon);
        content.Children.Add(_label);
        Content = content;

        var header = new Grid { Margin = new Thickness(2, 0, 2, 6) };
        header.Children.Add(_month);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        nav.Children.Add(NavButton(AppIcon.ChevronLeft, -1));
        nav.Children.Add(NavButton(AppIcon.ChevronRight, 1));
        header.Children.Add(nav);
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_days);
        var card = new Border { Child = stack, Padding = new Thickness(10), CornerRadius = Radii.L, BorderThickness = new Thickness(1), Width = 252 };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        _popup = new Popup
        {
            Child = card, PlacementTarget = this, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
        };
        Click += (_, _) =>
        {
            _shown = new DateTime((SelectedDate ?? DateTime.Today).Year, (SelectedDate ?? DateTime.Today).Month, 1);
            Render();
            _popup.IsOpen = true;
        };
        UpdateLabel();
    }

    public void Clear()
    {
        SelectedDate = null;
        UpdateLabel();
    }

    private Button NavButton(AppIcon icon, int months)
    {
        var button = new Button { Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, Content = new HeroIcon { Kind = icon, Width = 13, Height = 13 } };
        button.Click += (_, _) =>
        {
            _shown = _shown.AddMonths(months);
            Render();
        };
        return button;
    }

    private void UpdateLabel()
    {
        // Short enough for a narrow button: "Nov 25", with the year only when it isn't this one.
        _label.Text = SelectedDate is { } date
            ? date.ToString(date.Year == DateTime.Today.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.CurrentCulture)
            : Placeholder;
        _label.SetResourceReference(TextBlock.ForegroundProperty, SelectedDate is null ? "SubTextBrush" : "TextBrush");
    }

    /// <summary>The month on screen: weekday initials, then the days, starting on the culture's first day of the week.</summary>
    private void Render()
    {
        _month.Text = _shown.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _days.Children.Clear();
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        var first = (int)culture.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var initial = new TextBlock
            {
                Text = culture.ShortestDayNames[(first + i) % 7], FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            initial.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            _days.Children.Add(initial);
        }
        var lead = ((int)_shown.DayOfWeek - first + 7) % 7;
        var start = _shown.AddDays(-lead);
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            if (i >= 35 && day.Month != _shown.Month) break; // a sixth row only when the month needs it
            _days.Children.Add(DayCell(day));
        }
        _days.Rows = (int)Math.Ceiling(_days.Children.Count / 7.0);
    }

    private FrameworkElement DayCell(DateTime day)
    {
        var inMonth = day.Month == _shown.Month;
        var selected = SelectedDate?.Date == day;
        var today = day == DateTime.Today;
        var allowed = !FutureOnly || day >= DateTime.Today;
        var text = new TextBlock { Text = day.Day.ToString(CultureInfo.CurrentCulture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "OnAccentBrush" : inMonth ? "TextBrush" : "SubTextBrush");
        var cell = new Border
        {
            Width = 30, Height = 26, CornerRadius = new CornerRadius(13), Child = text, Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent, BorderThickness = new Thickness(today && !selected ? 1 : 0),
            Opacity = allowed ? (inMonth ? 1 : 0.45) : 0.25, Cursor = allowed ? System.Windows.Input.Cursors.Hand : null,
        };
        if (selected) cell.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        if (today) cell.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        if (!allowed) return cell;
        cell.MouseEnter += (_, _) => { if (!selected) cell.SetResourceReference(Border.BackgroundProperty, "HoverBrush"); };
        cell.MouseLeave += (_, _) => { if (!selected) cell.Background = Brushes.Transparent; };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            SelectedDate = day;
            UpdateLabel();
            _popup.IsOpen = false;
            DatePicked?.Invoke(day);
        };
        return cell;
    }
}
