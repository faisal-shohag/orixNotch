using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Screen Time: today's total, the last 7 days and the most used apps (ScreenTimeService).</summary>
public partial class ScreenTimeView : UserControl, IToolView
{
    private static ScreenTimeService Service => ScreenTimeService.Instance;
    private bool _shown;

    public ScreenTimeView()
    {
        InitializeComponent();
        Service.Changed += () =>
        {
            if (_shown) Render();
        };
    }

    public void OnShown()
    {
        _shown = true;
        Render();
    }

    public void OnHidden() => _shown = false;

    private void OnStart(object sender, RoutedEventArgs e) => Service.SetEnabled(true);

    private void Render()
    {
        Intro.Visibility = Service.Enabled ? Visibility.Collapsed : Visibility.Visible;
        Stats.Visibility = Service.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!Service.Enabled) return;

        var today = Service.Day(DateTime.Today);
        var total = today.Sum(x => x.Seconds);
        TotalText.Text = ScreenTimeService.Format(total);
        var yesterday = Service.Total(DateTime.Today.AddDays(-1));
        var diff = total - yesterday;
        CompareText.Text = yesterday == 0 ? "So far today"
            : Math.Abs(diff) < 60 ? "About the same as yesterday"
            : diff > 0 ? $"{ScreenTimeService.Format(diff)} more than yesterday so far" : $"{ScreenTimeService.Format(-diff)} less than yesterday";

        RenderWeek();
        RenderTop(today);
    }

    private void RenderWeek()
    {
        Week.Children.Clear();
        Week.ColumnDefinitions.Clear();
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).ToList();
        var totals = days.Select(Service.Total).ToList();
        var max = Math.Max(1, totals.Max());
        for (var i = 0; i < days.Count; i++)
        {
            Week.ColumnDefinitions.Add(new ColumnDefinition());
            var column = new Grid { Margin = new Thickness(3, 0, 3, 0), ToolTip = $"{days[i]:ddd d MMM} · {ScreenTimeService.Format(totals[i])}" };
            column.RowDefinitions.Add(new RowDefinition());
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Border
            {
                Height = Math.Max(3, 44 * totals[i] / (double)max),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            bar.SetResourceReference(Border.BackgroundProperty, i == days.Count - 1 ? "AccentBrush" : "TrackBrush");
            var letter = new TextBlock
            {
                Text = days[i].ToString("ddd", CultureInfo.CurrentCulture)[..1],
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };
            letter.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            Grid.SetRow(letter, 1);
            column.Children.Add(bar);
            column.Children.Add(letter);
            Grid.SetColumn(column, i);
            Week.Children.Add(column);
        }
    }

    private void RenderTop(List<(string App, int Seconds)> today)
    {
        TopList.Children.Clear();
        EmptyText.Visibility = today.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (today.Count == 0) return;
        var most = today[0].Seconds;
        foreach (var (app, seconds) in today.Take(5))
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });

            var label = Service.DisplayName(app);
            var name = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            // The app's own icon beside its name (its initial on a soft disc when there is none).
            FrameworkElement icon;
            if (Service.IconFor(app) is { } image)
                icon = new Image { Source = image, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            else
            {
                var initial = new TextBlock { Text = label[..1].ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                initial.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                var disc = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Child = initial, VerticalAlignment = VerticalAlignment.Center };
                disc.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                icon = disc;
            }
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            icon.Margin = new Thickness(0, 0, 8, 0);
            var nameCell = new StackPanel { Orientation = Orientation.Horizontal };
            nameCell.Children.Add(icon);
            nameCell.Children.Add(name);

            // The app's glowing bar: its share of the top app's time.
            var track = new GlowBar { BarHeight = 6, Value = seconds / (double)Math.Max(1, most), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(track, 1);

            var time = new TextBlock { Text = ScreenTimeService.Format(seconds), FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            time.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            Grid.SetColumn(time, 2);

            row.Children.Add(nameCell);
            row.Children.Add(track);
            row.Children.Add(time);
            TopList.Children.Add(row);
        }
    }
}
