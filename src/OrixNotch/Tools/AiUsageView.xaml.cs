using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Per-tool cards: 5-hour session, weekly total, today, and a 14-day trend.</summary>
public partial class AiUsageView : UserControl, IToolView
{
    private DateTime _lastLoad = DateTime.MinValue;
    private bool _loading;

    public AiUsageView()
    {
        InitializeComponent();
        Strip.PreviewMouseWheel += (_, e) =>
        {
            Strip.ScrollToHorizontalOffset(Strip.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
    }

    public void OnShown()
    {
        if (DateTime.Now - _lastLoad > TimeSpan.FromSeconds(60)) _ = LoadAsync();
    }

    public void OnHidden()
    {
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        Status.Text = "Reading logs…";
        try
        {
            var providers = await AiUsageService.LoadAsync();
            _lastLoad = DateTime.Now;
            TodayCost.Text = Money(providers.Where(p => p.HasCost).Sum(p => p.TodayCost));
            MonthCost.Text = Money(providers.Where(p => p.HasCost).Sum(p => p.MonthCost));
            Cards.Children.Clear();
            foreach (var p in providers) Cards.Children.Add(Card(p));
            Empty.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Status.Text = $"Estimated · {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Status.Text = "Could not read logs";
        }
        finally
        {
            _loading = false;
        }
    }

    private FrameworkElement Card(ProviderUsage p)
    {
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new HeroIcon { Kind = p.Name.StartsWith("Claude") ? AppIcon.Sparkles : AppIcon.Terminal, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        var badgeText = new TextBlock { Text = "Local", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        badge.Child = badgeText;
        head.Children.Add(badge);
        stack.Children.Add(head);

        var isClaude = p.Name.StartsWith("Claude");
        var sessionLimit = isClaude ? App.Settings.ClaudeSessionTokenLimit : 0;
        var weeklyLimit = isClaude ? App.Settings.ClaudeWeeklyTokenLimit : 0;

        var spct = Pct(p.SessionTokens, sessionLimit);
        var resets = p.SessionStarted is { } start ? $"Resets {start.AddHours(5):t}" : "No active session";
        var spare = sessionLimit > 0 ? $"~{Math.Max(0, 100 - spct):0}% left at reset" : Tokens(p.SessionTokens);
        stack.Children.Add(Meter("Session", p.SessionTokens, spct, sessionLimit > 0, resets, spare));

        var wpct = Pct(p.WeeklyTokens, weeklyLimit);
        var wspare = weeklyLimit > 0 ? $"~{Math.Max(0, 100 - wpct):0}% spare" : Tokens(p.WeeklyTokens);
        stack.Children.Add(Meter("Weekly", p.WeeklyTokens, wpct, weeklyLimit > 0, $"Today {Tokens(p.TodayTokens)}", wspare));

        // Third row: top session model (Claude) or the usage trend.
        var top = p.SessionModels.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (top.Value > 0 && p.SessionTokens > 0)
        {
            var mpct = 100.0 * top.Value / p.SessionTokens;
            stack.Children.Add(Meter(top.Key, top.Value, mpct, true, $"{Tokens(top.Value)} this session", $"{mpct:0}% of session", plainBar: true));
        }
        else
        {
            var trendHead = new Grid { Margin = new Thickness(0, 5, 0, 4) };
            trendHead.Children.Add(new TextBlock { Text = "Usage trend", FontSize = 12.5, FontWeight = FontWeights.Medium });
            trendHead.Children.Add(new TextBlock { Text = "14 days", Style = (Style)FindResource("SubText"), FontSize = 10.5, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Right });
            stack.Children.Add(trendHead);
            stack.Children.Add(Trend(p.Daily));
        }

        var card = new Border { Width = 236, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return card;
    }

    private static double Pct(long tokens, long limit) =>
        limit > 0 ? Math.Min(100, 100.0 * tokens / limit) : 0;

    /// <summary>"Label .... 42% used" + bar with a tick + left/right captions.</summary>
    private FrameworkElement Meter(string label, long tokens, double pct, bool hasLimit, string left, string right, bool plainBar = false)
    {
        pct = Math.Clamp(pct, 0, 100);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelText = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        top.Children.Add(labelText);
        top.Children.Add(new TextBlock
        {
            Text = hasLimit ? $"{pct:0}% used" : Tokens(tokens),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Grid.SetColumn(top.Children[1], 1);
        panel.Children.Add(top);
        panel.Children.Add(Bar(pct, plainBar ? "TextBrush" : BrushFor(pct)));

        var captions = new Grid();
        captions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        captions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        captions.Children.Add(new TextBlock { Text = left, Style = (Style)FindResource("SubText"), FontSize = 10.5, FontWeight = FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        var rightText = new TextBlock { Text = right, Style = (Style)FindResource("SubText"), FontSize = 10.5, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(rightText, 1);
        captions.Children.Add(rightText);
        panel.Children.Add(captions);
        return panel;
    }

    private static string BrushFor(double pct) => pct > 85 ? "BadBrush" : pct > 60 ? "AccentBrush" : "TextBrush";

    /// <summary>Rounded track with a proportional fill and a light tick at the value.</summary>
    private static FrameworkElement Bar(double pct, string fillBrush)
    {
        var grid = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.01), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.01), GridUnitType.Star) });

        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);

        if (pct > 0.5)
        {
            var fill = new Border { CornerRadius = new CornerRadius(3, 0, 0, 3) };
            fill.SetResourceReference(Border.BackgroundProperty, fillBrush);
            grid.Children.Add(fill);
            if (pct < 99.5)
            {
                var tick = new Border { Width = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -1, 0) };
                tick.SetResourceReference(Border.BackgroundProperty, "TextBrush");
                grid.Children.Add(tick);
            }
        }
        return grid;
    }

    private static FrameworkElement Trend(long[] daily)
    {
        var max = Math.Max(1, daily.Max());
        var grid = new UniformGrid { Rows = 1, Height = 22 };
        for (var i = 0; i < daily.Length; i++)
        {
            var bar = new Border
            {
                Height = Math.Max(2, 22.0 * daily[i] / max),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(1, 0, 1, 0),
                Opacity = i == daily.Length - 1 ? 1 : 0.55,
            };
            bar.SetResourceReference(Border.BackgroundProperty, i == daily.Length - 1 ? "AccentBrush" : "SubTextBrush");
            grid.Children.Add(bar);
        }
        return grid;
    }

    private static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#}B tokens",
        >= 1_000_000 => $"{n / 1e6:0.#}M tokens",
        >= 1_000 => $"{n / 1e3:0.#}K tokens",
        _ => $"{n} tokens",
    };

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
