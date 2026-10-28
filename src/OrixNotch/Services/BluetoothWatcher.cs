using System.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OrixNotch.Services;

/// <summary>
/// Paired Bluetooth devices (classic and LE) connecting or disconnecting: headphones, mice, controllers.
/// Each device's first report is its starting state; only later changes are raised (on the UI thread).
/// </summary>
public sealed class BluetoothWatcher
{
    public static BluetoothWatcher Instance { get; } = new();

    /// <summary>Device name, and whether it just connected.</summary>
    public event Action<string, bool>? Changed;

    private const string ConnectedProp = "System.Devices.Aep.IsConnected";
    private readonly Dictionary<string, (string Name, bool Connected)> _devices = new();
    private readonly List<DeviceWatcher> _watchers = new();

    public void Start()
    {
        if (_watchers.Count > 0) return;
        try
        {
            foreach (var selector in new[]
                     {
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                     })
            {
                var watcher = DeviceInformation.CreateWatcher(selector, [ConnectedProp], DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (_, d) =>
                {
                    var connected = IsConnected(d.Properties);
                    Post(() => _devices[d.Id] = (d.Name, connected));
                };
                watcher.Updated += (_, u) =>
                {
                    if (u.Properties.TryGetValue(ConnectedProp, out var value) && value is bool connected)
                        Post(() => OnUpdated(u.Id, connected));
                };
                watcher.Removed += (_, u) => Post(() => _devices.Remove(u.Id));
                watcher.Start();
                _watchers.Add(watcher);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex); // no Bluetooth radio or service
        }
    }

    private void OnUpdated(string id, bool connected)
    {
        if (!_devices.TryGetValue(id, out var device) || device.Connected == connected) return;
        _devices[id] = device with { Connected = connected };
        if (device.Name.Length > 0) Changed?.Invoke(device.Name, connected);
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(ConnectedProp, out var value) && value is true;

    private static void Post(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
