using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace OrixNotch.Services;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// Tells the user when a newer OrixNotch is out. The Microsoft Store build asks the Store (and installs
/// through Windows' own update dialog, downloading quietly first where Windows allows). The MSI install
/// downloads the new setup in the background and runs it (it closes, upgrades and restarts the app);
/// the portable exe opens the release page.
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
    private GitHubAsset? _msiAsset;
    private Task<string?>? _msiDownload; // path of the downloaded setup, or null if it failed
    private string? _msiFor;              // version that download is for

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
            if (found && !userAsked && StartMsiDownload() is { } download) await download; // announce once it can install
            else if (found) StartMsiDownload();
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
        _msiAsset = release.Assets?.FirstOrDefault(a => a.Name?.EndsWith("-setup.msi", StringComparison.OrdinalIgnoreCase) == true);
        LatestVersion = latest.ToString(3);
        return latest > current;
    }

    private async Task<bool> CheckStoreAsync()
    {
        _store ??= CreateStoreContext();
        _storeUpdates = await _store.GetAppAndOptionalStorePackageUpdatesAsync();
        if (_storeUpdates.Count == 0) return false;
        LatestVersion = _storeUpdates[0].Package.Id.Version.ToVersionString();
        try
        {
            // Fetch it now (no dialog; respects metered and Store settings) so installing later is quick.
            if (_store.CanSilentlyDownloadStorePackageUpdates)
                await _store.TrySilentDownloadStorePackageUpdatesAsync(_storeUpdates);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
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

    /// <summary>Store: Windows downloads and installs (the app restarts). MSI: runs the new setup. Portable: opens the release page.</summary>
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
        if (StartMsiDownload() is { } download)
        {
            Status = UpdateStatus.Downloading;
            var msi = await download;
            Status = UpdateStatus.Available;
            if (msi is not null)
            {
                try
                {
                    // The setup closes this app, upgrades in place and starts the new version (OrixNotch.wxs).
                    Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /passive") { UseShellExecute = false });
                    return;
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }
            }
        }
        Open(_releaseUrl ?? "https://github.com/faisal-shohag/orixNotch/releases/latest");
    }

    /// <summary>Starts (or reuses) the background download of the latest setup; MSI installs only.</summary>
    private Task<string?>? StartMsiDownload()
    {
        if (StartupService.IsPackaged || _msiAsset is null || !IsMsiInstall) return null;
        var failed = _msiDownload is { IsCompleted: true, Result: null };
        if (_msiDownload is null || _msiFor != LatestVersion || failed)
        {
            _msiFor = LatestVersion;
            _msiDownload = DownloadAsync(_msiAsset, LatestVersion);
        }
        return _msiDownload;
    }

    /// <summary>Running from the folder the MSI installed to (it records it in HKCU\Software\OrixNotch).</summary>
    private static bool IsMsiInstall { get; } = DetectMsiInstall();

    private static bool DetectMsiInstall()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\OrixNotch", "InstallDir", null) is not string dir) return false;
            static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\');
            return string.Equals(Norm(dir), Norm(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Own client: the shared one gives up after 12 s, too short for a ~70 MB setup.
    private static readonly HttpClient Download = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrixNotch/" + CurrentVersion);
        return client;
    }

    /// <summary>Downloads the setup to %TEMP%, checked against the SHA-256 GitHub lists for it. Null on any failure.</summary>
    private static async Task<string?> DownloadAsync(GitHubAsset asset, string version)
    {
        try
        {
            // Only GitHub's download host (or the test server standing in for its API).
            var testHost = Uri.TryCreate(Environment.GetEnvironmentVariable("ORIXNOTCH_UPDATE_BASE"), UriKind.Absolute, out var b) ? b.Host : null;
            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
                || !(uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" || uri.Host == testHost))
                return null;

            var dir = Path.Combine(Path.GetTempPath(), "OrixNotch-update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"OrixNotch-{version}-setup.msi");
            // Setups for older versions (already installed, or skipped) aren't needed any more.
            foreach (var old in Directory.EnumerateFiles(dir).Where(f => !f.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // still open (msiexec mid-install): next time
                }
            }
            var part = path + ".part";
            using (var response = await Download.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(part);
                await response.Content.CopyToAsync(file);
            }
            if (asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string hash;
                await using (var read = File.OpenRead(part))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(read));
                if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    return null;
                }
            }
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
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
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; } // "sha256:…" on newer releases
    }
}

internal static class PackageVersionExtensions
{
    public static string ToVersionString(this PackageVersion v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
