using System.Globalization;

namespace OrixNotch.Services;

/// <summary>
/// Parses a number typed in either convention. .NET's AllowThousands accepts a group separator
/// anywhere, so "9.99" becomes 999 in de-DE. Here a separator counts as thousands only when
/// every group after it has exactly three digits and the culture groups with it (or both kinds appear).
/// </summary>
public static class NumberInput
{
    public static bool TryParse(string text, out double value)
    {
        value = 0;
        return Normalize(text) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParse(string text, out decimal value)
    {
        value = 0;
        return Normalize(text) is { } s && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The text in invariant form ("1234.5"), or null when the separators make no sense.</summary>
    internal static string? Normalize(string text, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var s = text.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "");
        int commas = s.Count(c => c == ','), dots = s.Count(c => c == '.');
        if (commas == 0 && dots == 0) return s;

        char group, dec;
        if (commas > 0 && dots > 0)
        {
            // Both kinds: the last one is the decimal point ("1.234,5" / "1,234.5").
            dec = s.LastIndexOf(',') > s.LastIndexOf('.') ? ',' : '.';
            group = dec == ',' ? '.' : ',';
            if (s.Count(c => c == dec) > 1 || !GroupsOk(s[..s.LastIndexOf(dec)], group)) return null;
        }
        else
        {
            var sep = commas > 0 ? ',' : '.';
            var count = commas + dots;
            var cultureGroups = culture.NumberFormat.NumberGroupSeparator == sep.ToString();
            // "1,000,000" is always thousands; one separator is thousands only if the culture groups with it.
            var thousands = GroupsOk(s, sep) && (count > 1 || cultureGroups);
            if (!thousands && count > 1) return null;
            group = thousands ? sep : '\0';
            dec = thousands ? '\0' : sep;
        }
        if (group != '\0') s = s.Replace(group.ToString(), "");
        return dec == ',' ? s.Replace(',', '.') : s;
    }

    /// <summary>Every group after the first separator has exactly three digits, and the first has 1–3.</summary>
    private static bool GroupsOk(string s, char sep)
    {
        var parts = s.TrimStart('-', '+').Split(sep);
        return parts.Length > 1 && parts[0].Length is >= 1 and <= 3 && parts.Skip(1).All(p => p.Length == 3 && p.All(char.IsDigit));
    }
}
