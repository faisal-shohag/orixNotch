using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace OrixNotch.Services;

/// <summary>A one-click action: launch an app, file, folder, URL or ms-settings: page.</summary>
public sealed class ShortcutItem
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class ShortcutsService
{
    private const string FileName = "shortcuts.json";

    public static ShortcutsService Instance { get; } = new();

    public ObservableCollection<ShortcutItem> Items { get; }

    private ShortcutsService()
    {
        var path = Storage.PathOf(FileName);
        var items = File.Exists(path) ? Storage.Load<List<ShortcutItem>>(FileName) : Defaults();
        Items = new ObservableCollection<ShortcutItem>(items);
        if (!File.Exists(path)) Save();
    }

    private static List<ShortcutItem> Defaults() =>
    [
        new() { Name = "Snip screen", Target = "ms-screenclip:" },
        new() { Name = "Lock PC", Target = "rundll32.exe", Arguments = "user32.dll,LockWorkStation" },
        new() { Name = "Task Manager", Target = "taskmgr.exe" },
        new() { Name = "Calculator", Target = "calc.exe" },
        new() { Name = "Notepad", Target = "notepad.exe" },
        new() { Name = "Downloads", Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
        new() { Name = "Bluetooth", Target = "ms-settings:bluetooth" },
        new() { Name = "Wi-Fi", Target = "ms-settings:network-wifi" },
        new() { Name = "Sound", Target = "ms-settings:sound" },
        new() { Name = "Display", Target = "ms-settings:display" },
        new() { Name = "Focus", Target = "ms-settings:quiethours" },
        new() { Name = "Battery", Target = "ms-settings:batterysaver" },
    ];

    public static bool Run(ShortcutItem item, out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo(item.Target, item.Arguments ?? "") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Add(ShortcutItem item)
    {
        Items.Add(item);
        Save();
    }

    public void Remove(ShortcutItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Save() => Storage.Save(FileName, Items.ToList());
}
