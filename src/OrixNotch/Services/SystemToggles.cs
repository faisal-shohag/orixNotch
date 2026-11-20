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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
