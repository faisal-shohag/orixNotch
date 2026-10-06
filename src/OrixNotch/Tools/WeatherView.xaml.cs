using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

public sealed record ForecastDay(string Day, string Icon, string High, string Low);

/// <summary>Weather from Open-Meteo (free, no API key).</summary>
public partial class WeatherView : UserControl, IToolView
{
    private DateTime _lastFetch = DateTime.MinValue;
    private bool _loading;

    public WeatherView()
    {
        InitializeComponent();
        CityInput.Text = App.Settings.WeatherCity;
    }

    public void OnShown()
    {
        if (DateTime.Now - _lastFetch > TimeSpan.FromMinutes(15)) _ = LoadAsync();
    }

    public void OnHidden()
    {
    }

    private void OnCityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var city = CityInput.Text.Trim();
        if (city.Length == 0) return;
        App.Settings.WeatherCity = city;
        SettingsService.Save(notify: false);
        _ = LoadAsync();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        Status.Text = "Loading…";
        try
        {
            var city = App.Settings.WeatherCity;
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name={Uri.EscapeDataString(city)}";
            using var geo = JsonDocument.Parse(await Http.Client.GetStringAsync(geoUrl));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                Status.Text = $"City \"{city}\" not found";
                return;
            }

            var place = results[0];
            var lat = place.GetProperty("latitude").GetDouble();
            var lon = place.GetProperty("longitude").GetDouble();
            var name = place.GetProperty("name").GetString();
            var country = place.TryGetProperty("country", out var c) ? c.GetString() : null;

            var unit = App.Settings.UseFahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=7{unit}");
            using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var windUnit = App.Settings.UseFahrenheit ? "mph" : "km/h";

            var code = cur.GetProperty("weather_code").GetInt32();
            var isDay = cur.GetProperty("is_day").GetInt32() == 1;
            var (icon, desc) = Describe(code, isDay);

            PlaceText.Text = country is null ? name : $"{name}, {country}";
            CurrentIcon.Text = icon;
            TempText.Text = Deg(cur.GetProperty("temperature_2m").GetDouble());
            DescText.Text = desc;
            FeelsText.Text = $"Feels {Deg(cur.GetProperty("apparent_temperature").GetDouble())}";
            HumidityText.Text = $"{cur.GetProperty("relative_humidity_2m").GetDouble():0}%";
            WindText.Text = $"{cur.GetProperty("wind_speed_10m").GetDouble():0} {windUnit}";

            var days = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var max = daily.GetProperty("temperature_2m_max");
            var min = daily.GetProperty("temperature_2m_min");
            RangeText.Text = $"{Deg(max[0].GetDouble())} / {Deg(min[0].GetDouble())}";

            var forecast = new List<ForecastDay>();
            for (var i = 0; i < days.GetArrayLength(); i++)
            {
                var date = DateTime.Parse(days[i].GetString()!, CultureInfo.InvariantCulture);
                forecast.Add(new ForecastDay(
                    i == 0 ? "TODAY" : date.ToString("ddd").ToUpperInvariant(),
                    Describe(codes[i].GetInt32(), true).Icon,
                    Deg(max[i].GetDouble()),
                    Deg(min[i].GetDouble())));
            }
            Forecast.ItemsSource = forecast;

            CurrentPanel.Visibility = Visibility.Visible;
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
        }
    }

    private static string Deg(double value) => $"{Math.Round(value):0}°";

    /// <summary>WMO weather interpretation codes.</summary>
    private static (string Icon, string Text) Describe(int code, bool day) => code switch
    {
        0 => (day ? "☀️" : "🌙", "Clear"),
        1 => (day ? "🌤️" : "🌙", "Mainly clear"),
        2 => ("⛅", "Partly cloudy"),
        3 => ("☁️", "Overcast"),
        45 or 48 => ("🌫️", "Fog"),
        >= 51 and <= 57 => ("🌦️", "Drizzle"),
        >= 61 and <= 67 => ("🌧️", "Rain"),
        >= 71 and <= 77 => ("🌨️", "Snow"),
        >= 80 and <= 82 => ("🌦️", "Rain showers"),
        85 or 86 => ("🌨️", "Snow showers"),
        >= 95 => ("⛈️", "Thunderstorm"),
        _ => ("🌡️", "Unknown"),
    };
}
