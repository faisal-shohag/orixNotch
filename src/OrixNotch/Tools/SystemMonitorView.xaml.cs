using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrixNotch.Interop;
using OrixNotch.Services;
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

    private readonly ProcessSampler _processes = new();
    private int _tick;

    public SystemMonitorView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Sample();
    }

    private int? _daysUntilFull;

    public void OnShown()
    {
        _daysUntilFull = null;
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
        if (_tick++ % 2 == 0) SampleTopApps(); // listing every process is heavier: every other second
    }

    private void SampleTopApps()
    {
        var top = _processes.Sample(3);
        TopApps.Children.Clear();
        if (top.Count == 0)
        {
            TopApps.Children.Add(new TextBlock { Text = "Measuring…", Style = (Style)FindResource("SubText"), FontSize = 11 });
            return;
        }
        foreach (var (name, cpu, count) in top)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var value = new TextBlock { Text = cpu < 1 ? "<1%" : $"{cpu:0}%", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) };
            value.SetResourceReference(TextBlock.ForegroundProperty, cpu >= 50 ? "BadBrush" : "SubTextBrush");
            Grid.SetColumn(value, 1);
            row.Children.Add(label);
            row.Children.Add(value);
            row.ToolTip = count > 1 ? $"{count} processes · click to end" : "Click to end";
            row.MouseLeftButtonUp += (_, _) => ConfirmEnd(name, count);
            TopApps.Children.Add(row);
        }
    }

    private static void ConfirmEnd(string name, int count)
    {
        using (NotchWindow.Instance?.HoldOpen())
        {
            var what = count > 1 ? $"all {count} {name} processes" : name;
            if (MessageBox.Show($"End {what}? Unsaved work in it will be lost.", "End task",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            if (ProcessSampler.EndApp(name) == 0)
                MessageBox.Show($"Windows didn't let OrixNotch end {name}. Try Task Manager.", "End task", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        CpuSub.Text = $"{Environment.ProcessorCount} threads · up {(up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m")}";
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
            DiskHistory.Record(drive.TotalFreeSpace);
            _daysUntilFull ??= DiskHistory.DaysUntilFull(drive.TotalFreeSpace) ?? -1; // once per opening: it moves by days
            DiskSub.Text = _daysUntilFull is > 0 and var days
                ? $"{Gb((ulong)drive.TotalFreeSpace)} free · full in ~{days}d"
                : $"{Gb((ulong)drive.TotalFreeSpace)} free";
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
        BatteryBar.SetResourceReference(ForegroundProperty, pct < 20 && !charging ? "BadBrush" : "AccentBrush");
    }

    private static string Gb(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:0.#} GB";

    private static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bytesPerSec / 1024:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };
}
