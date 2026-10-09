using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

public sealed class StockQuote
{
    private static readonly Brush Neutral = Frozen(Color.FromRgb(0x8E, 0x8E, 0x93));

    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string PriceText { get; init; } = "–";
    public string ChangeText { get; init; } = "…";
    public Brush Tint { get; init; } = Neutral;
    public PointCollection Points { get; init; } = new();

    public static StockQuote Placeholder(string symbol, string name = "Loading…") =>
        new() { Symbol = symbol, Name = name };

    public static StockQuote Create(string symbol, string name, double price, double previous, string currency, IReadOnlyList<double> closes)
    {
        var change = price - previous;
        var pct = previous != 0 ? change / previous * 100 : 0;
        return new StockQuote
        {
            Symbol = symbol,
            Name = name,
            PriceText = price.ToString(price >= 1000 ? "N0" : "N2", CultureInfo.CurrentCulture) + (currency == "USD" ? "" : $" {currency}"),
            ChangeText = $"{(change >= 0 ? "+" : "")}{pct:0.00}%",
            // Theme brushes so the pill keeps contrast on light schemes; picked up again on the next refresh.
            Tint = ThemeService.Get(change > 0 ? "GoodBrush" : change < 0 ? "BadBrush" : "SubTextBrush"),
            Points = Sparkline(closes, previous, 120, 24),
        };
    }

    private static PointCollection Sparkline(IReadOnlyList<double> values, double baseline, double w, double h)
    {
        var points = new PointCollection();
        if (values.Count < 2) return points;
        var min = Math.Min(values.Min(), baseline);
        var max = Math.Max(values.Max(), baseline);
        var range = Math.Max(1e-9, max - min);
        for (var i = 0; i < values.Count; i++)
            points.Add(new Point(w * i / (values.Count - 1), h - (values[i] - min) / range * h));
        points.Freeze();
        return points;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Watchlist backed by Yahoo Finance's public chart endpoint.</summary>
public partial class StocksView : UserControl, IToolView
{
    private readonly ObservableCollection<StockQuote> _quotes = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private DateTime _lastFetch = DateTime.MinValue;

    public StocksView()
    {
        InitializeComponent();
        List.ItemsSource = _quotes;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        foreach (var t in App.Settings.StockTickers) _quotes.Add(StockQuote.Placeholder(t));
    }

    public void OnShown()
    {
        if (DateTime.Now - _lastFetch > TimeSpan.FromSeconds(30)) _ = RefreshAsync();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void OnTickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add();
    }

    private void OnAdd(object sender, RoutedEventArgs e) => Add();

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void Add()
    {
        var symbol = TickerInput.Text.Trim().ToUpperInvariant();
        if (symbol.Length == 0) return;
        if (App.Settings.StockTickers.Contains(symbol))
        {
            Field.ShowError(TickerInput);
            Status.Text = $"{symbol} is already in the list";
            return;
        }
        App.Settings.StockTickers.Add(symbol);
        SettingsService.Save(notify: false);
        TickerInput.Clear();
        var placeholder = StockQuote.Placeholder(symbol);
        _quotes.Add(placeholder);
        _ = LoadOneAsync(placeholder);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StockQuote q) return;
        App.Settings.StockTickers.Remove(q.Symbol);
        SettingsService.Save(notify: false);
        _quotes.Remove(q);
    }

    private async Task RefreshAsync()
    {
        Status.Text = "Updating…";
        await Task.WhenAll(_quotes.ToList().Select(LoadOneAsync));
        _lastFetch = DateTime.Now;
        Status.Text = $"Updated {DateTime.Now:t}";
    }

    private async Task LoadOneAsync(StockQuote current)
    {
        StockQuote next;
        try
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(current.Symbol)}?range=1d&interval=5m";
            using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
            var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta = result.GetProperty("meta");
            var price = meta.GetProperty("regularMarketPrice").GetDouble();
            var previous = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble()
                : meta.TryGetProperty("previousClose", out var p2) ? p2.GetDouble() : price;
            var currency = meta.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "USD" : "USD";
            var name = meta.TryGetProperty("shortName", out var sn) ? sn.GetString() ?? current.Symbol
                : meta.TryGetProperty("longName", out var ln) ? ln.GetString() ?? current.Symbol : current.Symbol;

            var closes = new List<double>();
            if (result.TryGetProperty("indicators", out var ind) &&
                ind.GetProperty("quote")[0].TryGetProperty("close", out var closeArr))
            {
                foreach (var v in closeArr.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.Number) closes.Add(v.GetDouble());
            }

            next = StockQuote.Create(current.Symbol, name, price, previous, currency, closes);
        }
        catch (Exception)
        {
            next = StockQuote.Placeholder(current.Symbol, "Unavailable");
        }

        var index = _quotes.IndexOf(current);
        if (index < 0) index = _quotes.ToList().FindIndex(q => q.Symbol == current.Symbol);
        if (index >= 0) _quotes[index] = next;
    }
}
