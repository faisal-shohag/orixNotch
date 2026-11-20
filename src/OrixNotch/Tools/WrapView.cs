using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// The end-of-day wrap, opened from the evening Screen Time alert: screen time, top apps, focus sessions and
/// to-dos done, with "Save image" for a PNG to share.
/// </summary>
public sealed class WrapView : UserControl
{
    private readonly UniformGrid _cards = new() { Rows = 1, Columns = 4 };
    private readonly Border _shot; // what "Save image" renders: the cards on the notch's own background

    public WrapView()
    {
        _shot = new Border { Child = _cards, Padding = new Thickness(0, 4, 0, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 5, 0) };
        buttons.Children.Add(Button("Details", () => NotchWindow.Instance?.OpenTool("screentime")));
        var save = Button("Save image", SaveImage);
        save.Style = (Style)FindResource("AccentButton");
        save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(save);
        var root = new StackPanel { Margin = new Thickness(-5, 0, -5, 0) };
        root.Children.Add(_shot);
        root.Children.Add(buttons);
        Content = root;
    }

    private static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Height = 30, Padding = new Thickness(14, 0, 14, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    public void Show()
    {
        var st = ScreenTimeService.Instance;
        var day = st.Day(DateTime.Today);
        var total = day.Sum(x => x.Seconds);
        var top = day.Take(3).Select(x => st.DisplayName(x.App)).ToList();
        var focus = PomodoroService.Instance.Completed;
        var done = TodosView.DoneToday();

        _cards.Children.Clear();
        _cards.Children.Add(MorningBriefView.Card(Glyph(AppIcon.Hourglass), "Screen time", total > 0 ? ScreenTimeService.Format(total) : "Not tracked",
            total > 0 ? "TextBrush" : "SubTextBrush", total > 0 ? null : "Turn it on in Screen Time"));
        _cards.Children.Add(MorningBriefView.Card(Glyph(AppIcon.Monitor), "Most used", top.Count > 0 ? top[0] : "—",
            sub: top.Count > 1 ? string.Join(", ", top.Skip(1)) : null));
        _cards.Children.Add(MorningBriefView.Card(Glyph(AppIcon.Fire), "Focus", focus == 1 ? "1 session" : $"{focus} sessions",
            focus > 0 ? "AccentBrush" : "TextBrush", focus > 0 ? $"About {focus * App.Settings.PomodoroWorkMin} min" : null));
        _cards.Children.Add(MorningBriefView.Card(Glyph(AppIcon.Check), "To-dos done", done.ToString(), done > 0 ? "GoodBrush" : "TextBrush"));
    }

    private static FrameworkElement Glyph(AppIcon icon)
    {
        var glyph = new HeroIcon { Kind = icon, Width = 13, Height = 13 };
        glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
        return glyph;
    }

    /// <summary>The cards at 2× as a PNG in Pictures\OrixNotch, shown in Explorer.</summary>
    private void SaveImage()
    {
        try
        {
            const int Pad = 24;
            var w = _shot.ActualWidth + Pad * 2;
            var h = _shot.ActualHeight + Pad * 2 + 30;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle((Brush)FindResource("SurfaceBrush"), null, new Rect(0, 0, w, h), 20, 20);
                var title = new FormattedText($"My day · {DateTime.Today:ddd, MMM d}", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 15,
                    (Brush)FindResource("TextBrush"), 2);
                dc.DrawText(title, new Point(Pad + 5, Pad - 4));
                var mark = new FormattedText("OrixNotch", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, (Brush)FindResource("SubTextBrush"), 2);
                dc.DrawText(mark, new Point(w - Pad - 5 - mark.Width, Pad));
                dc.DrawRectangle(new VisualBrush(_shot), null, new Rect(Pad, Pad + 26, _shot.ActualWidth, _shot.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OrixNotch");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"my-day-{DateTime.Today:yyyy-MM-dd}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            App.Log(ex);
        }
    }
}
