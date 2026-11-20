using System.Runtime.InteropServices;
using System.Windows;

namespace OrixNotch.Services;

/// <summary>
/// Master volume changes on the default speakers, from Core Audio's endpoint-volume callback.
/// Raised on the UI thread with the level (0..1) and mute state. Also mutes the default microphone.
/// </summary>
public sealed class VolumeService
{
    public static VolumeService Instance { get; } = new();

    public event Action<float, bool>? Changed;

    /// <summary>The default microphone was muted or unmuted (from here or elsewhere: a headset button, Windows).</summary>
    public event Action<bool>? MicChanged;

    public bool MicMuted { get; private set; }
    public bool HasMic => _mic is not null;

    // Held so the COM objects and our callbacks stay alive.
    private IAudioEndpointVolume? _endpoint;
    private Callback? _callback;
    private IAudioEndpointVolume? _mic;
    private Callback? _micCallback;

    // shortcut: listens to the default devices at start only; switching speakers or mic needs a restart. Add
    // IMMNotificationClient (OnDefaultDeviceChanged) if users ask.
    public void Start()
    {
        if (_endpoint is null)
        {
            _callback = new Callback((level, muted) => Changed?.Invoke(level, muted));
            _endpoint = Open(0 /* eRender */, _callback);
        }
        if (_mic is null)
        {
            _micCallback = new Callback((_, muted) =>
            {
                if (muted == MicMuted) return; // level changes also call back
                MicMuted = muted;
                MicChanged?.Invoke(muted);
            });
            _mic = Open(1 /* eCapture */, _micCallback);
            if (_mic is not null && _mic.GetMute(out var m) == 0) MicMuted = m != 0;
        }
    }

    private static IAudioEndpointVolume? Open(int dataFlow, Callback callback)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, 1 /* eMultimedia */, out var device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) != 0) return null;
            var endpoint = (IAudioEndpointVolume)obj;
            return endpoint.RegisterControlChangeNotify(callback) == 0 ? endpoint : null;
        }
        catch (COMException)
        {
            return null; // no such device / audio service
        }
    }

    /// <summary>Mutes or unmutes the default microphone; <see cref="MicChanged"/> follows from Core Audio.</summary>
    public void ToggleMic()
    {
        if (_mic is null) return;
        var context = Guid.Empty;
        _mic.SetMute(MicMuted ? 0 : 1, ref context);
    }

    // Public: Core Audio calls back through a COM wrapper, which needs visible types.
    public sealed class Callback(Action<float, bool> raise) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID (16 bytes), BOOL bMuted, float fMasterVolume, ...
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            Application.Current?.Dispatcher.BeginInvoke(() => raise(level, muted));
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

    // Methods must stay in vtable order (endpointvolume.h) up to the last one we call.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }
}
