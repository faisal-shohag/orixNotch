using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Data.Sqlite;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <summary>A Windows notification to mirror on the closed notch.</summary>
public sealed record NotchNotification(string AppId, string AppName, ImageSource? Icon, string Title, string Body);

/// <summary>
/// Watches Windows notifications from other apps. With package identity (MSIX / Store build) it uses
/// the official UserNotificationListener (the user grants access once); otherwise it reads Windows'
/// local notification history database read-only, so MSI and dev builds work too.
/// </summary>
public sealed class NotificationWatcher
{
    public static NotificationWatcher Instance { get; } = new();

    public enum SourceKind { None, Listener, Database }

    /// <summary>Raised on the UI thread for each new notification that passes the filters.</summary>
    public event Action<NotchNotification>? Arrived;

    public SourceKind Source { get; private set; }
    /// <summary>Null when working; otherwise why notifications can't be read (shown in Settings).</summary>
    public string? Problem { get; private set; }
    /// <summary>Windows Settings page that fixes <see cref="Problem"/>.</summary>
    public string ProblemSettingsUri { get; private set; } = "ms-settings:privacy-notifications";

    /// <summary>Windows' master "Notifications" switch (Settings → System → Notifications).</summary>
    public static bool WindowsNotificationsOn()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
        return key?.GetValue("ToastEnabled") is not int enabled || enabled != 0;
    }

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, (string Name, ImageSource? Icon)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;

    // Listener state
    private readonly HashSet<uint> _seenIds = new();
    private bool _listenerPrimed;

    // Database state
    // ORIXNOTCH_WPN_DB points at a copy of the database (end-to-end tests insert toasts into it).
    private static readonly string DbPath =
        Environment.GetEnvironmentVariable("ORIXNOTCH_WPN_DB") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");
    private long _lastOrder = -1;

    private NotificationWatcher()
    {
        _poll.Tick += async (_, _) => await PollAsync();
        Source = StartupService.IsPackaged ? SourceKind.Listener : SourceKind.Database;
        Reconfigure();
    }

    /// <summary>Starts or stops watching after the setting changes.</summary>
    public void Reconfigure()
    {
        if (App.Settings.NotifyEnabled) _poll.Start();
        else _poll.Stop();
    }

    /// <summary>Asks Windows for notification access (listener mode only). Returns true when allowed.</summary>
    public async Task<bool> RequestAccessAsync()
    {
        if (Source != SourceKind.Listener) return true;
        try
        {
            var status = await UserNotificationListener.Current.RequestAccessAsync();
            Problem = status == UserNotificationListenerAccessStatus.Allowed ? null : "Notification access is turned off for OrixNotch in Windows Settings.";
            return Problem is null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Problem = "Windows refused notification access.";
            return false;
        }
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var found = Source == SourceKind.Listener ? await PollListenerAsync() : await Task.Run(PollDatabase);
            if (!WindowsNotificationsOn())
            {
                // Windows creates no notifications at all while its master switch is off.
                Problem = "Notifications are turned off in Windows, so there's nothing to show. Turn them on in Settings → System → Notifications.";
                ProblemSettingsUri = "ms-settings:notifications";
            }
            else ProblemSettingsUri = "ms-settings:privacy-notifications";
            foreach (var n in found) Publish(n);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------------------------------------------------------------- official listener

    private async Task<List<(string AppId, string? AppName, ImageSource? Icon, string Title, string Body)>> PollListenerAsync()
    {
        var result = new List<(string, string?, ImageSource?, string, string)>();
        var listener = UserNotificationListener.Current;
        if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
        {
            Problem = "Allow OrixNotch to read notifications in Windows Settings.";
            return result;
        }
        Problem = null;

        var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var n in list)
        {
            if (!_seenIds.Add(n.Id) || !_listenerPrimed) continue;
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            ImageSource? icon = null;
            try
            {
                var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                using var stream = (await logo.OpenReadAsync()).AsStream();
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                icon = image;
            }
            catch
            {
                // No logo: the notch shows a letter tile instead.
            }
            result.Add((n.AppInfo.AppUserModelId, n.AppInfo.DisplayInfo.DisplayName, icon,
                texts.FirstOrDefault() ?? "", string.Join(" ", texts.Skip(1))));
        }
        _listenerPrimed = true; // the first pass only records what was already there
        return result;
    }

    // ---------------------------------------------------------------- notification database fallback

    private List<(string AppId, string? AppName, ImageSource? Icon, string Title, string Body)> PollDatabase()
    {
        var result = new List<(string, string?, ImageSource?, string, string)>();
        if (!File.Exists(DbPath))
        {
            Problem = "Windows notification history isn't available on this PC.";
            return result;
        }

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        try
        {
            db.Open();
        }
        catch (SqliteException ex)
        {
            Problem = "Couldn't open Windows notification history.";
            App.Log(ex);
            return result;
        }
        Problem = null;

        if (_lastOrder < 0)
        {
            // First pass: start from now, don't replay history.
            using var max = db.CreateCommand();
            max.CommandText = "SELECT IFNULL(MAX([Order]), 0) FROM Notification";
            _lastOrder = Convert.ToInt64(max.ExecuteScalar());
            return result;
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT n.[Order], n.Payload, n.ArrivalTime, h.PrimaryId
            FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
            WHERE n.Type = 'toast' AND n.[Order] > $last
            ORDER BY n.[Order]
            """;
        cmd.Parameters.AddWithValue("$last", _lastOrder);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _lastOrder = Math.Max(_lastOrder, reader.GetInt64(0));
            if (reader.IsDBNull(1)) continue;
            // Skip anything that arrived more than 30 s ago (e.g. replayed after sleep).
            if (!reader.IsDBNull(2))
            {
                var arrived = DateTime.FromFileTimeUtc(reader.GetInt64(2));
                if (DateTime.UtcNow - arrived > TimeSpan.FromSeconds(30)) continue;
            }
            var (title, body) = ParsePayload((byte[])reader[1]);
            result.Add((reader.GetString(3), null, null, title, body));
        }
        return result;
    }

    /// <summary>Toast XML: the first &lt;text&gt; is the title, the rest the body.</summary>
    private static (string Title, string Body) ParsePayload(byte[] payload)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(System.Text.Encoding.UTF8.GetString(payload).TrimEnd('\0'));
            var texts = doc.SelectNodes("//binding/text")?.Cast<XmlNode>()
                .Select(n => n.InnerText.Trim()).Where(t => t.Length > 0).ToList() ?? [];
            return (texts.FirstOrDefault() ?? "", string.Join(" ", texts.Skip(1)));
        }
        catch
        {
            return ("", "");
        }
    }

    // ---------------------------------------------------------------- filtering + publishing (UI thread)

    private void Publish((string AppId, string? AppName, ImageSource? Icon, string Title, string Body) n)
    {
        if (!App.Settings.NotifyEnabled) return;
        if (string.IsNullOrWhiteSpace(n.Title) && string.IsNullOrWhiteSpace(n.Body)) return;
        if (n.AppId.Contains("OrixNotch", StringComparison.OrdinalIgnoreCase)) return; // our own toasts

        var (name, icon) = AppInfo(n.AppId, n.AppName, n.Icon);
        if (name.Equals("OrixNotch", StringComparison.OrdinalIgnoreCase)) return;
        RememberApp(n.AppId, name);
        if (App.Settings.NotifyMutedApps.Contains(n.AppId, StringComparer.OrdinalIgnoreCase)) return;

        var title = string.IsNullOrWhiteSpace(n.Title) ? name : n.Title;
        var body = App.Settings.NotifyShowText ? n.Body : "New notification";
        if (!App.Settings.NotifyShowText) title = name;
        Arrived?.Invoke(new NotchNotification(n.AppId, name, icon, title, body));
    }

    private (string Name, ImageSource? Icon) AppInfo(string aumid, string? name, ImageSource? icon)
    {
        if (!_apps.TryGetValue(aumid, out var info))
        {
            info = (name ?? ShellItem.AppName(aumid) ?? FallbackName(aumid), icon ?? ShellItem.AppIcon(aumid));
            _apps[aumid] = info;
        }
        return info;
    }

    /// <summary>"Microsoft.Windows.Explorer" → "Explorer"; "…\\powershell.exe" → "powershell".</summary>
    private static string FallbackName(string aumid)
    {
        var last = aumid.Split('\\', '!', '.').LastOrDefault(s => s.Length > 0 && !s.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? aumid;
        return last.Length > 0 ? char.ToUpperInvariant(last[0]) + last[1..] : aumid;
    }

    /// <summary>Keeps a short "apps seen" list for the mute switches in Settings.</summary>
    private static void RememberApp(string aumid, string name)
    {
        var seen = App.Settings.NotifySeenApps;
        var entry = $"{aumid}|{name}";
        var existing = seen.FindIndex(s => s.StartsWith(aumid + "|", StringComparison.OrdinalIgnoreCase));
        if (existing == 0 && seen[0] == entry) return;
        if (existing >= 0) seen.RemoveAt(existing);
        seen.Insert(0, entry);
        if (seen.Count > 12) seen.RemoveRange(12, seen.Count - 12);
        SettingsService.Save(notify: false);
    }
}
