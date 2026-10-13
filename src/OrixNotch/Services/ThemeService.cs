using System.Windows;
using System.Windows.Media;

namespace OrixNotch.Services;

/// <param name="Accent">The accent this scheme is designed with (used while the accent is "Auto").</param>
/// <param name="PillTo">Gradient schemes: the notch fades from <paramref name="Pill"/> to this.</param>
public sealed record ColorScheme(
    string Id,
    string Name,
    Color Pill,
    Color Surface,
    Color Surface2,
    Color Hover,
    Color Text,
    Color SubText,
    bool IsLight,
    string Accent,
    Color? PillTo = null)
{
    public bool IsGradient => PillTo is not null;

    /// <summary>One colour standing for the notch background (edge fades, previews of solid things).</summary>
    public Color PillMid => PillTo is { } to ? ThemeService.Mix(Pill, to, 0.5) : Pill;

    public Brush PillBrush() => PillTo is { } to ? ThemeService.Gradient([Pill, to], vertical: true) : ThemeService.Solid(Pill);

    /// <summary>Settings preview card: the panel tint, fading into the gradient's colour for gradient schemes.</summary>
    public Brush PreviewBrush() => PillTo is { } to
        ? ThemeService.Gradient([Surface, ThemeService.Mix(Surface, to, 0.9)], vertical: true)
        : ThemeService.Solid(Surface);
}

/// <param name="Stops">Gradient accents: two or more colours, left to right. Empty for a solid accent.</param>
public sealed record AccentColor(string Id, string Name, Color Color, params Color[] Stops)
{
    public bool IsGradient => Stops.Length > 1;

    /// <summary>The accent as a brush, optionally see-through (soft fills).</summary>
    public Brush Brush(byte alpha = 0xFF) => IsGradient
        ? ThemeService.Gradient(Stops.Select(s => Color.FromArgb(alpha, s.R, s.G, s.B)).ToArray())
        : ThemeService.Solid(Color.FromArgb(alpha, Color.R, Color.G, Color.B));

    /// <summary>The average colour, for glows, waveforms and contrast checks.</summary>
    public Color Mean => IsGradient ? ThemeService.Mix(Stops[0], Stops[^1], 0.5) : Color;
}

/// <summary>
/// Swaps the brush resources at runtime. Everything in XAML references them with
/// DynamicResource, so a new scheme or accent applies instantly. Gradient accents and schemes put a
/// LinearGradientBrush in AccentBrush / PillBrush, so code that needs a single colour reads
/// <see cref="CurrentAccent"/>.Mean or <see cref="CurrentScheme"/>.PillMid instead of casting the brush.
/// </summary>
public static class ThemeService
{
    public static IReadOnlyList<ColorScheme> Schemes { get; } =
    [
        new("midnight", "Midnight", C("#000000"), C("#121214"), C("#1C1C1F"), C("#28282C"), C("#F5F5F7"), C("#8A8A90"), false, "orange"),
        new("graphite", "Graphite", C("#1B1B1D"), C("#242427"), C("#2D2D31"), C("#38383D"), C("#F2F2F4"), C("#94949A"), false, "blue"),
        new("slate", "Slate", C("#161A20"), C("#1F242C"), C("#283039"), C("#333C47"), C("#EEF1F5"), C("#8F99A6"), false, "sky"),
        new("nord", "Nord", C("#1A1F29"), C("#232A36"), C("#2C3442"), C("#38414F"), C("#ECEFF4"), C("#8E99AB"), false, "teal"),
        new("ocean", "Ocean", C("#07141F"), C("#0E1E2B"), C("#152837"), C("#1F3445"), C("#E6F1F8"), C("#86A0B3"), false, "ocean-g"),
        new("forest", "Forest", C("#0F1712"), C("#17211B"), C("#1F2B23"), C("#29372E"), C("#E9F2EC"), C("#8BA295"), false, "mint"),
        new("plum", "Plum", C("#170F1A"), C("#211725"), C("#2B1F30"), C("#372A3D"), C("#F4ECF6"), C("#A592AB"), false, "lavender"),
        new("mocha", "Mocha", C("#1C1714"), C("#26201C"), C("#302823"), C("#3C332C"), C("#F4EDE6"), C("#A39589"), false, "amber"),
        new("rosewood", "Rosewood", C("#1C1012"), C("#27181B"), C("#322024"), C("#3E2A2F"), C("#F7ECEE"), C("#AE9196"), false, "rose"),
        new("daylight", "Daylight", C("#FFFFFF"), C("#F3F3F5"), C("#E9E9EC"), C("#DCDCE1"), C("#1C1C1E"), C("#6E6E75"), true, "blue"),
        new("paper", "Paper", C("#FBF8F3"), C("#F2EDE4"), C("#E8E1D5"), C("#DDD4C5"), C("#24201A"), C("#776E61"), true, "orange"),
        new("frost", "Frost", C("#F6F9FC"), C("#EAF0F6"), C("#DFE7F0"), C("#D2DCE8"), C("#18212B"), C("#66727F"), true, "indigo"),

        // Gradient schemes: the notch itself fades top to bottom; panels keep a matching flat tint.
        new("aurora", "Aurora", C("#04131A"), C("#0B1E24"), C("#12292F"), C("#1B353B"), C("#E8F6F5"), C("#86A9A8"), false, "lagoon", C("#0F2F2A")),
        new("nebula", "Nebula", C("#0B0718"), C("#161026"), C("#201833"), C("#2B2241"), C("#F1ECFA"), C("#9C92B3"), false, "berry", C("#2A1240")),
        new("sunset", "Sunset", C("#130A12"), C("#20121A"), C("#2B1922"), C("#38222C"), C("#FAEEEB"), C("#B0928F"), false, "sunrise", C("#3A1A1C")),
        new("abyss", "Abyss", C("#02060F"), C("#0A1220"), C("#111B2C"), C("#1A2639"), C("#E9EEF7"), C("#8995A8"), false, "brand", C("#0A2340")),
        new("dawn", "Dawn", C("#FFF7F0"), C("#F5EDE8"), C("#ECE2DD"), C("#E0D4CF"), C("#241C1A"), C("#7A6B66"), true, "flamingo", C("#F3E6FA")),
    ];

