using System.Diagnostics;
using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <summary>
/// Which app is in front, for Settings → General "Hide for these apps". Event-driven (a foreground
/// WinEvent hook, delivered on the UI thread), so nothing polls. Our own windows are ignored: opening
/// the notch over a listed app must not count as switching away from it.
/// </summary>
/// <remarks>
/// shortcut: Store/UWP apps all run under ApplicationFrameHost, so they can't be told apart by process
/// name. Resolve the hosted app (GetPackageFamilyName on the child window) if users ask for it.
/// </remarks>
public sealed class ForegroundAppWatcher
{
    public static ForegroundAppWatcher Instance { get; } = new();

    /// <summary>Process name of the app in front: lowercase, no ".exe". Null until known.</summary>
    public string? Current { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    private Win32.WinEventProc? _proc; // held: the hook calls back through it
    private IntPtr _hook;
    private static readonly int OwnPid = Environment.ProcessId;

    /// <summary>Call on the UI thread (the hook delivers to the thread that set it).</summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = (_, _, hwnd, _, _, _, _) => Update(hwnd);
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT);
        Update(Win32.GetForegroundWindow());
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>
    /// Re-reads the foreground window (the notch's guard calls this every couple of seconds): closing the
    /// app in front doesn't always raise a foreground event, and a listed app that's gone must stop hiding the notch.
    /// </summary>
    public void Recheck() => Update(Win32.GetForegroundWindow());

    private void Update(IntPtr hwnd)
    {
        string? name = null; // no foreground window (the last one just closed): nothing is in front
        if (hwnd != IntPtr.Zero)
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == OwnPid) return;
            if (pid != 0) name = NameOf((int)pid);
        }
        if (name == Current) return;
        Current = name;
        Changed?.Invoke();
    }

    /// <summary>Process name, lowercase, no ".exe"; null if it has already exited.</summary>
    public static string? NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
