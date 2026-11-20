using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly Queue<double> _down = new(), _up = new(); // last half-minute of bytes/s, for the sparklines
    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastRx, _lastTx;
    private DateTime _lastNetSample;

    private readonly ProcessSampler _processes = new();
    private int _tick;

    public SystemMonitorView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Sample();
        (CpuBar.Icon, MemBar.Icon, DiskBar.Icon) = (AppIcon.Cpu, AppIcon.Memory, AppIcon.Drive);
    }

    private int? _daysUntilFull;

    public void OnShown()
    {
        _daysUntilFull = null;
        // The last readings are from when the tool was hidden: a rate over that gap would show a minutes-long average,
        // and the graph would join old history to new. Start fresh.
        (_lastIdle, _lastKernel, _lastUser, _lastNetSample) = (0, 0, 0, default);
        _cpuHistory.Clear();
        _down.Clear();
        _up.Clear();
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

    // Top apps: one row per app, kept between samples so it can glide to its new rank, its bar easing to the new
    // share; an app that enters fades and slides in, one that drops out fades away.
    private const double RowHeight = 22;
    private readonly Dictionary<string, TopRow> _rows = new();
    private TextBlock? _measuring;

    private sealed record TopRow(Grid Row, TranslateTransform Shift, GlowBar Bar, TextBlock Value)
    {
        public int Count { get; set; }
    }

    private void SampleTopApps()
    {
        var top = _processes.Sample(3);
        if (top.Count == 0)
        {
            if (_rows.Count == 0 && _measuring is null)
            {
                _measuring = new TextBlock { Text = "Measuring…", Style = (Style)FindResource("SubText"), FontSize = 11, VerticalAlignment = VerticalAlignment.Top };
                TopApps.Children.Add(_measuring);
            }
            return;
        }
        if (_measuring is not null)
        {
            TopApps.Children.Remove(_measuring);
            _measuring = null;
        }

        var names = top.Select(t => t.Name).ToHashSet();
        foreach (var (name, gone) in _rows.Where(r => !names.Contains(r.Key)).ToList())
        {
            _rows.Remove(name);
            Leave(gone);
        }
        for (var i = 0; i < top.Count; i++)
        {
            var (name, cpu, count) = top[i];
            var y = i * RowHeight;
            if (!_rows.TryGetValue(name, out var row))
            {
                row = NewRow(name);
                _rows[name] = row;
                TopApps.Children.Add(row.Row);
                row.Shift.Y = y;
                Enter(row);
            }
            else Glide(row.Shift, TranslateTransform.YProperty, y);
            row.Count = count;
            row.Bar.Value = cpu / 100;
            row.Bar.Tone = cpu >= 50 ? "BadBrush" : "AccentBrush";
            row.Value.Text = cpu < 1 ? "<1%" : $"{cpu:0}%";
            row.Value.SetResourceReference(TextBlock.ForegroundProperty, cpu >= 50 ? "BadBrush" : "SubTextBrush");
            row.Row.ToolTip = count > 1 ? $"{count} processes · click to end" : "Click to end";
        }
    }

    /// <summary>name · a small glowing bar of its CPU share · the number</summary>
    private TopRow NewRow(string name)
    {
        var shift = new TranslateTransform();
        var grid = new Grid
        {
            Height = RowHeight, VerticalAlignment = VerticalAlignment.Top, RenderTransform = shift,
            Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        var label = new TextBlock { Text = name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var bar = new GlowBar { BarHeight = 4, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        var value = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 2);
        grid.Children.Add(label);
        grid.Children.Add(bar);
        grid.Children.Add(value);
        var row = new TopRow(grid, shift, bar, value);
        grid.MouseLeftButtonUp += (_, _) => ConfirmEnd(name, row.Count);
        return row;
    }

    private static void Enter(TopRow row)
    {
        row.Row.Opacity = 0;
        row.Shift.X = 10;
        Glide(row.Row, OpacityProperty, 1);
        Glide(row.Shift, TranslateTransform.XProperty, 0);
    }

    private void Leave(TopRow row)
    {
        row.Row.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, Motion.Scale(260)) { EasingFunction = Motion.Out };
        fade.Completed += (_, _) => TopApps.Children.Remove(row.Row);
        row.Row.BeginAnimation(OpacityProperty, fade);
        Glide(row.Shift, TranslateTransform.XProperty, -10);
    }

    /// <summary>Eases a property to a value from wherever it is now (a running glide hands over smoothly).</summary>
    private static void Glide(DependencyObject target, DependencyProperty property, double to)
    {
        var animation = new DoubleAnimation(to, Motion.Scale(380)) { EasingFunction = Motion.Out };
        if (target is UIElement element) element.BeginAnimation(property, animation);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation);
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
            CpuBar.Value = usage / 100;
            CpuBar.HeadText = $"{usage:0}%";
            CpuBar.Tone = GlowBar.ToneFor(usage / 100);
            _cpuHistory.Enqueue(usage);
            while (_cpuHistory.Count > HistoryLength) _cpuHistory.Dequeue();
            CpuGraph.Values = [.. _cpuHistory];
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        CpuSub.Text = $"{Environment.ProcessorCount} threads · up {(up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m")}";
    }

    private void SampleMemory()
    {
        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref mem)) return;
        var used = mem.ullTotalPhys - mem.ullAvailPhys;
        MemText.Text = $"{Gb(used)} of {Gb(mem.ullTotalPhys)}";
        MemSub.Text = $"{Gb(mem.ullAvailPhys)} free";
        MemBar.Value = mem.dwMemoryLoad / 100.0;
        MemBar.HeadText = $"{mem.dwMemoryLoad}%";
        MemBar.Tone = GlowBar.ToneFor(mem.dwMemoryLoad / 100.0);
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
            DiskText.Text = $"{Gb((ulong)drive.TotalFreeSpace)} free";
            DiskHistory.Record(drive.TotalFreeSpace);
            _daysUntilFull ??= DiskHistory.DaysUntilFull(drive.TotalFreeSpace) ?? -1; // once per opening: it moves by days
            DiskSub.Text = _daysUntilFull is > 0 and var days
                ? $"of {Gb((ulong)drive.TotalSize)} · full in ~{days}d"
                : $"of {Gb((ulong)drive.TotalSize)}";
            DiskBar.Value = pct / 100;
            DiskBar.HeadText = $"{pct:0}%";
            DiskBar.Tone = GlowBar.ToneFor(pct / 100);
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
            var down = Math.Max(0, rx - _lastRx) / secs;
            var up = Math.Max(0, tx - _lastTx) / secs;
            DownText.Text = IdleInfoService.Rate(down);
            UpText.Text = IdleInfoService.Rate(up);
            DownGraph.Values = Push(_down, down);
            UpGraph.Values = Push(_up, up);
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
            BatteryText.Text = "Plugged in";
            BatterySub.Text = "No battery in this PC";
            BatteryIcon.Kind = AppIcon.Bolt;
            BatteryBar.Value = 1;
            BatteryBar.Icon = AppIcon.Bolt;
            BatteryBar.HeadText = "AC";
            BatteryBar.Tone = "GoodBrush";
            return;
        }

        var pct = power.BatteryLifePercent * 100;
        var charging = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        BatteryText.Text = charging
            ? (pct >= 99 ? "Fully charged" : "Charging")
            : power.BatteryLifeRemaining > 0
                ? $"{TimeSpan.FromSeconds(power.BatteryLifeRemaining):h\\:mm} left"
                : "On battery";
        BatterySub.Text = charging ? "Plugged in" : "Unplugged";
        BatteryIcon.Kind = charging ? AppIcon.Bolt : AppIcon.BatteryFull;
        BatteryBar.Value = pct / 100;
        BatteryBar.Icon = charging ? AppIcon.Bolt : AppIcon.BatteryFull;
        BatteryBar.HeadText = $"{pct:0}%";
        // Charging glows green; low on battery glows red.
        BatteryBar.Tone = charging ? "GoodBrush" : GlowBar.ToneFor(pct / 100, lowIsBad: true);
    }

    private static double[] Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > 30) history.Dequeue();
        return [.. history];
    }

    private static string Gb(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:0.#} GB";
}
