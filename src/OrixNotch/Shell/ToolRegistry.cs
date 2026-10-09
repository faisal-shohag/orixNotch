using System.Windows;
using OrixNotch.Tools;

namespace OrixNotch.Shell;

/// <summary>Optional hooks for tools that poll or animate only while visible.</summary>
public interface IToolView
{
    void OnShown();
    void OnHidden();
}

/// <summary>Optional for tool views: return true from <see cref="OnEscape"/> to consume Esc
/// (close an inline editor, a picker…) before the notch goes back or collapses.</summary>
public interface IEscapeHandler
{
    bool OnEscape();
}

/// <param name="Width">Content width; the notch springs to fit it.</param>
/// <param name="Height">Content height between the header and the tab bar.</param>
public sealed record ToolDef(string Id, string Name, AppIcon Icon, double Width, double Height, Func<FrameworkElement> Create);

public static class ToolRegistry
{
    // OmniNotch keeps one panel size (≈726×323) for every tool: 690 wide content after
    // 18 px padding, ≈203 tall between header and tab bar. A few tools need a little more.
    private const double W = 720;
    private const double H = 220;

    public static IReadOnlyList<ToolDef> All { get; } =
    [
        new("home", "OrixNotch", AppIcon.Grid, W, H, () => new HomeView()),
        new("ask", "Ask AI", AppIcon.Sparkles, W, H, () => new AskAiView()),
        new("aiusage", "AI Usage", AppIcon.ChartBar, W, H, () => new AiUsageView()),
        new("shelf", "Shelf", AppIcon.ArchiveBox, W, H, () => new ShelfView()),
        new("clipboard", "Clipboard", AppIcon.ClipboardList, W, H, () => new ClipboardView()),
        new("teleprompter", "Teleprompter", AppIcon.Teleprompter, W, H, () => new TeleprompterView()),
        new("shortcuts", "Shortcuts", AppIcon.Stack, W, H, () => new ShortcutsView()),
        new("nowplaying", "Now Playing", AppIcon.MusicNote, W, H, () => new NowPlayingView()),
        new("note", "Quick Note", AppIcon.BookOpen, W, H, () => new QuickNoteView()),
        new("todos", "To-Dos", AppIcon.ListBullet, W, H, () => new TodosView()),
        new("timer", "Timers", AppIcon.Clock, W, H, () => new TimerView()),
        new("calendar", "Calendar", AppIcon.CalendarDays, W, 245, () => new CalendarView()),
        new("weather", "Weather", AppIcon.Cloud, W, 245, () => new WeatherView()),
        new("system", "System", AppIcon.Signal, W, H, () => new SystemMonitorView()),
        new("stocks", "Stocks", AppIcon.ChartLine, W, H, () => new StocksView()),
        new("converter", "Converter", AppIcon.ArrowsLeftRight, W, H, () => new ConverterView()),
        new("emoji", "Emoji", AppIcon.FaceSmile, W, 245, () => new EmojiView()),
    ];

    /// <summary>Tools in the user's order, hidden ones removed.</summary>
    public static List<ToolDef> Ordered(bool includeHidden = false)
    {
        var settings = App.Settings;
        var order = settings.ToolOrder;
        var list = All.OrderBy(t =>
        {
            var i = order.IndexOf(t.Id);
            return i < 0 ? int.MaxValue : i;
        }).ToList();
        return includeHidden ? list : list.Where(t => !settings.HiddenTools.Contains(t.Id)).ToList();
    }
}
