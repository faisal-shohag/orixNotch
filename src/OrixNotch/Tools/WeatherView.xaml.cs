using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>One forecast day: its condition (icon and colour key) and temperatures.</summary>
public sealed record ForecastDay(string Name, AppIcon Icon, string Tone, double High, double Low);

/// <summary>Weather from Open-Meteo (free, no API key).</summary>
public partial class WeatherView : UserControl, IToolView
{
    private DateTime _lastFetch = DateTime.MinValue;
    private bool _loading;
    private bool _hasData;

    public WeatherView()
    {
        InitializeComponent();
        CityInput.Text = App.Settings.WeatherCity;
        BuildSkeleton();
    }

    /// <summary>Placeholder blocks laid out like the hero card, the four tiles and the 7-day strip.</summary>
    private void BuildSkeleton()
    {
        var current = new Grid { Margin = new Thickness(-3, 5, -3, 2) };
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        current.ColumnDefinitions.Add(new ColumnDefinition());
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hero.Children.Add(Skeleton.Circle(52));
        var heroText = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(Skeleton.Block(70, 34, 8));
        heroText.Children.Add(Skeleton.Block(96, 12, 4, new Thickness(0, 8, 0, 0)));
        heroText.Children.Add(Skeleton.Block(80, 10, 4, new Thickness(0, 6, 0, 0)));
        hero.Children.Add(heroText);
        current.Children.Add(hero);
        var tiles = new UniformGrid { Columns = 2, Rows = 2 };
        for (var i = 0; i < 4; i++)
        {
            var tile = new StackPanel { Margin = new Thickness(13, 10, 13, 8) };
            tile.Children.Add(Skeleton.Block(70, 10, 3));
            tile.Children.Add(Skeleton.Block(double.NaN, 12, 6, new Thickness(0, 12, 0, 0)));
            tiles.Children.Add(tile);
        }
        Grid.SetColumn(tiles, 1);
        current.Children.Add(tiles);
        LoadingSkeleton.Children.Add(current);

        var days = new UniformGrid { Rows = 1 };
        for (var i = 0; i < 7; i++)
        {
            var day = new StackPanel { Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center };
            day.Children.Add(Skeleton.Block(30, 9, 3, new Thickness(0, 7, 0, 0), HorizontalAlignment.Center));
            day.Children.Add(Skeleton.Circle(20, new Thickness(0, 6, 0, 6), HorizontalAlignment.Center));
            day.Children.Add(Skeleton.Block(70, 8, 4, new Thickness(0, 0, 0, 7), HorizontalAlignment.Center));
            days.Children.Add(day);
        }
        Grid.SetRow(days, 1);
        LoadingSkeleton.Children.Add(days);
    }

    /// <summary>Swaps the content for the skeleton (or back). Forecast stays hidden until data exists.</summary>
    private void SetLoading(bool loading)
    {
        LoadingSkeleton.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        CurrentPanel.Visibility = !loading && _hasData ? Visibility.Visible : Visibility.Hidden;
        Forecast.Visibility = !loading && _hasData ? Visibility.Visible : Visibility.Hidden;
    }

    private (string City, bool Fahrenheit) _fetchedFor;

    public void OnShown()
    {
        // The city or °C/°F changed in Settings: refetch now, not when the 15 minutes are up.
        var wanted = (App.Settings.WeatherCity, App.Settings.UseFahrenheit);
        if (wanted != _fetchedFor && !CityInput.IsKeyboardFocused) CityInput.Text = wanted.WeatherCity;
        if (wanted != _fetchedFor) _ = LoadAsync(newPlace: wanted.WeatherCity != _fetchedFor.City);
        else if (DateTime.Now - _lastFetch > TimeSpan.FromMinutes(15)) _ = LoadAsync();
    }

    public void OnHidden()
    {
    }

