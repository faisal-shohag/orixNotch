using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace OrixNotch.Services;

/// <summary>Windows switches for the Controls tool: Wi-Fi and Bluetooth radios, and dark mode.</summary>
public static class SystemToggles
{
    /// <summary>The first radio of a kind, or null when the PC has none (or Windows refuses access).</summary>
    public static async Task<Radio?> RadioAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <returns>Whether Windows switched it.</returns>
    public static async Task<bool> SetRadioAsync(Radio radio, bool on)
    {
        try
        {
            return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Windows' app colour mode is dark.</summary>
    public static bool DarkMode
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
            {
                key.SetValue("AppsUseLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
            }
            // Tell open windows (Explorer, the taskbar, apps) the colour set changed.
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 0x0002 /* SMTO_ABORTIFHUNG */, 200, out _);
        }
    }

    private const string MicConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string CameraConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    /// <summary>
    /// Some app is using the microphone right now (a call, in any app or browser): Windows' privacy records keep a
    /// start time per app and a stop time of 0 while it's in use.
    /// </summary>
    public static bool MicInUse() => Mic.InUse();

    /// <summary>Some app is using the camera right now (the same privacy records as the microphone).</summary>
    public static bool CameraInUse() => Camera.InUse();

    /// <summary>On a call: the microphone or the camera is in use.</summary>
    public static bool InCall() => MicInUse() || CameraInUse();

    private static readonly ConsentWatch Mic = new(MicConsentKey), Camera = new(CameraConsentKey);

    /// <summary>
    /// Walking the consent tree opens dozens of keys; it's polled every 2 s. Windows signals an event when anything
    /// under it changes, so the tree is only walked again after a change. Call from one long-lived thread (the UI):
    /// the notification ends when the thread that armed it exits.
    /// </summary>
    private sealed class ConsentWatch(string path)
    {
        private readonly AutoResetEvent _changed = new(true); // set: walk on the first call
        private RegistryKey? _root;
        private bool _inUse;

        public bool InUse()
        {
            if (!_changed.WaitOne(0)) return _inUse;
            try
            {
                _root ??= Registry.CurrentUser.OpenSubKey(path);
                if (_root is null)
                {
                    _changed.Set(); // not there yet: look again next time
                    return _inUse = false;
                }
                // Armed before the walk, so a change during it isn't missed. If arming fails, poll as before.
                if (RegNotifyChangeKeyValue(_root.Handle, true, RegNotifyName | RegNotifyLastSet, _changed.SafeWaitHandle, true) != 0)
                    _changed.Set();
                return _inUse = Walk(_root, 0);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                _changed.Set();
                return _inUse = false;
            }
        }

        private static bool Walk(RegistryKey key, int depth)
        {
            if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start > 0) return true;
            if (depth >= 2) return false; // apps sit one level down (two under NonPackaged)
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub is not null && Walk(sub, depth + 1)) return true;
            }
            return false;
        }
    }

    private const uint RegNotifyName = 0x1, RegNotifyLastSet = 0x4;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree, uint filter,
        Microsoft.Win32.SafeHandles.SafeWaitHandle evt, bool asynchronous);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