    public static IReadOnlyList<AccentColor> Accents { get; } =
    [
        new("orange", "Orange", C("#FF8A3D")),
        new("amber", "Amber", C("#FFC233")),
        new("lime", "Lime", C("#A3E635")),
        new("green", "Green", C("#32D74B")),
        new("mint", "Mint", C("#34D3A0")),
        new("teal", "Teal", C("#3CD3E0")),
        new("sky", "Sky", C("#38BDF8")),
        new("blue", "Blue", C("#0A84FF")),
        new("indigo", "Indigo", C("#6E6CF0")),
        new("lavender", "Lavender", C("#A78BFA")),
        new("purple", "Purple", C("#BF5AF2")),
        new("pink", "Pink", C("#FF4F79")),
        new("rose", "Rose", C("#FB7185")),
        new("red", "Red", C("#FF453A")),
        new("mono", "Mono", C("#9A9AA2")),

        // Gradient accents (left to right).
        new("brand", "OrixNotch", C("#4F46FF"), C("#2E9CFF"), C("#4F46FF"), C("#7C3CFF"), C("#D946EF")),
        new("sunrise", "Sunrise", C("#FF7E5F"), C("#FF7E5F"), C("#FEB47B")),
        new("flamingo", "Flamingo", C("#FF5ACD"), C("#FF5ACD"), C("#FBDA61")),
        new("ocean-g", "Ocean", C("#2BC0E4"), C("#2BC0E4"), C("#4F7CFF")),
        new("neon", "Neon", C("#00F5A0"), C("#00F5A0"), C("#00D9F5")),
        new("berry", "Berry", C("#C471F5"), C("#C471F5"), C("#FA71CD")),
        new("ember", "Ember", C("#F83600"), C("#F83600"), C("#F9D423")),
        new("lagoon", "Lagoon", C("#43E97B"), C("#43E97B"), C("#38F9D7")),
    ];

    public static event Action? Changed;

    public static ColorScheme CurrentScheme =>
        Schemes.FirstOrDefault(s => s.Id == App.Settings.ThemeScheme) ?? Schemes[0];

    /// <summary>Accent setting that follows the scheme's own accent.</summary>
    public const string AutoAccent = "auto";

    public static bool IsAutoAccent => App.Settings.ThemeAccent == AutoAccent;

    public static AccentColor CurrentAccent => AccentFor(CurrentScheme);

    /// <summary>The accent <paramref name="scheme"/> shows: its own while the accent is Auto, else the one picked.</summary>
    public static AccentColor AccentFor(ColorScheme scheme) =>
        (IsAutoAccent ? null : Accents.FirstOrDefault(a => a.Id == App.Settings.ThemeAccent))
        ?? Accents.FirstOrDefault(a => a.Id == scheme.Accent)
        ?? Accents[0];

