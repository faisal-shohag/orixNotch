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

    /// <summary>Speaker volume, 0..1 (null without an audio device).</summary>
    public float? MasterVolume
    {
        get => _endpoint is not null && _endpoint.GetMasterVolumeLevelScalar(out var level) == 0 ? level : null;
        set
        {
            if (_endpoint is null || value is not { } level) return;
            var context = Guid.Empty;
            _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), ref context);
        }
    }

    /// <summary>One app's sound in the Windows volume mixer (all its sessions together), adjustable here.</summary>
    public sealed class AppAudio(string app, int pid, List<object> sessions)
    {
        public string App { get; } = app;   // process name; "" for Windows' system sounds
        public int ProcessId { get; } = pid;

        public float Volume
        {
            get => sessions.Count > 0 && ((ISimpleAudioVolume)sessions[0]).GetMasterVolume(out var v) == 0 ? v : 1;
            set
            {
                var context = Guid.Empty;
                foreach (ISimpleAudioVolume s in sessions) s.SetMasterVolume(Math.Clamp(value, 0, 1), ref context);
            }
        }

        public bool Muted
        {
            get => sessions.Count > 0 && ((ISimpleAudioVolume)sessions[0]).GetMute(out var m) == 0 && m != 0;
            set
            {
                var context = Guid.Empty;
                foreach (ISimpleAudioVolume s in sessions) s.SetMute(value ? 1 : 0, ref context);
            }
        }
    }

    /// <summary>Apps with sound on the default speakers (as in Windows' volume mixer), playing ones first.</summary>
    public List<AppAudio> Apps()
    {
        var result = new List<AppAudio>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return result;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) != 0) return result;
            if (((IAudioSessionManager2)obj).GetSessionEnumerator(out var sessions) != 0 || sessions.GetCount(out var count) != 0) return result;
            var groups = new Dictionary<string, (int Pid, bool Active, List<object> Sessions)>();
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0 || session.GetState(out var state) != 0 || state == 2 /* expired */) continue;
                session.GetProcessId(out var pid);
                var app = session.IsSystemSoundsSession() == 0 ? "" : ForegroundAppWatcher.NameOf((int)pid);
                if (app is null) continue; // the app has exited
                var group = groups.TryGetValue(app, out var g) ? g : (Pid: (int)pid, Active: false, Sessions: new List<object>());
                group.Sessions.Add(session);
                groups[app] = (group.Pid, group.Active || state == 1, group.Sessions);
            }
            result.AddRange(groups.OrderByDescending(g => g.Value.Active).ThenBy(g => g.Key.Length == 0)
                .Select(g => new AppAudio(g.Key, g.Value.Pid, g.Value.Sessions)));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            App.Log(ex);
        }
        return result;
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

    // Per-app sessions (audiopolicy.h), vtable order.
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr group, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr group, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession(); // S_OK (0) for system sounds
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }
}
