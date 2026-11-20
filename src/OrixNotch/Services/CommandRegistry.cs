using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OrixNotch.Settings;
using OrixNotch.Shell;
using OrixNotch.Tools;

namespace OrixNotch.Services;

/// <summary>One line in the command palette. <paramref name="Complete"/> set: choosing it fills the box instead of running.</summary>
public sealed record Suggestion(AppIcon Icon, string Title, string Hint, Action? Run = null, string? Complete = null);

/// <summary>
/// What the command palette understands: "timer 10", "todo buy milk", "ask …", "20 usd to eur", "12*7",
/// "quiet 1h", "mic", "awake", "settings keyboard", or part of a tool's name. Also behind orixnotch://run?text=….
/// </summary>
public static partial class CommandRegistry
{
    private const int Max = 6;

    /// <summary>Shown while the box is empty: one example per command, choosing it fills the box.</summary>
    public static readonly Suggestion[] Examples =
    [
        new(AppIcon.Clock, "timer 25", "Start a countdown: 10, 45m, 1h30", Complete: "timer "),
        new(AppIcon.ListBullet, "todo …", "Add a to-do", Complete: "todo "),
        new(AppIcon.Sparkles, "ask …", "Ask AI a question", Complete: "ask "),
        new(AppIcon.ArrowsLeftRight, "20 usd to eur", "Convert currency, or type a sum like 12*7", Complete: "20 usd to eur"),
        new(AppIcon.Fire, "focus 25", "Start a focus session; alerts wait until it ends", Complete: "focus "),
        new(AppIcon.Grid, "weather, phone, settings…", "Open a tool or a settings page by name", Complete: ""),
    ];

