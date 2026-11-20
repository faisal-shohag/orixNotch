using System.Runtime.InteropServices;
using System.Windows;
using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <summary>The text selected in the app in front, read with a simulated Ctrl+C (Explain selected text).</summary>
public static class SelectionService
{
    private const byte VK_CONTROL = 0x11, VK_C = 0x43;
    private static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, left / right Win

    /// <summary>
    /// Copies the selection and puts the clipboard's previous text back. Null when nothing was selected (the
    /// clipboard didn't change). Call before the notch takes focus. shortcut: only text is restored, so a copied
    /// image or file list is replaced; restore the whole data object if people miss it.
    /// </summary>
    public static async Task<string?> CopyAsync()
    {
        // The hotkey's own modifiers are still down: Ctrl+C with Alt held would be Ctrl+Alt+C. Wait up to a second.
        for (var i = 0; i < 50 && Modifiers.Any(k => (Win32.GetAsyncKeyState(k) & 0x8000) != 0); i++) await Task.Delay(20);

        var previous = Try(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        var before = Win32.GetClipboardSequenceNumber();
        ClipboardService.Instance.IgnoreFor(TimeSpan.FromSeconds(2)); // not a copy you made: keep it out of history
        Win32.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(VK_C, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(VK_C, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Win32.keybd_event(VK_CONTROL, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);

        for (var i = 0; i < 30 && Win32.GetClipboardSequenceNumber() == before; i++) await Task.Delay(20);
        if (Win32.GetClipboardSequenceNumber() == before) return null;
        await Task.Delay(50); // let the app finish writing every format
        var text = Try(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (previous is not null) ClipboardService.Instance.CopyQuiet(previous);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>The clipboard is shared: another app holding it open throws, so try twice.</summary>
    private static string? Try(Func<string?> read)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (COMException) when (attempt == 0)
            {
                Thread.Sleep(30);
            }
            catch (COMException)
            {
                return null;
            }
        }
    }
}
