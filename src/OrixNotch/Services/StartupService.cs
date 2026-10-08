using Microsoft.Win32;
using Windows.ApplicationModel;

namespace OrixNotch.Services;

/// <summary>
/// "Open at login". Two mechanisms, because Windows treats the two install types differently:
/// <list type="bullet">
/// <item>MSIX / Microsoft Store build (has package identity): the manifest's StartupTask
/// (<c>OrixNotchAutostart</c>). Windows enables it after the first launch; the user can turn it off in
/// Task Manager, and then only they can turn it back on.</item>
/// <item>MSI / portable exe: the per-user Run key, pointing at the exe that is running now (so it follows
/// the app when an update or a move changes its path).</item>
/// </list>
/// On by default for new installs; <see cref="SyncAsync"/> runs at every launch to apply the setting.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OrixNotch";
    private const string TaskId = "OrixNotchAutostart"; // Package.appxmanifest → desktop:StartupTask

    /// <summary>True when running from an MSIX package (Store or sideloaded).</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>The user switched startup off in Task Manager; the app may not override that.</summary>
    public static bool DisabledByUser { get; private set; }

    private static bool DetectPackage()
    {
        try
        {
            return Package.Current is not null;
        }
        catch
        {
            return false; // no package identity
        }
    }

    /// <summary>
    /// At launch: turn startup on for new installs (once), then make Windows match the setting.
    /// The packaged state can change behind our back (Task Manager), so it is read back into the setting.
    /// </summary>
    public static async Task SyncAsync()
    {
        var settings = App.Settings;
        if (!settings.StartupDefaultApplied)
        {
            settings.LaunchAtStartup = true;
            settings.StartupDefaultApplied = true;
            SettingsService.Save(notify: false);
        }

        if (IsPackaged)
        {
            var state = await GetTaskStateAsync();
            DisabledByUser = state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
            var enabled = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            if (settings.LaunchAtStartup && !enabled && !DisabledByUser) enabled = await ApplyAsync(true);
            else if (!settings.LaunchAtStartup && enabled) await ApplyAsync(false);
            else if (DisabledByUser && settings.LaunchAtStartup)
            {
                settings.LaunchAtStartup = false; // reflect the user's choice in Task Manager
                SettingsService.Save(notify: false);
            }
            return;
        }

        await ApplyAsync(settings.LaunchAtStartup);
    }

    /// <summary>Turns open-at-login on or off. Returns whether it is on afterwards.</summary>
    public static async Task<bool> ApplyAsync(bool enabled)
    {
        try
        {
            if (IsPackaged)
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    task.Disable();
                    return false;
                }
                var state = await task.RequestEnableAsync();
                DisabledByUser = state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return false;
            if (enabled)
            {
                var command = $"\"{Environment.ProcessPath}\"";
                if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return enabled;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    private static async Task<StartupTaskState?> GetTaskStateAsync()
    {
        try
        {
            return (await StartupTask.GetAsync(TaskId)).State;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }
}
