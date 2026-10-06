using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

public sealed class CalendarEvent
{
    public DateTime Date { get; set; }
    public string Time { get; set; } = "";
    public string Title { get; set; } = "";

    [JsonIgnore] public string TimeLabel => string.IsNullOrEmpty(Time) ? "All day" : Time;
}

public partial class CalendarView : UserControl, IToolView
{
    private const string FileName = "events.json";
    private readonly List<CalendarEvent> _events = Storage.Load<List<CalendarEvent>>(FileName);
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;

    public CalendarView()
    {
        InitializeComponent();
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        var first = (int)culture.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = culture.ShortestDayNames[(first + i) % 7],
                Style = (Style)FindResource("SubText"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        Render();
        ThemeService.Changed += Render;
    }

    public void OnShown()
    {
        // Day may have rolled over while the panel was closed.
        if (_selected.Date != DateTime.Today && _month.Month == _selected.Month) Render();
    }

    public void OnHidden()
    {
    }

    private void Render()
    {
        MonthText.Text = _month.ToString("MMMM yyyy");
        Days.Children.Clear();

        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)_month.DayOfWeek - first + 7) % 7;
        var start = _month.AddDays(-offset);
        var accent = ThemeService.Get("AccentBrush");
        var onAccent = ThemeService.Get("OnAccentBrush");
        var sub = ThemeService.Get("SubTextBrush");
        var text = ThemeService.Get("TextBrush");

        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var isSelected = day.Date == _selected.Date;
            var isToday = day.Date == DateTime.Today;
            var inMonth = day.Month == _month.Month;
            var hasEvents = _events.Any(e => e.Date.Date == day.Date);

            var label = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isSelected ? onAccent : inMonth ? text : sub,
                FontWeight = isToday || isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(label);
            content.Children.Add(MakeDot(hasEvents, isSelected ? onAccent : accent));

            var cell = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = isSelected ? accent : Brushes.Transparent,
                BorderBrush = isToday && !isSelected ? accent : null,
                BorderThickness = new Thickness(isToday && !isSelected ? 1.5 : 0),
                Child = content,
                Cursor = Cursors.Hand,
                Opacity = inMonth ? 1 : 0.55,
            };
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _selected = day;
                if (day.Month != _month.Month) _month = new DateTime(day.Year, day.Month, 1);
                Render();
            };
            Days.Children.Add(cell);
        }

        DayTitle.Text = _selected.Date == DateTime.Today ? "Today" : _selected.ToString("dddd");
        DaySub.Text = _selected.ToString("D");
        var items = _events.Where(e => e.Date.Date == _selected.Date)
            .OrderBy(e => string.IsNullOrEmpty(e.Time) ? "" : e.Time)
            .ToList();
        EventList.ItemsSource = items;
        NoEvents.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        Render();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        Render();
    }

    private void OnToday(object sender, RoutedEventArgs e)
    {
        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        Render();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add();
    }

    private void OnAdd(object sender, RoutedEventArgs e) => Add();

    private void Add()
    {
        var title = TitleInput.Text.Trim();
        if (title.Length == 0) return;

        var time = "";
        var rawTime = TimeInput.Text.Trim();
        if (rawTime.Length > 0)
        {
            if (!TryParseTime(rawTime, out var t))
            {
                TimeInput.Focus();
                return;
            }
            time = $"{t.Hours:00}:{t.Minutes:00}";
        }

        _events.Add(new CalendarEvent { Date = _selected.Date, Time = time, Title = title });
        Storage.Save(FileName, _events);
        TitleInput.Clear();
        TimeInput.Clear();
        Render();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarEvent ev) return;
        _events.Remove(ev);
        Storage.Save(FileName, _events);
        Render();
    }

    /// <summary>Accepts "9", "14", "9:30", "09:30".</summary>
    private static bool TryParseTime(string raw, out TimeSpan time)
    {
        if (int.TryParse(raw, out var hour) && hour is >= 0 and < 24)
        {
            time = TimeSpan.FromHours(hour);
            return true;
        }
        return TimeSpan.TryParse(raw, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    /// <summary>Small event marker under the day number.</summary>
    private static System.Windows.Shapes.Ellipse MakeDot(bool visible, Brush fill) => new()
    {
        Width = 4,
        Height = 4,
        Fill = fill,
        Margin = new Thickness(0, 1, 0, 0),
        Visibility = visible ? Visibility.Visible : Visibility.Hidden,
    };
}
