using OrixNotch.Interop;

namespace OrixNotch.Services;

/// <summary>
/// Keeps the PC and screen awake until turned off or the app exits (Windows drops the request with the thread).
/// Call from the UI thread: the request belongs to the thread that made it.
/// </summary>
public static class KeepAwakeService
{
    public static bool On { get; private set; }

    public static event Action<bool>? Changed;

    public static void Toggle()
    {
        On = !On;
        Win32.SetThreadExecutionState(On
            ? Win32.ES_CONTINUOUS | Win32.ES_SYSTEM_REQUIRED | Win32.ES_DISPLAY_REQUIRED
            : Win32.ES_CONTINUOUS);
        Changed?.Invoke(On);
    }
}
