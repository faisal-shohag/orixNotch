using System.Runtime.InteropServices;
using System.Windows;

namespace OrixNotch.Services;

/// <summary>
/// Master volume changes on the default speakers, from Core Audio's endpoint-volume callback.
/// Raised on the UI thread with the level (0..1) and mute state.
/// </summary>
public sealed class VolumeService
{
    public static VolumeService Instance { get; } = new();

    public event Action<float, bool>? Changed;

    // Held so the COM objects and our callback stay alive.
    private IAudioEndpointVolume? _endpoint;
    private Callback? _callback;

    // shortcut: listens to the default device at start only; switching speakers needs a restart. Add
    // IMMNotificationClient (OnDefaultDeviceChanged) if users ask.
    public void Start()
    {
        if (_endpoint is not null) return;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) != 0) return;
            _endpoint = (IAudioEndpointVolume)obj;
            _callback = new Callback(this);
            if (_endpoint.RegisterControlChangeNotify(_callback) != 0) _endpoint = null;
        }
        catch (COMException)
        {
            _endpoint = null; // no audio device / service
        }
    }

    private void Raise(float level, bool muted) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(level, muted));

    // Public: Core Audio calls back through a COM wrapper, which needs visible types.
    public sealed class Callback(VolumeService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID (16 bytes), BOOL bMuted, float fMasterVolume, ...
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            owner.Raise(level, muted);
            return 0;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }
}
