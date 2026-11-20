using System.Windows;
using System.Windows.Media;

namespace OrixNotch.Shell;

/// <summary>
/// Idle notch "Day timeline": today on one line. The elapsed part is filled with the accent, today's events are
/// ticks and a dot marks now. Spans 08:00–22:00, stretched to fit earlier or later events.
/// </summary>
public sealed class DayTimeline : FrameworkElement
{
    private readonly IReadOnlyList<TimeSpan> _events;

    public DayTimeline(IReadOnlyList<TimeSpan> events)
    {
        _events = events;
        Width = 112;
        Height = 12;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var now = DateTime.Now.TimeOfDay;
        var start = TimeSpan.FromHours(8);
        var end = TimeSpan.FromHours(22);
        foreach (var e in _events)
        {
            if (e < start) start = TimeSpan.FromHours(Math.Floor(e.TotalHours));
            if (e > end) end = TimeSpan.FromHours(Math.Min(24, Math.Ceiling(e.TotalHours)));
        }
        double X(TimeSpan t) => Math.Clamp((t - start) / (end - start), 0, 1) * ActualWidth;

        var mid = ActualHeight / 2;
        var track = Brush("TrackBrush");
        // The timeline's own orange on the default theme (like the other idle items); the accent on other schemes.
        var accent = OrixNotch.Services.ThemeService.IsDefaultScheme ? Orange : Brush("AccentBrush");
        dc.DrawRoundedRectangle(track, null, new Rect(0, mid - 1.5, ActualWidth, 3), 1.5, 1.5);
        var x = X(now);
        if (x > 0) dc.DrawRoundedRectangle(accent, null, new Rect(0, mid - 1.5, x, 3), 1.5, 1.5);

        var past = Brush("SubTextBrush");
        var ahead = Brush("TextBrush");
        foreach (var e in _events)
            dc.DrawRoundedRectangle(e < now ? past : ahead, null, new Rect(X(e) - 1, mid - 4.5, 2, 9), 1, 1);

        dc.DrawEllipse(accent, new Pen(Brush("PillBrush"), 1.5), new Point(x, mid), 4, 4);
    }

    private static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A)); // the idle items' orange (NotchWindow's palette)

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush Brush(string key) => Application.Current.Resources[key] as Brush ?? Brushes.Gray;
}