    private void OnCityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var city = CityInput.Text.Trim();
        if (city.Length == 0) return;
        _ = LoadAsync(newPlace: true, city); // saved once it's found: a typo mustn't replace a working city
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>Skeleton on first load or a new city; a plain refresh keeps the current data on screen.</summary>
    private async Task LoadAsync(bool newPlace = false, string? typed = null)
    {
        if (_loading) return;
        _loading = true;
        var skeleton = newPlace || !_hasData;
        if (skeleton) SetLoading(true);
        Status.Text = skeleton ? "Loading…" : "Updating…";
        try
        {
            var city = typed ?? App.Settings.WeatherCity;
            if (await OpenMeteo.FindAsync(city) is not { } place)
            {
                Status.Text = $"City \"{city}\" not found";
                Field.ShowError(CityInput);
                return;
            }
            var (name, country, lat, lon) = (place.Name, place.Country, place.Lat, place.Lon);

            var unit = App.Settings.UseFahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset&timezone=auto&forecast_days=7{unit}");
            using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var windUnit = App.Settings.UseFahrenheit ? "mph" : "km/h";

            var code = cur.GetProperty("weather_code").GetInt32();
            var isDay = cur.GetProperty("is_day").GetInt32() == 1;
            var now = Describe(code, isDay);
            CurrentIcon.Kind = now.Icon;
            CurrentIcon.SetResourceReference(ForegroundProperty, now.Tone);
            CurrentHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, now.Tone);
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var feels = cur.GetProperty("apparent_temperature").GetDouble();
            TempText.Text = Deg(temp);
            DescText.Text = now.Text;
            PlaceText.Text = country is null ? name : $"{name}, {country}";
            PlaceText.ToolTip = PlaceText.Text;

            var humidity = cur.GetProperty("relative_humidity_2m").GetDouble();
            HumidityBar.Icon = AppIcon.Drop;
            HumidityBar.HeadText = $"{humidity:0}%";
            HumidityBar.Value = humidity / 100;
            HumidityBar.EndText = humidity >= 70 ? "Humid" : humidity <= 30 ? "Dry" : "Comfortable";

            var wind = cur.GetProperty("wind_speed_10m").GetDouble();
            WindText.Text = $"{wind:0} {windUnit}";
            WindSub.Text = WindWord(App.Settings.UseFahrenheit ? wind * 1.609 : wind);
            FeelsText.Text = Deg(feels);
            FeelsSub.Text = Math.Abs(feels - temp) < 1.5 ? "Same as actual" : feels > temp ? "Warmer than actual" : "Cooler than actual";

            // Daylight: how far through the day it is where the city is (the API's times are the city's own clock).
            var offset = TimeSpan.FromSeconds(root.TryGetProperty("utc_offset_seconds", out var off) ? off.GetInt32() : 0);
            var cityNow = DateTime.UtcNow + offset;
            if (Stamp(daily.GetProperty("sunrise")[0]) is { } sunrise && Stamp(daily.GetProperty("sunset")[0]) is { } sunset && sunset > sunrise)
            {
                SunTimes.Text = $"{sunrise:t} – {sunset:t}";
                var left = sunset - cityNow;
                (DaylightBar.Value, DaylightBar.Icon, DaylightBar.HeadText, DaylightBar.Tone) =
                    cityNow < sunrise ? (0.0, AppIcon.Sunrise, $"{sunrise:t}", "StarBrush")
                    : cityNow > sunset ? (1.0, AppIcon.MoonStars, "Night", "WeatherNightBrush")
                    : ((cityNow - sunrise) / (sunset - sunrise), AppIcon.WeatherSun,
                        left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, left.Minutes)}m", "StarBrush");
                DaylightBar.EndText = cityNow >= sunrise && cityNow <= sunset ? "left" : "";
            }

            var days = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var max = daily.GetProperty("temperature_2m_max");
            var min = daily.GetProperty("temperature_2m_min");
            var forecast = new List<ForecastDay>();
            for (var i = 0; i < days.GetArrayLength(); i++)
            {
                var date = DateTime.Parse(days[i].GetString()!, CultureInfo.InvariantCulture);
                var look = Describe(codes[i].GetInt32(), true);
                forecast.Add(new ForecastDay(i == 0 ? "Today" : date.ToString("ddd", CultureInfo.CurrentCulture),
                    look.Icon, look.Tone, max[i].GetDouble(), min[i].GetDouble()));
            }
            RenderForecast(forecast);

