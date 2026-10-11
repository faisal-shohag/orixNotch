using System.Windows.Media;
using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <param name="Source">"app" for Windows notifications, "orixnotch" for OrixNotch's own alerts (AI usage).</param>
public sealed record StoredNotification(string Id, string AppId, string AppName, string Title, string Body, DateTime At, string Source = "app");

/// <summary>
/// Notifications that popped out of the notch, kept on this PC (<c>notifications.json</c>) so they can be
/// read later in the Notifications tool. Newest first, at most <see cref="MaxItems"/>, 30 days.
/// Only what already passed the mute and "show message text" settings is saved.
/// </summary>
public sealed class NotificationHistory
{
    public static NotificationHistory Instance { get; } = new();

    private const string FileName = "notifications.json";
    private const int MaxItems = 300;
    // A const, not a static field: Instance is built during static init, before later static fields are set.
    private const int MaxAgeDays = 30;

    private readonly List<StoredNotification> _items;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<StoredNotification> Items => _items;

    public event Action? Changed;

    private NotificationHistory()
    {
        _items = Storage.Load<List<StoredNotification>>(FileName);
        var cutoff = DateTime.Now.AddDays(-MaxAgeDays);
        if (_items.RemoveAll(n => n.At < cutoff) > 0) Save();
    }

    /// <summary>Starts recording what the notch shows.</summary>
    public void Start()
    {
        NotificationWatcher.Instance.Arrived += n =>
        {
            if (n.Icon is not null) _icons[n.AppId] = n.Icon;
            Add(new StoredNotification(Guid.NewGuid().ToString("N"), n.AppId, n.AppName, n.Title, n.Body, DateTime.Now));
        };
        AiLimitWatcher.Instance.Alert += a =>
        {
            var window = a.Window == "plan" ? "plan" : a.Window;
            var resets = a.Resets is { } r ? $" Resets {r:ddd h:mm tt}." : "";
            Add(new StoredNotification(Guid.NewGuid().ToString("N"), "orixnotch:" + a.ToolId, "AI Usage",
                $"{a.ToolName} · {window} {a.Percent:0}% used", $"Reached {a.Threshold}% of its {window} limit.{resets}", DateTime.Now, "orixnotch"));
        };
    }

    private void Add(StoredNotification n)
    {
        if (!App.Settings.NotifyKeepHistory) return;
        _items.Insert(0, n);
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(n => n.Id == id) == 0) return;
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Save();
        Changed?.Invoke();
    }

    /// <summary>The sending app's icon (cached), or null when Windows has none.</summary>
    public ImageSource? IconFor(string appId)
    {
        if (appId.StartsWith("orixnotch:", StringComparison.Ordinal)) return null;
        if (_icons.TryGetValue(appId, out var icon)) return icon;
        try
        {
            icon = ShellItem.AppIcon(appId);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            icon = null;
        }
        _icons[appId] = icon;
        return icon;
    }

    private void Save() => Storage.Save(FileName, _items);
}
