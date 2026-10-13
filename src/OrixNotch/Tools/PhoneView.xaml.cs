using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrixNotch.Services;
using OrixNotch.Shell;
using QRCoder;

namespace OrixNotch.Tools;

/// <summary>Phone transfer: a QR code for the browser page, what the PC offers the phone, and transfer activity.</summary>
public partial class PhoneView : UserControl, IToolView
{
    private static PhoneTransferService Phone => PhoneTransferService.Instance;
    private string? _qrFor;

    public PhoneView()
    {
        InitializeComponent();
        TransferList.ItemsSource = Phone.Transfers;
        OutboxList.ItemsSource = Phone.Outbox;
        Loaded += (_, _) =>
        {
            Phone.PropertyChanged += OnServiceChanged;
            Phone.Transfers.CollectionChanged += OnListChanged;
            Phone.Outbox.CollectionChanged += OnListChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            Phone.PropertyChanged -= OnServiceChanged;
            Phone.Transfers.CollectionChanged -= OnListChanged;
            Phone.Outbox.CollectionChanged -= OnListChanged;
        };
    }

    public void OnShown()
    {
        ApplyHeight();
        Update();
    }

    public void OnHidden()
    {
    }

    private void OnServiceChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void OnListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Update();

    private void Update()
    {
        var on = Phone.IsRunning;
        OffState.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        OnState.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TurnOnButton.IsEnabled = !Phone.IsStarting;
        TurnOnButton.Content = Phone.IsStarting ? "Starting…" : "Turn on";
        OffStatus.Text = Phone.Status;
        StatusText.Text = Phone.Status;
        var waiting = Phone.Outbox.Count;
        OffWaiting.Visibility = !on && waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        OffWaiting.Text = waiting == 1 ? "1 item waiting · turn on to send it" : $"{waiting} items waiting · turn on to send them";
        if (!on) return;

        LinkText.Text = Phone.PhoneConnected ? "Phone connected" : "Waiting for phone · scan the code with its camera";
        LinkDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Phone.PhoneConnected ? "GoodBrush" : "SubTextBrush");
        NoTransfers.Visibility = Phone.Transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DropHint.Visibility = waiting == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearOutboxButton.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;

        QrPanel.Visibility = Phone.WebUrl is null ? Visibility.Collapsed : Visibility.Visible;
        QrOff.Visibility = Phone.WebUrl is null ? Visibility.Visible : Visibility.Collapsed;
        if (Phone.WebUrl is { } url && url != _qrFor)
        {
            _qrFor = url;
            Qr.Source = QrImage(url);
            Qr.ToolTip = url; // to type in by hand when a camera won't scan
            IpText.Text = new Uri(url).Authority;
        }
    }

    /// <summary>A tall tab has room under the code for the three steps; a short one keeps just the code.</summary>
    private void OnQrCardSizeChanged(object sender, SizeChangedEventArgs e) =>
        Steps.Visibility = e.NewSize.Height >= 330 ? Visibility.Visible : Visibility.Collapsed;

    private static BitmapImage QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6, drawQuietZones: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async void OnTurnOn(object sender, RoutedEventArgs e)
    {
        App.Settings.PhoneEnabled = true;
        SettingsService.Save(false);
        await Phone.SyncAsync();
        if (!Phone.IsRunning)
        {
            App.Settings.PhoneEnabled = false;
            SettingsService.Save(false);
        }
    }

    private async void OnTurnOff(object sender, RoutedEventArgs e)
    {
        App.Settings.PhoneEnabled = false;
        SettingsService.Save(false);
        await Phone.SyncAsync();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Phone.ReceiveFolder);
            Process.Start(new ProcessStartInfo(Phone.ReceiveFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Copies text the phone sent; for a file, opens the receive folder in Explorer with it selected.</summary>
    private void OnTransferClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhoneTransfer { Text: { } text } textTransfer)
        {
            try
            {
                Clipboard.SetText(text);
                textTransfer.State = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                textTransfer.State = "Clipboard busy · click again";
            }
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is not PhoneTransfer { SavedPath: { } path }) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Send to phone", Filter = "All files|*.*", Multiselect = true };
        using (NotchWindow.Instance?.HoldOpen())
        {
            if (dialog.ShowDialog(NotchWindow.Instance) != true) return;
        }
        Phone.Send(dialog.FileNames);
    }

    private void OnSendClipboard(object sender, RoutedEventArgs e)
    {
        if (!Phone.SendClipboard()) StatusText.Text = "Nothing on the clipboard to send.";
    }

    private void OnClearOutbox(object sender, RoutedEventArgs e) => Phone.ClearOutbox();

    private void OnRemoveOutbox(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhoneOutboxItem item) Phone.RemoveFromOutbox(item);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropPayload.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files, browser images or text dropped on the Phone tab go to the phone, not onto the Shelf.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DropPayload.Read(e.Data) is not { } payload) return;
        e.Handled = true;
        NotchWindow.Instance?.DeliverDrop(payload, NotchWindow.DropTarget.Phone);
    }

    // ---------------------------------------------------------------- resize (same handle as Ask AI)

    private const double DefaultHeight = 220;
    private const double MinPhoneHeight = 200;
    private const double MaxPhoneHeight = 520;
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;

    private static void ApplyHeight() =>
        NotchWindow.Instance?.SetToolSize(720, Math.Clamp(App.Settings.PhoneHeight, MinPhoneHeight, MaxPhoneHeight));

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Settings.PhoneHeight = DefaultHeight;
            ApplyHeight();
            SettingsService.Save(notify: false);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartY = PointToScreen(e.GetPosition(this)).Y;
        _dragStartHeight = ActualHeight; // what's shown, which may be capped to fit the screen
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var dy = (PointToScreen(e.GetPosition(this)).Y - _dragStartY) / dpi;
        var want = Math.Clamp(_dragStartHeight + dy, MinPhoneHeight, MaxPhoneHeight);
        App.Settings.PhoneHeight = NotchWindow.Instance?.SetToolSize(720, want) ?? want;
    }

    private void OnGripUp(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grip.ReleaseMouseCapture();
        SettingsService.Save(notify: false);
    }

    private void OnCancelTransfer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhoneTransfer transfer) Phone.CancelTransfer(transfer);
    }
}
