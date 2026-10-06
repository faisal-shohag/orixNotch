using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrixNotch.Services;

public sealed record LyricLine(double Time, string Text);

/// <summary>
/// Looks up time-synced lyrics for the current track on LRCLIB (https://lrclib.net), a free
/// public lyrics API. Nothing is bundled; results are cached in memory for the session.
/// </summary>
public static partial class LyricsService
{
    private static readonly Dictionary<string, IReadOnlyList<LyricLine>?> Cache = new();

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    public static async Task<IReadOnlyList<LyricLine>?> GetAsync(string title, string artist, double duration)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        var key = $"{artist}\u0001{title}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        IReadOnlyList<LyricLine>? result = null;
        try
        {
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            if (duration > 0) url += $"&duration={Math.Round(duration).ToString(CultureInfo.InvariantCulture)}";
            using var response = await Http.Client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
                    result = Parse(synced.GetString()!);
            }
        }
        catch (HttpRequestException)
        {
            return null; // offline: don't cache, try again next time
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        Cache[key] = result;
        return result;
    }

    private static List<LyricLine>? Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = LrcLine().Match(raw.Trim());
            if (!m.Success) continue;
            var t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            lines.Add(new LyricLine(t, m.Groups[3].Value.Trim()));
        }
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>Index of the line being sung at <paramref name="position"/> seconds, or -1.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, double position)
    {
        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time <= position + 0.15) index = i;
            else break;
        }
        return index;
    }
}