    public static List<Suggestion> Suggest(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return Examples.ToList();
        var list = new List<Suggestion>();
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        var window = NotchWindow.Instance;

        switch (verb)
        {
            case "timer" or "t" when ParseMinutes(rest) is { } min:
                list.Add(new(AppIcon.Clock, $"Start a {FormatMinutes(min)} timer", "Timers", () => StartTimer(min)));
                break;
            case "todo" or "add" when rest.Length > 0:
                list.Add(new(AppIcon.ListBullet, $"Add to-do: {rest}", "To-Dos", () =>
                {
                    window?.OpenTool("todos");
                    window?.View<TodosView>("todos")?.AddText(rest);
                }));
                break;
            case "ask" when rest.Length > 0:
                list.Add(AskSuggestion(rest));
                break;
            case "focus" or "pomodoro":
                var focusMin = rest.Length == 0 ? App.Settings.PomodoroWorkMin : ParseMinutes(rest);
                if (focusMin is { } fm)
                    list.Add(new(AppIcon.Fire, $"Focus for {FormatMinutes(fm)}", App.Settings.FocusQuiet ? "Alerts wait until it ends" : "Pomodoro",
                        () => { PomodoroService.Instance.StartFocus(fm); NotchWindow.Instance?.Collapse(); }));
                break;
            case "quiet":
                if (rest is "off" or "stop" or "end")
                    list.Add(new(AppIcon.Moon, "Turn quiet off", "Alerts show again", () => SetQuiet(null)));
                else if ((rest.Length == 0 ? 60 : ParseMinutes(rest)) is { } quietMin)
                    list.Add(new(AppIcon.Moon, $"Quiet for {FormatMinutes(quietMin)}", "Alerts are held, then summed up", () => SetQuiet(quietMin)));
                break;
            case "settings" or "setting" or "set":
                foreach (var page in SettingsView.Pages.Where(p => rest.Length == 0 || Score(p.Name, rest) > 0 || Score(p.Id, rest) > 0).Take(Max))
                    list.Add(new(page.Icon, $"Settings › {page.Name}", "Settings", () => window?.OpenSettings(page.Id)));
                break;
        }

        if (ParseConversion(text) is { } conv)
        {
            var result = ConverterView.ConvertCurrency(conv.Amount, conv.From, conv.To);
            if (result is { } value)
            {
                var shown = value.ToString(value >= 1 ? "N2" : "0.####", CultureInfo.CurrentCulture);
                list.Add(new(AppIcon.ArrowsLeftRight, $"{conv.Amount.ToString("0.##", CultureInfo.CurrentCulture)} {conv.From} = {shown} {conv.To}",
                    "Enter copies the amount", () => ClipboardService.Instance.CopyQuiet(value.ToString("0.##", CultureInfo.CurrentCulture))));
            }
            else
            {
                list.Add(new(AppIcon.ArrowsLeftRight, ConverterView.HasRates ? $"No rate for {conv.From} or {conv.To}" : "Loading exchange rates…",
                    "Currency codes like USD, EUR, BDT", Complete: text));
            }
        }
        else if (Evaluate(text) is { } sum)
        {
            var shown = sum.ToString("0.########", CultureInfo.CurrentCulture);
            list.Add(new(AppIcon.Plus, $"= {shown}", "Enter copies the result", () => ClipboardService.Instance.CopyQuiet(shown)));
        }

        // One-word actions, then tools by name ("open weather" works too).
        var query = verb == "open" ? rest : text;
        foreach (var action in Actions())
            if (Score(action.Title, query) >= 2 || action.Words.Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase) && query.Length >= 2))
                list.Add(action.Suggestion);
        foreach (var tool in ToolRegistry.All
                     .Select(t => (Tool: t, Score: Math.Max(Score(t.Name, query), Score(t.Id, query))))
                     .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Tool))
            list.Add(new(tool.Icon, $"Open {tool.Name}", "Tool", () => window?.OpenTool(tool.Id)));
        if (Score("Settings", query) >= 2 && verb is not ("settings" or "setting" or "set"))
            list.Add(new(AppIcon.Gear, "Open Settings", "Settings", () => window?.OpenTool(NotchWindow.SettingsId)));

        // Nothing matched: offer the text to Ask AI or as a to-do.
        if (list.Count == 0 && text.Length >= 3)
        {
            list.Add(AskSuggestion(text));
            list.Add(new(AppIcon.ListBullet, $"Add to-do: {text}", "To-Dos", () =>
            {
                window?.OpenTool("todos");
                window?.View<TodosView>("todos")?.AddText(text);
            }));
        }
        return list.DistinctBy(s => s.Title).Take(Max).ToList();
    }

    /// <summary>Runs the best match for a whole command (orixnotch://run?text=…). False when nothing matched.</summary>
    public static bool RunText(string text)
    {
        var first = Suggest(text).FirstOrDefault(s => s.Run is not null);
        first?.Run?.Invoke();
        return first is not null;
    }

    private static Suggestion AskSuggestion(string question) =>
        new(AppIcon.Sparkles, $"Ask AI: {question}", "Ask AI", () =>
        {
            NotchWindow.Instance?.OpenTool("ask");
            NotchWindow.Instance?.View<AskAiView>("ask")?.Ask(question);
        });

    private sealed record QuickAction(string Title, string[] Words, Suggestion Suggestion);

    private static IEnumerable<QuickAction> Actions()
    {
        var micMuted = VolumeService.Instance.MicMuted;
        yield return new(micMuted ? "Unmute microphone" : "Mute microphone", ["mic", "microphone", "mute"],
            new(micMuted ? AppIcon.Microphone : AppIcon.MicrophoneOff, micMuted ? "Unmute microphone" : "Mute microphone", "Microphone",
                () => VolumeService.Instance.ToggleMic()));
        yield return new("Keep awake", ["awake", "caffeine", "sleep"],
            new(AppIcon.Coffee, KeepAwakeService.On ? "Stop keeping awake" : "Keep awake", "Keeps the PC and screen on", KeepAwakeService.Toggle));
        yield return new("My day", ["day", "wrap", "today", "summary"],
            new(AppIcon.Hourglass, "My day", "Screen time, focus and to-dos today", () => NotchWindow.Instance?.ShowWrap()));
        yield return new("Copy text from screen", ["ocr", "copy text", "text from screen", "snip"],
            new(AppIcon.DocText, "Copy text from screen", "Snip, then the text is copied", () => OcrService.Instance.Start()));
    }

    private static void StartTimer(int minutes)
    {
        CountdownService.Instance.SetMinutes(minutes);
        CountdownService.Instance.ToggleCommand.Execute(null);
        NotchWindow.Instance?.Collapse(); // the closed notch shows the countdown
    }

    private static void SetQuiet(int? minutes)
    {
        App.Settings.QuietUntil = minutes is { } m ? DateTime.Now.AddMinutes(m) : null;
        SettingsService.Save(notify: false);
        NotchWindow.Instance?.Collapse();
    }

    // ---------------------------------------------------------------- pure parsing (checked below)

    /// <summary>"10" or "10m" → 10, "1h" → 60, "1h30" / "1h 30m" / "1:30" → 90. Null if not a duration (or over 24 h).</summary>
    public static int? ParseMinutes(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(" ", "");
        if (t.Length == 0) return null;
        var m = DurationRegex().Match(t);
        if (!m.Success) return null;
        int total;
        if (m.Groups["hc"].Success) total = int.Parse(m.Groups["hc"].Value) * 60 + int.Parse(m.Groups["mc"].Value);
        else total = (m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) * 60 : 0) + (m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0);
        return total is > 0 and <= 24 * 60 ? total : null;
    }

    [GeneratedRegex(@"^(?:(?<hc>\d{1,2}):(?<mc>\d{2})|(?:(?<h>\d{1,2})h)?(?:(?<m>\d{1,4})(?:m|min|mins)?)?)$")]
    private static partial Regex DurationRegex();

    public static string FormatMinutes(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    public sealed record Conversion(double Amount, string From, string To);

    /// <summary>"20 usd to eur", "20usd in bdt", "1,500 EUR = USD" → (20, "USD", "EUR"). Null otherwise.</summary>
    public static Conversion? ParseConversion(string text)
    {
        var m = ConversionRegex().Match(text.Trim());
        if (!m.Success) return null;
        var number = m.Groups["n"].Value.Replace(",", "");
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            ? new(amount, m.Groups["f"].Value.ToUpperInvariant(), m.Groups["t"].Value.ToUpperInvariant())
            : null;
    }

    [GeneratedRegex(@"^(?<n>\d[\d,]*(?:\.\d+)?)\s*(?<f>[a-zA-Z]{3})\s+(?:to|in|=|->)\s+(?<t>[a-zA-Z]{3})$")]
    private static partial Regex ConversionRegex();

    /// <summary>Plain arithmetic: "12*7", "(3+4)/2", "15% of 80" isn't (null). Needs an operator and digits only.</summary>
    public static double? Evaluate(string text)
    {
        var t = text.Trim().Replace("×", "*").Replace("÷", "/").Replace(" ", "");
        if (t.Length < 3 || !SumRegex().IsMatch(t) || !t.Any(char.IsDigit)) return null;
        if (!t[1..].Any(c => c is '+' or '-' or '*' or '/' or '%')) return null; // "-5" alone is a number, not a sum
        try
        {
            var value = Convert.ToDouble(new DataTable().Compute(t, null), CultureInfo.InvariantCulture);
            return double.IsFinite(value) ? value : null;
        }
        catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidCastException or OverflowException or DivideByZeroException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[\d.+\-*/()%]+$")]
    private static partial Regex SumRegex();

    /// <summary>How well a name matches what was typed: 3 starts with it, 2 a word starts with it, 1 contains it.</summary>
    public static int Score(string name, string query)
    {
        if (query.Length == 0) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Split(' ', '-', '&').Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase))) return 2;
        return query.Length >= 3 && name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

