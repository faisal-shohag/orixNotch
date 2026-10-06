using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrixNotch.Services;

/// <summary>Script + play state for the teleprompter; the view does the scrolling.</summary>
public sealed partial class TeleprompterService : ObservableObject
{
    private static readonly string ScriptPath = Storage.PathOf("teleprompter.txt");

    public static TeleprompterService Instance { get; } = new();

    [ObservableProperty] private string _script = "";
    [ObservableProperty] private bool _isRunning;

    /// <summary>Raised when the script should restart from the top.</summary>
    public event Action? Restarted;

    private TeleprompterService()
    {
        try
        {
            if (File.Exists(ScriptPath)) _script = File.ReadAllText(ScriptPath);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    partial void OnScriptChanged(string value)
    {
        try
        {
            File.WriteAllText(ScriptPath, value);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    public bool HasScript => !string.IsNullOrWhiteSpace(Script);

    public void Toggle()
    {
        if (!HasScript) return;
        IsRunning = !IsRunning;
    }

    public void Restart()
    {
        Restarted?.Invoke();
    }

    /// <summary>Scroll speed in DIPs per second for the 0..1 speed setting.</summary>
    public static double PixelsPerSecond => 8 + App.Settings.PrompterSpeed * 90;
}
