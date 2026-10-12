using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OrixNotch.Services;
using OrixNotch.Shell;
using QRCoder;

namespace OrixNotch.Tools;

/// <summary>Phone transfer: nearby LocalSend devices, a QR code for the browser page, and transfers.</summary>
public partial class PhoneView : UserControl, IToolView
{
    private static PhoneTransferService Phone => PhoneTransferService.Instance;
    private string? _qrFor;

    public PhoneView()
    {
        InitializeComponent();
        DeviceList.ItemsSource = Phone.Devices;
        TransferList.ItemsSource = Phone.Transfers;
        Loaded += (_, _) =>
        {
            Phone.PropertyChanged += OnServiceChanged;
            Phone.Devices.CollectionChanged += OnListChanged;
            Phone.Transfers.CollectionChanged += OnListChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            Phone.PropertyChanged -= OnServiceChanged;
            Phone.Devices.CollectionChanged -= OnListChanged;
            Phone.Transfers.CollectionChanged -= OnListChanged;
        };
    }

    public void OnShown()
    {
        Phone.Refresh();
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
        if (!on) return;

        VisibleAs.Text = Phone.LocalIp.Length > 0 ? $"Visible as {Phone.Alias} · {Phone.LocalIp}" : $"Visible as {Phone.Alias}";
        NoDevices.Visibility = Phone.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTransfers.Visibility = Phone.Transfers.Count == 0 && Phone.Pending is null ? Visibility.Visible : Visibility.Collapsed;

        QrPanel.Visibility = Phone.WebUrl is null ? Visibility.Collapsed : Visibility.Visible;
        QrOff.Visibility = Phone.WebUrl is null ? Visibility.Visible : Visibility.Collapsed;
        if (Phone.WebUrl is { } url && url != _qrFor)
        {
            _qrFor = url;
            Qr.Source = QrImage(url);
            Qr.ToolTip = url; // to type in by hand when a camera won't scan
            IpText.Text = new Uri(url).Authority;
        }

        if (Phone.Pending is { } request)
        {
            RequestCard.Visibility = Visibility.Visible;
            RequestTitle.Text = $"{request.Sender} wants to send";
            RequestSub.Text = request.Summary;
        }
        else
        {
            RequestCard.Visibility = Visibility.Collapsed;
            TrustBox.IsChecked = false;
        }

        if (Phone.Queued is { Count: > 0 } queued)
        {
            QueuedBanner.Visibility = Visibility.Visible;
            QueuedText.Text = queued.Count == 1 ? $"Pick a device for {Path.GetFileName(queued[0])}" : $"Pick a device for {queued.Count} items";
        }
        else
        {
            QueuedBanner.Visibility = Visibility.Collapsed;
        }
    }

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

    private void OnRefresh(object sender, RoutedEventArgs e) => Phone.Refresh();

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

    private void OnClearQueued(object sender, RoutedEventArgs e) => Phone.Queued = null;

    private void OnAccept(object sender, RoutedEventArgs e) => Phone.Answer(true, TrustBox.IsChecked == true);

    private void OnDecline(object sender, RoutedEventArgs e) => Phone.Answer(false);

    private void OnCancelTransfer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhoneTransfer transfer) Phone.CancelTransfer(transfer);
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhoneDevice device) return;
        if (Phone.Queued is { Count: > 0 } queued)
        {
            _ = Phone.SendAsync(device, queued);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Send to {device.Alias}", Filter = "All files|*.*", Multiselect = true };
        using (NotchWindow.Instance?.HoldOpen())
        {
            if (dialog.ShowDialog(NotchWindow.Instance) != true) return;
        }
        _ = Phone.SendAsync(device, dialog.FileNames);
    }

    private void OnDeviceDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (sender is Border row) row.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDeviceDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border row) row.ClearValue(Border.BackgroundProperty); // back to the template's hover look
    }

    private void OnDeviceDrop(object sender, DragEventArgs e)
    {
        OnDeviceDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files ||
            (sender as FrameworkElement)?.DataContext is not PhoneDevice device) return;
        _ = Phone.SendAsync(device, files);
        e.Handled = true; // not onto the Shelf as well
    }
}
