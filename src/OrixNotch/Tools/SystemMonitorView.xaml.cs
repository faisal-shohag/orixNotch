using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrixNotch.Interop;
using OrixNotch.Shell;
using Forms = System.Windows.Forms;

namespace OrixNotch.Tools;

public partial class SystemMonitorView : UserControl, IToolView
{
    private const int HistoryLength = 60;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<double> _cpuHistory = new();
    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastRx, _lastTx;
    private DateTime _lastNetSample;

    public SystemMonitorView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Sample();
        MachineText.Text = Environment.MachineName;
    }

    public void OnShown()
    {
        Sample();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleDisk();
        SampleNetwork();
        SampleBattery();
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m";
    }

    private void SampleCpu()
    {
        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user)) return;
        if (_lastKernel != 0)
        {
            var idleD = idle - _lastIdle;
            var total = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle
            var usage = total > 0 ? Math.Clamp(100.0 * (total - idleD) / total, 0, 100) : 0;
            CpuText.Text = $"{usage:0}%";
            _cpuHistory.Enqueue(usage);
            while (_cpuHistory.Count > HistoryLength) _cpuHistory.Dequeue();
            DrawGraph();
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        CpuSub.Text = $"{Environment.ProcessorCount} threads";
    }

    private void DrawGraph()
    {
        var width = CpuGraph.ActualWidth > 0 ? CpuGraph.ActualWidth : 120;
        const double height = 26;
        var points = new PointCollection();
        var i = HistoryLength - _cpuHistory.Count;
        foreach (var v in _cpuHistory)
        {
            points.Add(new Point(width * i / (HistoryLength - 1), height - height * v / 100));
            i++;
        }
        CpuGraph.Points = points;
    }

    private void SampleMemory()
    {
        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref mem)) return;
        var used = mem.ullTotalPhys - mem.ullAvailPhys;
        MemText.Text = $"{mem.dwMemoryLoad}%";
        MemSub.Text = $"{Gb(used)} / {Gb(mem.ullTotalPhys)}";
        MemBar.Value = mem.dwMemoryLoad;
    }

    private void SampleDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var used = drive.TotalSize - drive.TotalFreeSpace;
            var pct = 100.0 * used / drive.TotalSize;
            DiskLabel.Text = $"DISK {root.TrimEnd('\\')}";
            DiskText.Text = $"{pct:0}%";
            DiskSub.Text = $"{Gb((ulong)drive.TotalFreeSpace)} free";
            DiskBar.Value = pct;
        }
        catch
        {
            DiskText.Text = "–";
        }
    }

    private void SampleNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch
        {
            return;
        }

        var now = DateTime.Now;
        if (_lastNetSample != default)
        {
            var secs = Math.Max(0.1, (now - _lastNetSample).TotalSeconds);
            DownText.Text = Rate(Math.Max(0, rx - _lastRx) / secs);
            UpText.Text = Rate(Math.Max(0, tx - _lastTx) / secs);
        }
        _lastRx = rx;
        _lastTx = tx;
        _lastNetSample = now;
    }

    private void SampleBattery()
    {
        var power = Forms.SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery))
        {
            BatteryText.Text = "AC";
            BatterySub.Text = "No battery";
            BatteryIcon.Kind = AppIcon.Bolt;
            BatteryBar.Value = 100;
            return;
        }

        var pct = power.BatteryLifePercent * 100;
        BatteryText.Text = $"{pct:0}%";
        BatteryBar.Value = pct;
        var charging = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        BatterySub.Text = charging
            ? (pct >= 99 ? "Fully charged" : "Charging")
            : power.BatteryLifeRemaining > 0
                ? $"{TimeSpan.FromSeconds(power.BatteryLifeRemaining):h\\:mm} remaining"
                : "On battery";
        BatteryIcon.Kind = charging ? AppIcon.Bolt : AppIcon.BatteryFull;
        BatteryBar.SetResourceReference(ForegroundProperty, pct < 20 && !charging ? "BadBrush" : "GoodBrush");
    }

    private static string Gb(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:0.#} GB";

    private static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bytesPerSec / 1024:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };
}
