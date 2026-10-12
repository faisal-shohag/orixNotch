using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace OrixNotch.Services;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Failed,
}

/// <summary>
/// Tells the user when a newer OrixNotch is out. The Microsoft Store build asks the Store (and installs
/// through Windows' own update dialog); GitHub builds (exe/msi) read the latest release and open its page.
/// Checks a couple of minutes after launch and then daily, while <see cref="AppSettings.CheckForUpdates"/> is on.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    private const string ReleasesApi = "https://api.github.com/repos/faisal-shohag/orixNotch";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    public static UpdateService Instance { get; } = new();

    private readonly DispatcherTimer _timer = new();
    private StoreContext? _store;
    private IReadOnlyList<StorePackageUpdate>? _storeUpdates;
    private string? _releaseUrl;

    [ObservableProperty] private UpdateStatus _status;
    /// <summary>The newer version, e.g. "0.7.0" (the Store doesn't say, so its builds show "new version").</summary>
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private DateTime? _checkedAt;

    /// <summary>An automatic check found a version the user hasn't been told about yet.</summary>
    public event Action<string>? Available;

    public static string CurrentVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private UpdateService()
    {
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            await CheckAsync(userAsked: false);
        };
    }

    /// <summary>Starts (or stops) the background checks to match the setting.</summary>
    public void Start()
    {
        _timer.Stop();
        if (!App.Settings.CheckForUpdates) return;
        // ORIXNOTCH_UPDATE_DELAY (seconds) lets tests see the first check without waiting.
        _timer.Interval = int.TryParse(Environment.GetEnvironmentVariable("ORIXNOTCH_UPDATE_DELAY"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : FirstCheck;
        _timer.Start();
    }

    public async Task CheckAsync(bool userAsked)
    {
        if (Status == UpdateStatus.Checking) return;
        Status = UpdateStatus.Checking;
        try
        {
            var found = StartupService.IsPackaged ? await CheckStoreAsync() : await CheckGitHubAsync();
            CheckedAt = DateTime.Now;
            Status = found ? UpdateStatus.Available : UpdateStatus.UpToDate;
            if (found && !userAsked) NotifyOnce();
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, or a sideloaded package the Store doesn't know.
            App.Log(ex);
            Status = UpdateStatus.Failed;
        }
    }

    /// <summary>Each version is announced once; after that it waits on the About page.</summary>
    private void NotifyOnce()
    {
        var key = StartupService.IsPackaged ? "store:" + string.Join(",", _storeUpdates!.Select(u => u.Package.Id.Version.ToVersionString())) : LatestVersion;
        if (App.Settings.UpdateNotified == key) return;
        App.Settings.UpdateNotified = key;
        SettingsService.Save(notify: false);
        Available?.Invoke(LatestVersion);
    }

    private async Task<bool> CheckGitHubAsync()
    {
        var api = Environment.GetEnvironmentVariable("ORIXNOTCH_UPDATE_BASE") is { Length: > 0 } b ? b.TrimEnd('/') : ReleasesApi;
        var release = await Http.Client.GetFromJsonAsync<GitHubRelease>(api + "/releases/latest");
        if (release?.TagName is not { Length: > 0 } tag) return false;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(CurrentVersion, out var current)) return false;
        _releaseUrl = release.HtmlUrl;
        LatestVersion = latest.ToString(3);
        return latest > current;
    }

    private async Task<bool> CheckStoreAsync()
    {
        _store ??= CreateStoreContext();
        _storeUpdates = await _store.GetAppAndOptionalStorePackageUpdatesAsync();
        if (_storeUpdates.Count == 0) return false;
        LatestVersion = _storeUpdates[0].Package.Id.Version.ToVersionString();
        return true;
    }

    /// <summary>A desktop app's Store dialogs need a window to sit on.</summary>
    private static StoreContext CreateStoreContext()
    {
        var context = StoreContext.GetDefault();
        if (Application.Current?.MainWindow is { } window && new WindowInteropHelper(window).Handle is var hwnd && hwnd != IntPtr.Zero)
            WinRT.Interop.InitializeWithWindow.Initialize(context, hwnd);
        return context;
    }

    /// <summary>Store: Windows downloads and installs (the app restarts). GitHub: opens the release page.</summary>
    public async Task InstallAsync()
    {
        if (StartupService.IsPackaged)
        {
            try
            {
                if (_store is not null && _storeUpdates is { Count: > 0 } updates)
                {
                    var result = await _store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                    if (result.OverallState is StorePackageUpdateState.Completed or StorePackageUpdateState.Canceled) return;
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
            Open($"ms-windows-store://pdp/?PFN={Package.Current.Id.FamilyName}");
            return;
        }
        Open(_releaseUrl ?? "https://github.com/faisal-shohag/orixNotch/releases/latest");
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}

internal static class PackageVersionExtensions
{
    public static string ToVersionString(this PackageVersion v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
