using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

public partial class CalendarView : UserControl, IToolView, IEscapeHandler
{
    private const string FileName = "events.json";
    private readonly List<CalendarEvent> _events = Storage.Load<List<CalendarEvent>>(FileName);
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;

    /// <summary>One timeline row; <see cref="Event"/> is the stored item (used by delete).</summary>
    public sealed record EventRow(CalendarEvent Event, string Time, string Title, bool IsPast, bool IsNext, string NextLabel, bool IsLast);

    private DateTime _renderedDay = DateTime.Today;

    public CalendarView()
    {
        InitializeComponent();
        BuildWeekdays();
        Render();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && TimePicker.Visibility == Visibility.Visible)
            {
                ClosePicker();
                e.Handled = true;
            }
        };
        ThemeService.Changed += () =>
        {
            BuildWeekdays();
            Render();
        };
    }

    public void OnShown()
    {
        // Day may have rolled over while the panel was closed; "up next" also moves with the clock.
        if (_renderedDay != DateTime.Today && _selected.Date == _renderedDay)
        {
            _selected = DateTime.Today;
            _month = new DateTime(_selected.Year, _selected.Month, 1);
        }
        Render();
        Rise(MonthCard, 0);
        Rise(DayPanel, 70);
    }

    public void OnHidden()
    {
    }

    private void BuildWeekdays()
    {
        WeekdayRow.Children.Clear();
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        var first = (int)culture.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)((first + i) % 7);
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = culture.AbbreviatedDayNames[(int)dow][..2].ToUpper(CultureInfo.CurrentCulture),
                Style = (Style)FindResource("SubText"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = dow is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.6 : 1,
            });
        }
    }

    private void Render()
    {
        _renderedDay = DateTime.Today;
        MonthText.Text = _month.ToString("MMMM", CultureInfo.CurrentCulture);
        YearText.Text = _month.ToString("yyyy", CultureInfo.CurrentCulture);
        Days.Children.Clear();

        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)_month.DayOfWeek - first + 7) % 7;
        var start = _month.AddDays(-offset);
        var accent = ThemeService.Get("AccentBrush");
        var onAccent = ThemeService.Get("OnAccentBrush");
        var sub = ThemeService.Get("SubTextBrush");
        var text = ThemeService.Get("TextBrush");
        var hover = ThemeService.Get("HoverBrush");
        var accentColor = ((SolidColorBrush)accent).Color;

        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var isSelected = day.Date == _selected.Date;
            var isToday = day.Date == DateTime.Today;
            var inMonth = day.Month == _month.Month;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var count = _events.Count(e => e.Date.Date == day.Date);

            var label = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.CurrentCulture),
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isSelected ? onAccent : isToday ? accent : weekend ? sub : text,
                FontWeight = isToday || isSelected ? FontWeights.Bold : FontWeights.Normal,
                Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
            };
            // The number is alone in its circle so it centres exactly; digits have no descenders,
            // so nudge down 1px to centre the glyphs rather than the line box.
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 1, 0, 0);

            var circle = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = isSelected ? accent : Brushes.Transparent,
                BorderBrush = isToday && !isSelected ? accent : null,
                BorderThickness = new Thickness(isToday && !isSelected ? 1.5 : 0),
                Child = label,
                Effect = isSelected
                    ? new System.Windows.Media.Effects.DropShadowEffect { Color = accentColor, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 }
                    : null,
            };

            // Event dots sit under the circle (always reserving their row, so every circle lines up).
            var dots = MakeDots(count, accent);
            dots.Margin = new Thickness(0, 1.5, 0, 0);

            var cell = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Opacity = inMonth ? 1 : 0.35,
            };
            cell.Children.Add(circle);
            cell.Children.Add(dots);
            if (!isSelected)
            {
                cell.MouseEnter += (_, _) => circle.Background = hover;
                cell.MouseLeave += (_, _) => circle.Background = Brushes.Transparent;
            }
            cell.MouseLeftButtonUp += (_, _) =>
            {
                var direction = day.Month == _month.Month ? 0 : day < _month ? -1 : 1;
                _selected = day;
                if (direction != 0) _month = new DateTime(day.Year, day.Month, 1);
                Render();
                if (direction != 0) SlideMonth(direction);
            };
            Days.Children.Add(cell);
        }

        RenderDay();
    }

    private void RenderDay()
    {
        var isToday = _selected.Date == DateTime.Today;
        DayNumber.Text = _selected.Day.ToString(CultureInfo.CurrentCulture);
        DayNumber.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "AccentBrush" : "TextBrush");
        DayTitle.Text = isToday ? "Today" : _selected.ToString("dddd", CultureInfo.CurrentCulture);
        TodayChip.Visibility = isToday ? Visibility.Collapsed : Visibility.Visible;

        var items = _events.Where(e => e.Date.Date == _selected.Date)
            .OrderBy(e => string.IsNullOrEmpty(e.Time) ? "" : e.Time)
            .ToList();
        var countText = items.Count switch { 0 => "No events", 1 => "1 event", var n => $"{n} events" };
        DaySub.Text = isToday
            ? $"{_selected.ToString("dddd, MMMM d", CultureInfo.CurrentCulture)} · {countText}"
            : $"{_selected.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture)} · {countText}";

        // "Up next" only makes sense for today: the first timed event that hasn't started yet.
        var now = DateTime.Now.TimeOfDay;
        TimeSpan? Start(CalendarEvent e) => TryParseTime(e.Time, out var t) ? t : null;
        var next = isToday ? items.FirstOrDefault(e => Start(e) >= now - TimeSpan.FromMinutes(1)) : null;

        EventList.ItemsSource = items.Select((e, i) =>
        {
            var at = Start(e);
            var past = isToday ? at < now && e != next : _selected.Date < DateTime.Today;
            var nextLabel = e == next && at is { } t ? "UP NEXT · " + NextLabel(t - now) : "";
            return new EventRow(e, e.TimeLabel, e.Title, past, e == next, nextLabel, i == items.Count - 1);
        }).ToList();
        NoEvents.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string NextLabel(TimeSpan until)
    {
        if (until.TotalMinutes < 1) return "NOW";
        if (until.TotalHours < 1) return $"IN {(int)until.TotalMinutes}M";
        return until.Minutes == 0 ? $"IN {(int)until.TotalHours}H" : $"IN {(int)until.TotalHours}H {until.Minutes}M";
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        Render();
        SlideMonth(-1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        Render();
        SlideMonth(1);
    }

    private void OnToday(object sender, RoutedEventArgs e)
    {
        var direction = Math.Sign((DateTime.Today.Year - _month.Year) * 12 + DateTime.Today.Month - _month.Month);
        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        Render();
        if (direction != 0) SlideMonth(direction);
    }

    /// <summary>New month glides in from the side it comes from.</summary>
    private void SlideMonth(int direction)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(240);
        DaysShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14 * direction, 0, duration) { EasingFunction = ease });
        DaysHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Fade in and rise a few pixels (same entrance as Home's cards).</summary>
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
                Field.ShowError(TimeInput); // red until the time is edited
                TimeInput.SelectAll();
                return;
            }
            time = $"{t.Hours:00}:{t.Minutes:00}";
        }

        _events.Add(new CalendarEvent { Date = _selected.Date, Time = time, Title = title });
        Storage.Save(FileName, _events);
        EventReminderService.Instance.Reload();
        TitleInput.Clear();
        TimeInput.Clear();
        Render();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventRow row) return;
        _events.Remove(row.Event);
        Storage.Save(FileName, _events);
        EventReminderService.Instance.Reload();
        Render();
    }

    // ---------------------------------------------------------------- time picker (clock dial)

    private const double DialSize = 180;
    private const double DialRadius = 68; // where the numbers sit
    private int _pickHour = 9;            // 0–23
    private int _pickMinute;
    private bool _pickingMinutes;
    private bool _dialDragging;

    /// <summary>The clock button toggles the dial open and closed.</summary>
    private void OnPickTime(object sender, RoutedEventArgs e)
    {
        if (TimePicker.Visibility == Visibility.Visible) ClosePicker();
        else OpenPicker();
    }

    /// <summary>Esc closes the time picker before it closes the notch.</summary>
    public bool OnEscape()
    {
        if (TimePicker.Visibility != Visibility.Visible) return false;
        ClosePicker();
        return true;
    }

    private void OnTimeInputClick(object sender, MouseButtonEventArgs e)
    {
        if (TimePicker.Visibility != Visibility.Visible) OpenPicker();
    }

    private void OpenPicker()
    {
        if (TryParseTime(TimeInput.Text.Trim(), out var t))
        {
            _pickHour = t.Hours;
            _pickMinute = t.Minutes;
        }
        else
        {
            // Default to the next half hour.
            var next = DateTime.Now.AddMinutes(30 - DateTime.Now.Minute % 30);
            _pickHour = next.Hour;
            _pickMinute = next.Minute;
        }
        _pickingMinutes = false;
        TimePicker.Visibility = Visibility.Visible;
        ClockButton.SetResourceReference(ForegroundProperty, "AccentBrush"); // shows the toggle is on
        ClockButton.ToolTip = "Close time picker";
        DrawDial();
    }

    private void ClosePicker()
    {
        TimePicker.Visibility = Visibility.Collapsed;
        ClockButton.SetResourceReference(ForegroundProperty, "SubTextBrush");
        ClockButton.ToolTip = "Pick a time";
        _dialDragging = false;
        Dial.ReleaseMouseCapture();
    }

    private void OnPickDone(object sender, RoutedEventArgs e)
    {
        TimeInput.Text = $"{_pickHour:00}:{_pickMinute:00}";
        ClosePicker();
        TitleInput.Focus();
    }

    private void OnPickAllDay(object sender, RoutedEventArgs e)
    {
        TimeInput.Clear();
        ClosePicker();
        TitleInput.Focus();
    }

    private void OnPickCancel(object sender, RoutedEventArgs e) => ClosePicker();

    private void OnPickHourMode(object sender, MouseButtonEventArgs e)
    {
        _pickingMinutes = false;
        DrawDial();
    }

    private void OnPickMinuteMode(object sender, MouseButtonEventArgs e)
    {
        _pickingMinutes = true;
        DrawDial();
    }

    private void OnPickAm(object sender, MouseButtonEventArgs e)
    {
        if (_pickHour >= 12) _pickHour -= 12;
        DrawDial();
    }

    private void OnPickPm(object sender, MouseButtonEventArgs e)
    {
        if (_pickHour < 12) _pickHour += 12;
        DrawDial();
    }

    private void OnDialDown(object sender, MouseButtonEventArgs e)
    {
        _dialDragging = true;
        Dial.CaptureMouse();
        PickAt(e.GetPosition(Dial));
        e.Handled = true;
    }

    private void OnDialMove(object sender, MouseEventArgs e)
    {
        if (_dialDragging) PickAt(e.GetPosition(Dial));
    }

    private void OnDialUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dialDragging) return;
        _dialDragging = false;
        Dial.ReleaseMouseCapture();
        // Picking an hour moves straight on to minutes, like a phone clock.
        if (!_pickingMinutes)
        {
            _pickingMinutes = true;
            DrawDial();
        }
    }

    /// <summary>Maps a point on the dial to the hour or minute under it (12 o'clock = 0, clockwise).</summary>
    private void PickAt(Point p)
    {
        var c = DialSize / 2;
        var angle = Math.Atan2(p.X - c, c - p.Y);
        if (angle < 0) angle += 2 * Math.PI;
        if (_pickingMinutes)
        {
            _pickMinute = (int)Math.Round(angle / (2 * Math.PI) * 12) % 12 * 5;
        }
        else
        {
            var hour12 = (int)Math.Round(angle / (2 * Math.PI) * 12) % 12; // 0 = 12 o'clock
            _pickHour = hour12 + (_pickHour >= 12 ? 12 : 0);
        }
        DrawDial();
    }

    private void DrawDial()
    {
        Dial.Children.Clear();
        var c = DialSize / 2;
        var accent = ThemeService.Get("AccentBrush");
        var onAccent = ThemeService.Get("OnAccentBrush");
        var text = ThemeService.Get("TextBrush");

        var face = new System.Windows.Shapes.Ellipse { Width = DialSize, Height = DialSize };
        face.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SurfaceBrush");
        Dial.Children.Add(face);

        // Selected position: 0–11 around the face.
        var slot = _pickingMinutes ? _pickMinute / 5.0 : _pickHour % 12;
        var a = slot / 12 * 2 * Math.PI;
        var at = new Point(c + DialRadius * Math.Sin(a), c - DialRadius * Math.Cos(a));

        Dial.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = c, Y1 = c, X2 = at.X, Y2 = at.Y, Stroke = accent, StrokeThickness = 1.5,
        });
        Dial.Children.Add(Dot(c, c, 6, accent));
        Dial.Children.Add(Dot(at.X, at.Y, 30, accent));

        for (var i = 0; i < 12; i++)
        {
            var angle = i / 12.0 * 2 * Math.PI;
            var label = new TextBlock
            {
                Text = _pickingMinutes ? (i * 5).ToString("00") : (i == 0 ? 12 : i).ToString(),
                FontSize = 12.5,
                Foreground = Math.Abs(i - slot) < 0.01 ? onAccent : text,
                FontWeight = Math.Abs(i - slot) < 0.01 ? FontWeights.SemiBold : FontWeights.Normal,
                Width = 30,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, c + DialRadius * Math.Sin(angle) - 15);
            Canvas.SetTop(label, c - DialRadius * Math.Cos(angle) - 8.5);
            Dial.Children.Add(label);
        }

        var hour12 = _pickHour % 12 == 0 ? 12 : _pickHour % 12;
        PickHour.Text = hour12.ToString("00");
        PickMinute.Text = _pickMinute.ToString("00");
        PickHour.SetResourceReference(TextBlock.ForegroundProperty, _pickingMinutes ? "SubTextBrush" : "AccentBrush");
        PickMinute.SetResourceReference(TextBlock.ForegroundProperty, _pickingMinutes ? "AccentBrush" : "SubTextBrush");
        PickAm.SetResourceReference(TextBlock.ForegroundProperty, _pickHour < 12 ? "AccentBrush" : "SubTextBrush");
        PickPm.SetResourceReference(TextBlock.ForegroundProperty, _pickHour >= 12 ? "AccentBrush" : "SubTextBrush");
        PickHint.Text = _pickingMinutes ? "Pick the minutes" : "Pick the hour";
    }

    private static System.Windows.Shapes.Ellipse Dot(double x, double y, double size, Brush fill)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = size, Height = size, Fill = fill, IsHitTestVisible = false };
        Canvas.SetLeft(dot, x - size / 2);
        Canvas.SetTop(dot, y - size / 2);
        return dot;
    }

    /// <summary>Accepts "9", "14", "9:30", "09:30".</summary>
    internal static bool TryParseTime(string raw, out TimeSpan time)
    {
        if (int.TryParse(raw, out var hour) && hour is >= 0 and < 24)
        {
            time = TimeSpan.FromHours(hour);
            return true;
        }
        return TimeSpan.TryParse(raw, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    /// <summary>Event markers under the day number: one dot per event, up to three.</summary>
    private static StackPanel MakeDots(int count, Brush fill)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 3.5 };
        for (var i = 0; i < Math.Min(count, 3); i++)
            dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Fill = fill, Margin = new Thickness(0.75, 0, 0.75, 0) });
        return dots;
    }
}
