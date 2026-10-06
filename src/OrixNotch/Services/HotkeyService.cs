using System.Windows.Input;
using OrixNotch.Interop;

namespace OrixNotch.Services;

public sealed record HotkeyAction(string Id, string Name, string Description);

/// <summary>Global hotkeys from <see cref="AppSettings.Hotkeys"/>, registered on the notch window.</summary>
public static class HotkeyService
{
    public static IReadOnlyList<HotkeyAction> Actions { get; } =
    [
        new("toggle", "Open or close the notch", "Works from anywhere."),
        new("ask", "Ask AI", "Opens the notch on the AI chat."),
        new("clipboard", "Clipboard history", "Opens the notch on clipboard history."),
        new("teleprompter", "Teleprompter", "Opens the teleprompter and starts or pauses scrolling."),
    ];

    private const int BaseId = 0x4F00;
    private static IntPtr _hwnd;
    private static readonly Dictionary<int, string> Registered = new();

    public static event Action<string>? Triggered;

    public static void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        RegisterAll();
    }

    /// <summary>Re-registers everything; returns action ids that failed (combo taken by another app).</summary>
    public static List<string> RegisterAll()
    {
        UnregisterAll();
        var failed = new List<string>();
        var i = 0;
        foreach (var (action, gesture) in App.Settings.Hotkeys)
        {
            if (!TryParse(gesture, out var mods, out var key)) continue;
            var id = BaseId + i++;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (Win32.RegisterHotKey(_hwnd, id, mods | Win32.MOD_NOREPEAT, vk)) Registered[id] = action;
            else failed.Add(action);
        }
        return failed;
    }

    public static void UnregisterAll()
    {
        foreach (var id in Registered.Keys) Win32.UnregisterHotKey(_hwnd, id);
        Registered.Clear();
    }

    /// <returns>True when the WM_HOTKEY id belonged to us.</returns>
    public static bool Handle(int id)
    {
        if (!Registered.TryGetValue(id, out var action)) return false;
        Triggered?.Invoke(action);
        return true;
    }

    public static bool TryParse(string? gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win": modifiers |= Win32.MOD_WIN; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return false;
                    break;
            }
        }
        // Require a modifier so plain typing never triggers a global hotkey.
        return key != Key.None && modifiers != 0;
    }

    /// <summary>Builds "Ctrl+Alt+N" from a key event; null while only modifiers are held.</summary>
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return null;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
