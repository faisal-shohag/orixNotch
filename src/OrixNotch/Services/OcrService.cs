using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace OrixNotch.Services;

/// <summary>
/// "Copy text from screen": opens Windows' snipping overlay, then reads the text in the snip (Windows' own OCR,
/// on this PC) and copies it. The snip arrives on the clipboard, so the next image there within 30 s is the one.
/// </summary>
public sealed class OcrService
{
    public static OcrService Instance { get; } = new();

    /// <summary>Title and detail for the alert: words copied, nothing found, or why it couldn't run.</summary>
    public event Action<string, string>? Done;

    private DateTime _waitUntil;

    /// <summary>A snip is on its way: the screenshot alert stays quiet for it.</summary>
    public bool Waiting => DateTime.Now < _waitUntil;

    public void Start()
    {
        if (OcrEngine.TryCreateFromUserProfileLanguages() is null)
        {
            Done?.Invoke("Can't read text", "Add a language with text recognition in Windows Settings › Language");
            return;
        }
        _waitUntil = DateTime.Now.AddSeconds(30);
        if (!Launcher.Open("ms-screenclip:"))
        {
            _waitUntil = default;
            Done?.Invoke("Can't open the snipping tool", "Windows' Snipping Tool may be missing");
        }
    }

    /// <summary>From the clipboard listener: reads the snip if we're waiting for one.</summary>
    public void OnClipboardUpdate()
    {
        if (!Waiting) return;
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            BitmapSource? image = null;
            for (var attempt = 0; attempt < 5 && image is null; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsImage()) return; // something else was copied: keep waiting
                    image = Clipboard.GetImage();
                }
                catch (COMException)
                {
                    await Task.Delay(80); // the clipboard is still held open
                }
            }
            if (image is null) return;
            _waitUntil = default;
            await ReadAsync(image);
        });
    }

    private async Task ReadAsync(BitmapSource image)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return;
            using var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(png);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png.ToArray());
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            var decoder = await WinBitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            var result = await engine.RecognizeAsync(bitmap);
            var text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
            if (text.Length == 0)
            {
                Done?.Invoke("No text found", "Try a larger area, or text that isn't too small");
                return;
            }
            Clipboard.SetText(text); // lands in clipboard history too
            var words = result.Lines.Sum(l => l.Words.Count);
            Done?.Invoke(words == 1 ? "Copied 1 word" : $"Copied {words} words", text.ReplaceLineEndings(" "));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Done?.Invoke("Couldn't read the text", "Something went wrong reading the snip");
        }
    }
}
