using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Per-tool cards: 5-hour session, weekly total, today, and a 14-day trend.</summary>
public partial class AiUsageView : UserControl, IToolView
{
    private DateTime _lastLoad = DateTime.MinValue;
    private bool _loading;
    private bool _hasData;
    private readonly Border _todaySkeleton = Skeleton.Block(44, 13, 4, new Thickness(5, 0, 14, 0));
    private readonly Border _monthSkeleton = Skeleton.Block(64, 13, 4, new Thickness(5, 0, 0, 0));

    public AiUsageView()
    {
        InitializeComponent();
        Totals.Children.Insert(Totals.Children.IndexOf(TodayCost) + 1, _todaySkeleton);
        Totals.Children.Insert(Totals.Children.IndexOf(MonthCost) + 1, _monthSkeleton);
        ThemeService.Changed += UpdateEdgeFades;
        UpdateEdgeFades();
    }

    private void OnStripScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != Strip) return;
        FadeTo(EdgeLeft, Strip.HorizontalOffset > 1);
        FadeTo(EdgeRight, Strip.HorizontalOffset < Strip.ScrollableWidth - 1);
    }

    private static void FadeTo(FrameworkElement element, bool visible)
    {
        if (element.Tag is bool shown && shown == visible) return;
        element.Tag = visible;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1.0 : 0.0, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Edge fades go from the notch color to transparent, so cards dissolve into the edge (same as Clipboard).</summary>
    private void UpdateEdgeFades()
    {
        var pill = ((SolidColorBrush)ThemeService.Get("PillBrush")).Color;
        var clear = Color.FromArgb(0, pill.R, pill.G, pill.B);
        var left = new LinearGradientBrush(pill, clear, 0);
        var right = new LinearGradientBrush(clear, pill, 0);
        left.Freeze();
        right.Freeze();
        EdgeLeftFade.Fill = left;
        EdgeRightFade.Fill = right;
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
        // Skeleton until the first load lands; later refreshes keep the cards on screen.
        if (!_hasData) ShowSkeleton(true);
        Status.Text = "Reading logs…";
        try
        {
            var providers = await AiUsageService.LoadAsync();
            _lastLoad = DateTime.Now;
            _hasData = true;
            ShowSkeleton(false);
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
            if (!_hasData)
            {
                ShowSkeleton(false);
                Cards.Children.Clear();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowSkeleton(bool on)
    {
        TodayCost.Visibility = MonthCost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _todaySkeleton.Visibility = _monthSkeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        Empty.Visibility = Visibility.Collapsed;
        Cards.Children.Clear();
        for (var i = 0; i < 2; i++) Cards.Children.Add(SkeletonCard());
    }

    /// <summary>Placeholder shaped like <see cref="Card"/>: header, then three meter rows.</summary>
    private static FrameworkElement SkeletonCard()
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 9) };
        head.Children.Add(Skeleton.Circle(15));
        head.Children.Add(Skeleton.Block(96, 14, 4, new Thickness(7, 0, 7, 0)));
        head.Children.Add(Skeleton.Block(34, 16, 5));
        stack.Children.Add(head);

        for (var i = 0; i < 3; i++)
        {
            var top = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : 2, 0, 0) };
            var right = Skeleton.Block(52, 12, 4, default, HorizontalAlignment.Right);
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            top.Children.Add(Skeleton.Block(i == 2 ? 44 : 58, 12));
            stack.Children.Add(top);
            stack.Children.Add(Skeleton.Block(double.NaN, 6, 3, new Thickness(0, 6, 0, 5)));

            var captions = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var capRight = Skeleton.Block(40, 9, 3, default, HorizontalAlignment.Right);
            DockPanel.SetDock(capRight, Dock.Right);
            captions.Children.Add(capRight);
            captions.Children.Add(Skeleton.Block(i == 2 ? 104 : 78, 9, 3));
            stack.Children.Add(captions);
        }

        var card = new Border { Width = 236, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return card;
    }

    /// <summary>Logo, name and a small badge ("Plan", "Local", "Pro"…).</summary>
    private static FrameworkElement CardHeader(ProviderUsage p)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(BrandLogos.Create(p.Logo, 15));
        head.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        var badgeText = new TextBlock
        {
            Text = p.Badge ?? (p.SessionPercent is not null || p.WeeklyPercent is not null ? "Plan" : "Local"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
        };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        badge.Child = badgeText;
        head.Children.Add(badge);
        return head;
    }

    private static Border CardFrame(FrameworkElement content)
    {
        var card = new Border { Width = 236, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return card;
    }

    /// <summary>Cursor / Antigravity / DeepSeek / Perplexity: headline, meters, note and link from the loader.</summary>
    private FrameworkElement GenericCard(ProviderUsage p)
    {
        var stack = new StackPanel();
        stack.Children.Add(CardHeader(p));
        if (p.Headline is not null)
        {
            stack.Children.Add(new TextBlock { Text = p.Headline, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
            if (p.HeadlineSub is not null)
                stack.Children.Add(new TextBlock { Text = p.HeadlineSub, Style = (Style)FindResource("SubText"), FontSize = 10.5, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        }
        foreach (var m in p.Meters)
            stack.Children.Add(Meter(m.Label, 0, m.Percent ?? 0, m.Percent is not null, m.Left, m.Right, valueText: m.Value));
        if (p.Note is not null)
            stack.Children.Add(new TextBlock { Text = p.Note, Style = (Style)FindResource("SubText"), FontSize = 11.5, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        if (p.LinkUrl is not null)
        {
            var link = new Button { Content = p.LinkText ?? "Open", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            var url = p.LinkUrl;
            link.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            stack.Children.Add(link);
        }
        return CardFrame(stack);
    }

    private FrameworkElement Card(ProviderUsage p)
    {
        if (p.IsGeneric) return GenericCard(p);
        var stack = new StackPanel();
        stack.Children.Add(CardHeader(p));

        // Prefer the provider's own plan percentages; fall back to the manual token limits in Settings.
        var isClaude = p.Name.StartsWith("Claude");
        var sessionLimit = isClaude ? App.Settings.ClaudeSessionTokenLimit : 0;
        var weeklyLimit = isClaude ? App.Settings.ClaudeWeeklyTokenLimit : 0;

        var sessionHas = p.SessionPercent is not null || sessionLimit > 0;
        var spct = p.SessionPercent ?? Pct(p.SessionTokens, sessionLimit);
        var resets = p.SessionResets is { } sr ? $"Resets {sr:t}"
            : p.SessionStarted is { } start ? $"Resets {start.AddHours(5):t}" : "No active session";
        var spare = sessionHas ? $"{Math.Max(0, 100 - spct):0}% left" : Tokens(p.SessionTokens);
        stack.Children.Add(Meter("Session", p.SessionTokens, spct, sessionHas, resets, spare));

        var weeklyHas = p.WeeklyPercent is not null || weeklyLimit > 0;
        var wpct = p.WeeklyPercent ?? Pct(p.WeeklyTokens, weeklyLimit);
        var wleft = p.WeeklyResets is { } wr ? $"Resets {wr:ddd h:mm tt}" : $"Today {Tokens(p.TodayTokens)}";
        var wspare = weeklyHas ? $"{Math.Max(0, 100 - wpct):0}% left" : Tokens(p.WeeklyTokens);
        stack.Children.Add(Meter("Weekly", p.WeeklyTokens, wpct, weeklyHas, wleft, wspare));

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

        return CardFrame(stack);
    }

    private static double Pct(long tokens, long limit) =>
        limit > 0 ? Math.Min(100, 100.0 * tokens / limit) : 0;

    /// <summary>"Label .... 42% used" + bar with a tick + left/right captions.</summary>
    private FrameworkElement Meter(string label, long tokens, double pct, bool hasLimit, string left, string right, bool plainBar = false, string? valueText = null)
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
            Text = valueText ?? (hasLimit ? $"{pct:0}% used" : Tokens(tokens)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Grid.SetColumn(top.Children[1], 1);
        panel.Children.Add(top);
        if (hasLimit || valueText is null) panel.Children.Add(Bar(pct, plainBar ? "AccentBrush" : BrushFor(pct)));
        else panel.Children.Add(new Border { Height = 4 });

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

    private static string BrushFor(double pct) => pct > 85 ? "BadBrush" : "AccentBrush";

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
