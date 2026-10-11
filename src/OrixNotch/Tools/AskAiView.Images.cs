using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// Ask AI image attachments: paste (Ctrl+V), drop or pick images, preview them above the composer,
/// and send them with the message to models that read images (Claude, ChatGPT, Gemini).
/// Sent images are kept under <c>askai-images</c> so a restored chat still shows (and resends) them.
/// </summary>
public partial class AskAiView
{
    private const int MaxImages = 4;
    /// <summary>Longest side sent to the model; larger images cost more tokens without helping.</summary>
    private const int MaxImageSide = 1568;
    private const string ImageDir = "askai-images";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    private sealed record PendingImage(AiImage Image, BitmapSource Preview);

    private readonly List<PendingImage> _pending = new();

    private void InitImages()
    {
        CommandManager.AddPreviewExecutedHandler(Input, OnPreviewPaste);
    }

    private bool ModelReadsImages => AiService.SupportsImages(AiService.ActiveProvider, App.Settings.AiModel);

    /// <summary>Photo button and the hint under the thumbnails follow the selected model.</summary>
    private void UpdateAttachState()
    {
        var reads = ModelReadsImages;
        var model = AiService.LabelFor(AiService.ActiveProvider, App.Settings.AiModel);
        AttachButton.Visibility = Input.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        AttachButton.IsEnabled = reads && _pending.Count < MaxImages;
        AttachButton.ToolTip = reads ? "Attach images (or paste / drop them)" : $"{model} can't read images";
        ToolTipService.SetShowOnDisabled(AttachButton, true);
        AttachRow.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachHint.Text = _pending.Count > 0 && !reads ? $"{model} can't read images — pick another model." : "";
        AttachHint.SetResourceReference(TextBlock.ForegroundProperty, reads ? "SubTextBrush" : "BadBrush");
    }

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach images",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp",
            Multiselect = true,
        };
        // The dialog takes focus; keep the notch from closing behind it.
        using (NotchWindow.Instance?.HoldOpen())
        {
            if (dialog.ShowDialog(NotchWindow.Instance) != true) return;
        }
        foreach (var file in dialog.FileNames) AddImageFile(file);
        Input.Focus();
    }

    private void OnPreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ModelReadsImages) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
                if (files.Count == 0) return;
                foreach (var file in files) AddImageFile(file);
                e.Handled = true;
                return;
            }
            if (ClipboardImage() is { } bitmap)
            {
                AddImage(bitmap, flattenAlpha: false);
                e.Handled = true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or NotSupportedException or IOException)
        {
            // Clipboard busy or the image is in a format WPF can't read; fall back to a normal paste.
        }
    }

    /// <summary>
    /// The clipboard image. Prefers the "PNG" format screenshot tools put there, since the DIB that
    /// <see cref="Clipboard.GetImage"/> reads often has an empty alpha channel.
    /// </summary>
    private static BitmapSource? ClipboardImage()
    {
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        return Clipboard.ContainsImage() ? new FormatConvertedBitmap(Clipboard.GetImage(), PixelFormats.Bgr24, null, 0) : null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        e.Effects = ModelReadsImages && files.Any(IsImageFile) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || !ModelReadsImages) return;
        foreach (var file in ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(IsImageFile)) AddImageFile(file);
        e.Handled = true;
        Input.Focus();
    }

    private static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void AddImageFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            AddImage(frame, flattenAlpha: true);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException or ArgumentException)
        {
            AttachRow.Visibility = Visibility.Visible;
            AttachHint.Text = $"Couldn't open {Path.GetFileName(path)}.";
        }
    }

    private void AddImage(BitmapSource source, bool flattenAlpha)
    {
        if (_pending.Count >= MaxImages) return;
        var (image, preview) = Encode(source, flattenAlpha);
        _pending.Add(new PendingImage(image, preview));
        RenderAttachments();
    }

    /// <summary>
    /// Scales to <see cref="MaxImageSide"/> and encodes as JPEG. Transparent areas of image files are
    /// painted white first so they don't turn black.
    /// </summary>
    private static (AiImage Image, BitmapSource Preview) Encode(BitmapSource source, bool flattenAlpha)
    {
        var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(source.PixelWidth, source.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

        BitmapSource bitmap;
        if (flattenAlpha && source.Format.Masks.Count == 4)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawImage(source, new Rect(0, 0, width, height));
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            bitmap = new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);
        }
        else
        {
            BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            bitmap = scaled.Format == PixelFormats.Bgr24 ? scaled : new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        var bytes = output.ToArray();
        return (new AiImage("image/jpeg", Convert.ToBase64String(bytes)), Decode(bytes));
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RenderAttachments()
    {
        Attachments.Children.Clear();
        foreach (var pending in _pending.ToList())
        {
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -5, -5, 0),
                ToolTip = "Remove",
                Content = new HeroIcon { Kind = AppIcon.XMark, Width = 9, Height = 9 },
            };
            remove.SetResourceReference(BackgroundProperty, "Surface2Brush");
            remove.SetResourceReference(ForegroundProperty, "TextBrush");
            remove.Click += (_, _) =>
            {
                _pending.Remove(pending);
                RenderAttachments();
            };
            var cell = new Grid { Margin = new Thickness(0, 4, 10, 0) };
            cell.Children.Add(Thumbnail(pending.Preview, 44));
            cell.Children.Add(remove);
            Attachments.Children.Add(cell);
        }
        UpdateAttachState();
        UpdateSendState();
    }

    /// <summary>Rounded, cropped image tile.</summary>
    private static FrameworkElement Thumbnail(ImageSource source, double size, double? width = null)
    {
        var w = width ?? size;
        return new Border
        {
            Width = w,
            Height = size,
            CornerRadius = new CornerRadius(8),
            Background = new ImageBrush(source) { Stretch = Stretch.UniformToFill },
        };
    }

    /// <summary>Thumbnails shown in a sent message, sized to their aspect ratio.</summary>
    private static FrameworkElement MessageImages(IEnumerable<BitmapSource> images)
    {
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var image in images)
        {
            var ratio = image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : 1;
            var tile = Thumbnail(image, 96, Math.Clamp(96 * ratio, 60, 220));
            ((FrameworkElement)tile).Margin = new Thickness(6, 0, 0, 6);
            panel.Children.Add(tile);
        }
        return panel;
    }

    /// <summary>Writes sent images next to the chat history; returns their file names.</summary>
    private static string[] SaveImages(IReadOnlyList<AiImage> images)
    {
        var dir = Storage.Dir(ImageDir);
        var names = new List<string>();
        foreach (var image in images)
        {
            var name = $"{Guid.NewGuid():N}.jpg";
            try
            {
                File.WriteAllBytes(Path.Combine(dir, name), Convert.FromBase64String(image.Base64));
                names.Add(name);
            }
            catch (IOException ex)
            {
                App.Log(ex);
            }
        }
        return names.ToArray();
    }

    /// <summary>Images of a restored chat entry; missing files are skipped.</summary>
    private static List<(AiImage Image, BitmapSource Preview)> LoadImages(string[]? names)
    {
        var result = new List<(AiImage, BitmapSource)>();
        if (names is null) return result;
        foreach (var name in names)
        {
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(Storage.PathOf(ImageDir), Path.GetFileName(name)));
                result.Add((new AiImage("image/jpeg", Convert.ToBase64String(bytes)), Decode(bytes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }
        return result;
    }

    private static void DeleteSavedImages()
    {
        try
        {
            var dir = Storage.PathOf(ImageDir);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            App.Log(ex);
        }
    }
}
