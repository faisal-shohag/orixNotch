using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrixNotch.Tools;

namespace OrixNotch.Services;

/// <summary>
/// Subscribed calendars (Google "secret address in iCal format", Outlook "ICS link", iCloud public calendar):
/// fetched at start and every 30 minutes, expanded by <see cref="IcsParser"/> and kept in events-feed.json,
/// which the Calendar, Home, idle notch and reminders read alongside the user's own events.
/// </summary>
public sealed partial class CalendarFeedService : ObservableObject
{
    public const string FileName = "events-feed.json";
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    public static CalendarFeedService Instance { get; } = new();

    /// <summary>Raised after a refresh changed the saved events.</summary>
    public event Action? Updated;

    /// <summary>One line for Settings: when it last synced, or what went wrong.</summary>
    [ObservableProperty] private string _status = "";

    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private bool _refreshing;
    private bool _again; // a link was added or removed mid-refresh: go round once more so it counts

    private CalendarFeedService() => _timer.Tick += (_, _) => _ = RefreshAsync();

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _again = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _again = false;
                await RefreshOnceAsync();
            }
            while (_again);
        }
        catch (Exception ex) // fire-and-forget callers: never lose the error
        {
            App.Log(ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        var feeds = Feeds;
        if (feeds.Count == 0)
        {
            // Nothing subscribed: clear the last feed's events once, then stop writing and reloading every 30 minutes.
            Status = "";
            if (!File.Exists(Storage.PathOf(FileName))) return;
            File.Delete(Storage.PathOf(FileName));
            EventReminderService.Instance.Reload();
            Updated?.Invoke();
            return;
        }
        var previous = Storage.Load<List<CalendarEvent>>(FileName);
        var events = new List<CalendarEvent>();
        var failed = new List<string>();
        // Last month (for looking back) through a year ahead.
        var from = DateTime.Today.AddMonths(-1);
        var to = DateTime.Today.AddYears(1);
        foreach (var feed in feeds)
        {
            try
            {
                var text = await Http.Client.GetStringAsync(FetchUrl(feed));
                // An expired link often answers 200 with a sign-in page: that's a failure, not an empty calendar.
                if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Not a calendar (the link may have expired)");
                var parsed = await Task.Run(() => IcsParser.Parse(text, from, to));
                events.AddRange(parsed.Select(o => new CalendarEvent
                {
                    Date = DateTime.SpecifyKind(o.Start.Date, DateTimeKind.Unspecified), // same form as the user's own events
                    Time = o.AllDay ? "" : $"{o.Start.Hour:00}:{o.Start.Minute:00}",
                    Title = o.Title,
                    Feed = FeedId(feed),
                    Link = o.Link,
                }));
            }
            catch (Exception ex)
            {
                // Offline or a bad link: keep what this calendar had last time. Offline is logged once, not every 30 min.
                if (ex is not System.Net.Http.HttpRequestException || !Status.Contains("failed")) App.Log(ex);
                failed.Add(feed);
                events.AddRange(previous.Where(e => e.Feed == FeedId(feed)));
            }
        }
        Storage.Save(FileName, events);
        // Status first: Settings re-renders on Updated and should show the new line.
        Status = feeds.Count == 0 ? ""
            : failed.Count > 0 ? $"{failed.Count} of {feeds.Count} failed · using last copy"
            : $"Synced {DateTime.Now:t} · {events.Count(e => e.Date >= DateTime.Today)} upcoming";
        EventReminderService.Instance.Reload();
        Updated?.Invoke();
    }

    // ---------------------------------------------------------------- the links (secret: anyone with one can read the calendar)

    private const string LinksFile = "calendar-feeds.bin";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OrixNotch.CalendarFeeds");

    /// <summary>Subscribed links, kept DPAPI-protected like the API keys.</summary>
    public static List<string> Feeds
    {
        get
        {
            var text = Storage.LoadSecret(LinksFile, Entropy) ?? "";
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    public void Add(string url)
    {
        var feeds = Feeds;
        if (feeds.Contains(url)) return;
        feeds.Add(url);
        SaveFeeds(feeds);
        _ = RefreshAsync();
    }

    public void Remove(string url)
    {
        var feeds = Feeds;
        feeds.Remove(url);
        SaveFeeds(feeds);
        _ = RefreshAsync();
    }

    private static void SaveFeeds(List<string> feeds)
    {
        Storage.SaveSecret(LinksFile, string.Join('\n', feeds), Entropy); // no feeds: the file is deleted
    }

    /// <summary>Stable id for a link that doesn't reveal it (stored on each event).</summary>
    private static string FeedId(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12];

    /// <summary>A short, safe label for a link: its host (the secret part stays hidden).</summary>
    public static string Label(string url) => Uri.TryCreate(FetchUrl(url), UriKind.Absolute, out var uri) ? uri.Host : "Calendar";

    /// <summary>Whether a pasted link looks like a calendar address we can fetch.</summary>
    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(FetchUrl(url), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    // webcal:// is just "this is a calendar" on top of http(s).
    private static string FetchUrl(string url) =>
        url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + url["webcal://".Length..] : url;
}
