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

    /// <summary>A device that just connected reported its battery (0–100), a moment after <see cref="Changed"/>.</summary>
    public event Action<string, int>? Battery;

    private const string ConnectedProp = "System.Devices.Aep.IsConnected";
    private const string ContainerProp = "System.Devices.Aep.ContainerId";
    // DEVPKEY_Bluetooth_Battery: on the device node (not the endpoint), so it's looked up through the container.
    private const string BatteryProp = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private readonly Dictionary<string, (string Name, bool Connected, Guid? Container)> _devices = new();
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
                var watcher = DeviceInformation.CreateWatcher(selector, [ConnectedProp, ContainerProp], DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (_, d) =>
                {
                    var connected = IsConnected(d.Properties);
                    var container = d.Properties.TryGetValue(ContainerProp, out var c) && c is Guid g ? g : (Guid?)null;
                    Post(() => _devices[d.Id] = (d.Name, connected, container));
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
        if (device.Name.Length == 0) return;
        Changed?.Invoke(device.Name, connected);
        if (connected && device.Container is { } container) _ = ReadBatteryAsync(device.Name, container);
    }

    /// <summary>Headphones report their battery a moment after connecting; devices that never do are left alone.</summary>
    private async Task ReadBatteryAsync(string name, Guid container)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(1000); // the connect alert shows for 3 s: read within it
                var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                    [BatteryProp], DeviceInformationKind.Device);
                var level = nodes.Select(n => n.Properties.TryGetValue(BatteryProp, out var v) ? v : null).OfType<byte>().FirstOrDefault();
                if (level > 0)
                {
                    Post(() => Battery?.Invoke(name, Math.Min(100, (int)level)));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(ConnectedProp, out var value) && value is true;

    private static void Post(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
