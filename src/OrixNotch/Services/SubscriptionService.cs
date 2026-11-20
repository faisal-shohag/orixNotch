using System.Globalization;
using System.Windows.Threading;
using OrixNotch.Tools;

namespace OrixNotch.Services;

public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "";
    /// <summary>"Weekly", "Monthly" or "Yearly".</summary>
    public string Cycle { get; set; } = "Monthly";
    public DateTime Next { get; set; }
    /// <summary>The renewal date already announced, so the alert shows once per renewal.</summary>
    public DateTime? Alerted { get; set; }

    /// <summary>The day of the month it renews on, set when added: the 31st stays the 31st after a short month
    /// (AddMonths alone drifts Jan 31 → Feb 28 → Mar 28). Older items without it keep their current day.</summary>
    public int? Day { get; set; }

    public DateTime After(DateTime date)
    {
        if (Cycle == "Weekly") return date.AddDays(7);
        var next = Cycle == "Yearly" ? date.AddYears(1) : date.AddMonths(1);
        var day = Math.Min(Day ?? date.Day, DateTime.DaysInMonth(next.Year, next.Month));
        return new DateTime(next.Year, next.Month, day, 0, 0, 0, date.Kind) + date.TimeOfDay;
    }

    /// <summary>The price spread over a month, for the monthly total.</summary>
    public decimal PerMonth => Cycle switch
    {
        "Weekly" => Price * 52 / 12,
        "Yearly" => Price / 12,
        _ => Price,
    };

    public string PriceLabel => $"{Currency} {Price.ToString("#,0.##", CultureInfo.CurrentCulture)}".Trim();
}

/// <summary>
/// Subscriptions the user pays for (subscriptions.json): renewal dates roll forward on their own, show on the
/// calendar, and an alert comes 2 days before each renewal.
/// </summary>
public sealed class SubscriptionService
{
    public static SubscriptionService Instance { get; } = new();

    public static readonly string[] Cycles = ["Monthly", "Yearly", "Weekly"];
    private const string FileName = "subscriptions.json";
    private static readonly TimeSpan AlertAhead = TimeSpan.FromDays(2);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };

    public List<Subscription> Items { get; }

    public event Action? Changed;

    /// <summary>A renewal is coming up: the subscription and how many days away (0 = today).</summary>
    public event Action<Subscription, int>? Renewing;

    private SubscriptionService()
    {
        Items = Storage.Load<List<Subscription>>(FileName);
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        _timer.Start();
        Check();
    }

    public void Add(Subscription sub)
    {
        Items.Add(sub);
        Save();
        Check();
    }

    public void Remove(string id)
    {
        if (Items.RemoveAll(s => s.Id == id) == 0) return;
        Save();
    }

    /// <summary>Monthly total per currency, largest first.</summary>
    public List<(string Currency, decimal Total)> MonthlyTotals() =>
        Items.GroupBy(s => s.Currency).Select(g => (g.Key, g.Sum(s => s.PerMonth))).OrderByDescending(x => x.Item2).ToList();

    /// <summary>Upcoming renewals as read-only all-day calendar events (the next one per subscription, and the one after).</summary>
    public IEnumerable<CalendarEvent> Events() => Items.SelectMany(s => new[] { s.Next, s.After(s.Next) }.Select(d => new CalendarEvent
    {
        Date = d.Date,
        Time = "",
        Title = $"{s.Name} renews · {s.PriceLabel}",
        Feed = CalendarEvent.SubscriptionFeed,
    }));

    /// <summary>Rolls past renewals forward and announces those within two days.</summary>
    private void Check()
    {
        var today = DateTime.Today;
        var changed = false;
        foreach (var s in Items)
        {
            while (s.Next.Date < today)
            {
                s.Next = s.After(s.Next);
                changed = true;
            }
            if (s.Next.Date - today <= AlertAhead && s.Alerted != s.Next.Date)
            {
                s.Alerted = s.Next.Date;
                changed = true;
                Renewing?.Invoke(s, (s.Next.Date - today).Days);
            }
        }
        if (changed) Save();
    }

    private void Save()
    {
        Storage.Save(FileName, Items);
        Changed?.Invoke();
    }
}