    public static void Apply()
    {
        var s = CurrentScheme;
        var a = CurrentAccent;
        var res = Application.Current.Resources;

        if (IsGlass)
        {
            // Glass: the notch is a see-through tint over the desktop (NotchWindow.Glass.cs),
            // panels are lighter panes on it and the rim catches light at the top.
            var pill = PillAlpha;
            res["PillBrush"] = s.PillTo is { } to ? Gradient([WithAlpha(s.Pill, pill), WithAlpha(to, pill)], vertical: true) : Solid(WithAlpha(s.Pill, pill));
            var pane = (byte)Math.Max(0x70, pill * 0.75);
            res["SurfaceBrush"] = Solid(WithAlpha(s.Surface, pane));
            res["Surface2Brush"] = Solid(WithAlpha(s.Surface2, pane));
            res["HoverBrush"] = Solid(WithAlpha(s.Hover, (byte)Math.Min(0xFF, pane + 0x20)));
        }
        else
        {
            res["PillBrush"] = s.PillBrush();
            res["SurfaceBrush"] = Solid(s.Surface);
            res["Surface2Brush"] = Solid(s.Surface2);
            res["HoverBrush"] = Solid(s.Hover);
        }
        res["TextBrush"] = Solid(s.Text);
        res["SubTextBrush"] = Solid(s.SubText);
        res["TrackBrush"] = Solid(WithAlpha(s.Text, (byte)(s.IsLight ? 0x22 : 0x2A)));
        res["StrokeBrush"] = IsGlass
            ? Gradient([WithAlpha(s.IsLight ? Colors.White : s.Text, 0x48), WithAlpha(s.Text, 0x12)], vertical: true)
            : Solid(WithAlpha(s.Text, (byte)(s.IsLight ? 0x1A : 0x16)));
        res["ThumbBrush"] = Solid(WithAlpha(s.Text, 0x40));
        res["AccentBrush"] = a.Brush();
        res["AccentSoftBrush"] = a.Brush(0x30);
        res["OnAccentBrush"] = Solid(Luminance(a.Mean) > 0.55 ? C("#111113") : C("#FFFFFF"));
        res["GoodBrush"] = Solid(s.IsLight ? C("#1E9E46") : C("#32D74B"));
        res["BadBrush"] = Solid(s.IsLight ? C("#D7362B") : C("#FF453A"));
        res["AccentColor"] = a.Mean;

        // Syntax colors for code blocks (GitHub-style palettes, tuned for dark and light schemes).
        var dark = !s.IsLight;
        res["CodeKeywordBrush"] = Solid(C(dark ? "#FF7B72" : "#CF222E"));
        res["CodeStringBrush"] = Solid(C(dark ? "#A5D6FF" : "#0A3069"));
        res["CodeCommentBrush"] = Solid(C(dark ? "#8B949E" : "#6E7781"));
        res["CodeNumberBrush"] = Solid(C(dark ? "#79C0FF" : "#0550AE"));
        res["CodeFunctionBrush"] = Solid(C(dark ? "#D2A8FF" : "#8250DF"));
        res["CodeTypeBrush"] = Solid(C(dark ? "#FFA657" : "#953800"));
        res["CodePropertyBrush"] = Solid(C(dark ? "#7EE787" : "#116329"));
        res["CodeVariableBrush"] = Solid(C(dark ? "#FFA657" : "#953800"));
        res["CodeTagBrush"] = Solid(C(dark ? "#7EE787" : "#116329"));

        Changed?.Invoke();
    }

    /// <summary>The notch is frosted glass (Settings › Appearance › Glass).</summary>
    public static bool IsGlass => App.Settings.Glass;

    /// <summary>Alpha of the notch tint while glass is on: more see-through as the strength drops.
    /// Light schemes keep more tint so dark text stays readable over dark wallpapers.</summary>
    public static byte PillAlpha
    {
        get
        {
            if (!IsGlass) return 0xFF;
            var t = Math.Clamp(App.Settings.GlassStrength, 0, 1);
            var min = CurrentScheme.IsLight ? 0x90 : 0x50;
            return (byte)(min + (0xE8 - min) * t);
        }
    }

    /// <summary>The notch colour as edge fades should paint it (see-through while glass is on).</summary>
    public static Color PillFadeColor => WithAlpha(CurrentScheme.PillMid, PillAlpha);

    public static void SetScheme(string id)
    {
        App.Settings.ThemeScheme = id;
        SettingsService.Save(notify: false);
        Apply();
    }

    public static void SetAccent(string id)
    {
        App.Settings.ThemeAccent = id;
        SettingsService.Save(notify: false);
        Apply();
    }

    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    internal static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    internal static SolidColorBrush Solid(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>Evenly spaced stops, left to right (or top to bottom).</summary>
    internal static LinearGradientBrush Gradient(Color[] stops, bool vertical = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = vertical ? new Point(0.5, 0) : new Point(0, 0.5),
            EndPoint = vertical ? new Point(0.5, 1) : new Point(1, 0.5),
        };
        for (var i = 0; i < stops.Length; i++) brush.GradientStops.Add(new GradientStop(stops[i], i / (double)(stops.Length - 1)));
        brush.Freeze();
        return brush;
    }
}