            _hasData = true;
            if (city != App.Settings.WeatherCity)
            {
                App.Settings.WeatherCity = city;
                SettingsService.Save(notify: false);
                IdleInfoService.Instance.Reconfigure(); // the idle notch's weather follows
            }
            _fetchedFor = (city, App.Settings.UseFahrenheit);
            _lastFetch = DateTime.Now;
            Status.Text = $"Updated {DateTime.Now:t}";
        }
        catch (HttpRequestException)
        {
            Status.Text = "Offline — could not reach Open-Meteo";
        }
        catch (TaskCanceledException)
        {
            Status.Text = "Request timed out";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Status.Text = "Could not load weather";
        }
        finally
        {
            _loading = false;
            // On failure the last good data (if any) comes back; the status line explains why.
            SetLoading(false);
        }
    }

    private static string Deg(double value) => $"{Math.Round(value) + 0.0:0}°"; // + 0.0 turns -0 (from -0.4) into 0: no "-0°"

    /// <summary>A date-time from the API ("2026-11-20T06:05", the city's own clock); null if missing.</summary>
    private static DateTime? Stamp(JsonElement value) =>
        DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    /// <summary>Beaufort-style words for a wind speed in km/h.</summary>
    private static string WindWord(double kmh) => kmh switch
    {
        < 2 => "Calm",
        < 12 => "Light air",
        < 29 => "Breeze",
        < 50 => "Strong wind",
        < 75 => "Gale",
        _ => "Storm",
    };

    /// <summary>
    /// The week: each day's name, its condition icon in colour, and its low → high as a bar placed within the whole week's
    /// range (a cool-to-warm fill with a glow at the high end), so warm and cold days read at a glance.
    /// </summary>
    private void RenderForecast(List<ForecastDay> days)
    {
        Forecast.Children.Clear();
        if (days.Count == 0) return;
        var low = days.Min(d => d.Low);
        var high = days.Max(d => d.High);
        var span = Math.Max(1, high - low);
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var cell = new Border { Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(6, 6, 6, 7), CornerRadius = Radii.M };
            if (i == 0) cell.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            var stack = new StackPanel();
            var head = new Grid();
            var label = new TextBlock { Text = d.Name.ToUpperInvariant(), Style = (Style)FindResource("Caps"), VerticalAlignment = VerticalAlignment.Center };
            if (i == 0) label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var icon = new HeroIcon { Kind = d.Icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right };
            icon.SetResourceReference(ForegroundProperty, d.Tone);
            head.Children.Add(label);
            head.Children.Add(icon);
            stack.Children.Add(head);

            var temps = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            temps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            temps.ColumnDefinitions.Add(new ColumnDefinition());
            temps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lowText = new TextBlock { Text = Deg(d.Low), FontSize = 12, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
            lowText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var highText = new TextBlock { Text = Deg(d.High), FontSize = 12, FontWeight = FontWeights.SemiBold, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
            highText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var bar = RangeBar((d.Low - low) / span, (d.High - low) / span);
            bar.Margin = new Thickness(5, 0, 5, 0);
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(highText, 2);
            temps.Children.Add(lowText);
            temps.Children.Add(bar);
            temps.Children.Add(highText);
            stack.Children.Add(temps);
            cell.Child = stack;
            Forecast.Children.Add(cell);
        }
    }

    /// <summary>A thin track with the day's low → high filled, cool to warm, glowing at the high end.</summary>
    private static FrameworkElement RangeBar(double from, double to)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(Math.Max(to, from + 0.06), 0, 1); // a steady day still shows a sliver
        var grid = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(from, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(to - from, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - to, GridUnitType.Star) });
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        Grid.SetColumnSpan(track, 3);
        grid.Children.Add(track);
        var cold = (ThemeService.Get("WeatherColdBrush") as SolidColorBrush)?.Color ?? Colors.SkyBlue;
        var warm = (ThemeService.Get("WeatherWarmBrush") as SolidColorBrush)?.Color ?? Colors.Orange;
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Frozen(new LinearGradientBrush(cold, warm, 0)) };
        Grid.SetColumn(fill, 1);
        grid.Children.Add(fill);
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -2, -5, -2),
            Fill = Frozen(new RadialGradientBrush(Color.FromArgb(150, warm.R, warm.G, warm.B), Color.FromArgb(0, warm.R, warm.G, warm.B))),
            IsHitTestVisible = false,
        };
        Grid.SetColumn(glow, 1);
        grid.Children.Add(glow);
        return grid;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>WMO weather interpretation codes → a drawn icon, its colour (resource key) and words.</summary>
    private static (AppIcon Icon, string Tone, string Text) Describe(int code, bool day) => code switch
    {
        0 => day ? (AppIcon.WeatherSun, "StarBrush", "Clear") : (AppIcon.MoonStars, "WeatherNightBrush", "Clear"),
        1 => day ? (AppIcon.CloudSun, "StarBrush", "Mainly clear") : (AppIcon.CloudMoon, "WeatherNightBrush", "Mainly clear"),
        2 => day ? (AppIcon.CloudSun, "StarBrush", "Partly cloudy") : (AppIcon.CloudMoon, "WeatherNightBrush", "Partly cloudy"),
        3 => (AppIcon.Cloud, "SubTextBrush", "Overcast"),
        45 or 48 => (AppIcon.CloudFog, "SubTextBrush", "Fog"),
        >= 51 and <= 57 => (AppIcon.CloudRain, "WeatherRainBrush", "Drizzle"),
        >= 61 and <= 67 => (AppIcon.CloudRain, "WeatherRainBrush", "Rain"),
        >= 71 and <= 77 => (AppIcon.CloudSnow, "WeatherSnowBrush", "Snow"),
        >= 80 and <= 82 => (AppIcon.CloudRain, "WeatherRainBrush", "Rain showers"),
        85 or 86 => (AppIcon.CloudSnow, "WeatherSnowBrush", "Snow showers"),
        >= 95 => (AppIcon.CloudStorm, "WeatherStormBrush", "Thunderstorm"),
        _ => (AppIcon.Thermometer, "SubTextBrush", "Unknown"),
    };
}