#if DEBUG
    static CommandRegistry()
    {
        Debug.Assert(ParseMinutes("10") == 10 && ParseMinutes("45m") == 45 && ParseMinutes("1h") == 60);
        Debug.Assert(ParseMinutes("1h30") == 90 && ParseMinutes("1h 30m") == 90 && ParseMinutes("1:30") == 90);
        Debug.Assert(ParseMinutes("") is null && ParseMinutes("abc") is null && ParseMinutes("0") is null && ParseMinutes("30h") is null);
        Debug.Assert(ParseConversion("20 usd to eur") == new Conversion(20, "USD", "EUR"));
        Debug.Assert(ParseConversion("1,500.5EUR in bdt") == new Conversion(1500.5, "EUR", "BDT"));
        Debug.Assert(ParseConversion("timer 10") is null && ParseConversion("20 usd") is null);
        Debug.Assert(Evaluate("12*7") == 84 && Evaluate("(3+4)/2") == 3.5 && Evaluate("7/2") == 3.5);
        Debug.Assert(Evaluate("10") is null && Evaluate("-5") is null && Evaluate("2+") is null && Evaluate("1/0") is null && Evaluate("abc") is null);
        Debug.Assert(Score("Weather", "wea") == 3 && Score("Screen Time", "time") == 2 && Score("Clipboard", "board") == 1 && Score("Phone", "x") == 0);
    }
#endif
}
