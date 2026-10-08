using System.Windows;
using System.Windows.Media;

namespace OrixNotch.Services;

public sealed record ColorScheme(
    string Id,
    string Name,
    Color Pill,
    Color Surface,
    Color Surface2,
    Color Hover,
    Color Text,
    Color SubText,
    bool IsLight);

public sealed record AccentColor(string Id, string Name, Color Color);

/// <summary>
/// Swaps the brush resources at runtime. Everything in XAML references them with
/// DynamicResource, so a new scheme or accent applies instantly.
/// </summary>
public static class ThemeService
{
    public static IReadOnlyList<ColorScheme> Schemes { get; } =
    [
        new("midnight", "Midnight", C("#000000"), C("#121214"), C("#1C1C1F"), C("#28282C"), C("#F5F5F7"), C("#8A8A90"), false),
        new("graphite", "Graphite", C("#1B1B1D"), C("#242427"), C("#2D2D31"), C("#38383D"), C("#F2F2F4"), C("#94949A"), false),
        new("nord", "Nord", C("#1A1F29"), C("#232A36"), C("#2C3442"), C("#38414F"), C("#ECEFF4"), C("#8E99AB"), false),
        new("mocha", "Mocha", C("#1C1714"), C("#26201C"), C("#302823"), C("#3C332C"), C("#F4EDE6"), C("#A39589"), false),
        new("forest", "Forest", C("#0F1712"), C("#17211B"), C("#1F2B23"), C("#29372E"), C("#E9F2EC"), C("#8BA295"), false),
        new("daylight", "Daylight", C("#FFFFFF"), C("#F3F3F5"), C("#E9E9EC"), C("#DCDCE1"), C("#1C1C1E"), C("#6E6E75"), true),
    ];

    public static IReadOnlyList<AccentColor> Accents { get; } =
    [
        new("orange", "Orange", C("#FF8A3D")),
        new("amber", "Amber", C("#FFC233")),
        new("green", "Green", C("#32D74B")),
        new("teal", "Teal", C("#3CD3E0")),
        new("blue", "Blue", C("#0A84FF")),
        new("indigo", "Indigo", C("#6E6CF0")),
        new("purple", "Purple", C("#BF5AF2")),
        new("pink", "Pink", C("#FF4F79")),
        new("red", "Red", C("#FF453A")),
        new("mono", "Mono", C("#9A9AA2")),
    ];

    public static event Action? Changed;

    public static ColorScheme CurrentScheme =>
        Schemes.FirstOrDefault(s => s.Id == App.Settings.ThemeScheme) ?? Schemes[0];

    public static AccentColor CurrentAccent =>
        Accents.FirstOrDefault(a => a.Id == App.Settings.ThemeAccent) ?? Accents[0];

    public static void Apply()
    {
        var s = CurrentScheme;
        var accent = CurrentAccent.Color;
        var res = Application.Current.Resources;

        res["PillBrush"] = B(s.Pill);
        res["SurfaceBrush"] = B(s.Surface);
        res["Surface2Brush"] = B(s.Surface2);
        res["HoverBrush"] = B(s.Hover);
        res["TextBrush"] = B(s.Text);
        res["SubTextBrush"] = B(s.SubText);
        res["TrackBrush"] = B(WithAlpha(s.Text, (byte)(s.IsLight ? 0x22 : 0x2A)));
        res["StrokeBrush"] = B(WithAlpha(s.Text, (byte)(s.IsLight ? 0x1A : 0x16)));
        res["ThumbBrush"] = B(WithAlpha(s.Text, 0x40));
        res["AccentBrush"] = B(accent);
        res["AccentSoftBrush"] = B(WithAlpha(accent, 0x30));
        res["OnAccentBrush"] = B(Luminance(accent) > 0.55 ? C("#111113") : C("#FFFFFF"));        res["GoodBrush"] = B(s.IsLight ? C("#1E9E46") : C("#32D74B"));
        res["BadBrush"] = B(s.IsLight ? C("#D7362B") : C("#FF453A"));
        res["AccentColor"] = accent;

        // Syntax colors for code blocks (GitHub-style palettes, tuned for dark and light schemes).
        var dark = !s.IsLight;
        res["CodeKeywordBrush"] = B(C(dark ? "#FF7B72" : "#CF222E"));
        res["CodeStringBrush"] = B(C(dark ? "#A5D6FF" : "#0A3069"));
        res["CodeCommentBrush"] = B(C(dark ? "#8B949E" : "#6E7781"));
        res["CodeNumberBrush"] = B(C(dark ? "#79C0FF" : "#0550AE"));
        res["CodeFunctionBrush"] = B(C(dark ? "#D2A8FF" : "#8250DF"));
        res["CodeTypeBrush"] = B(C(dark ? "#FFA657" : "#953800"));
        res["CodePropertyBrush"] = B(C(dark ? "#7EE787" : "#116329"));
        res["CodeVariableBrush"] = B(C(dark ? "#FFA657" : "#953800"));
        res["CodeTagBrush"] = B(C(dark ? "#7EE787" : "#116329"));

        Changed?.Invoke();
    }

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

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush B(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
