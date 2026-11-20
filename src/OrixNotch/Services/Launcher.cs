using System.Diagnostics;

namespace OrixNotch.Services;

/// <summary>Opens files, folders, links and URIs (ms-settings:, ms-screenclip:) the way Explorer would.</summary>
public static class Launcher
{
    /// <summary>Opens with the default handler; false (logged) when Windows can't.</summary>
    public static bool Open(string target, string? args = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    /// <summary>An Explorer window on the file's folder with the file selected.</summary>
    public static bool Reveal(string path) => Open("explorer.exe", $"/select,\"{path}\"");
}
