using System.IO;
using Microsoft.Win32;

namespace OrixNotch.Services;

/// <summary>The user's Downloads and Screenshots folders, even if they moved them (Explorer's "User Shell Folders").</summary>
public static class ShellFolders
{
    public static string Downloads => Known("{374DE290-123F-4565-9164-39C4925E467B}",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    public static string Screenshots => Known("{B7BEDE81-DF94-4682-A7D8-57A52620B86F}",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));

    private static string Known(string id, string fallback)
    {
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        return Registry.GetValue(key, id, null) is string { Length: > 0 } path ? Environment.ExpandEnvironmentVariables(path) : fallback;
    }
}
