using System.Collections.Concurrent;
using System.Text.Json;

namespace OrixNotch.Services;

/// <summary>Open-Meteo's geocoding, shared by the Weather tool and the idle notch's weather.</summary>
public static class OpenMeteo
{
    public sealed record Place(string Name, string? Country, double Lat, double Lon);

    // A city doesn't move: look each one up once per session.
    private static readonly ConcurrentDictionary<string, Place> Places = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The best match for a city name; null when there's none. Network errors throw.</summary>
    public static async Task<Place?> FindAsync(string city)
    {
        if (Places.TryGetValue(city, out var known)) return known;
        using var geo = JsonDocument.Parse(await Http.Client.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name={Uri.EscapeDataString(city)}"));
        if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var p = results[0];
        var place = new Place(p.GetProperty("name").GetString() ?? city,
            p.TryGetProperty("country", out var c) ? c.GetString() : null,
            p.GetProperty("latitude").GetDouble(), p.GetProperty("longitude").GetDouble());
        Places[city] = place;
        return place;
    }
}
