using System.Text.RegularExpressions;

namespace OrixNotch.Tools;

public enum CodeToken
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
    Function,
    Type,
    Property,
    Variable,
    Tag,
}

/// <summary>
/// Small regex-based syntax highlighter for chat code blocks. One combined pattern per language
/// family, compiled once and cached; good enough to make code readable, not a full parser.
/// </summary>
public static class CodeHighlighter
{
    private sealed record Language(string Name, Regex Pattern);

    private static readonly Dictionary<string, Language> Cache = new();

    private const string CLikeKeywords =
        "abstract|as|async|await|base|break|case|catch|class|const|continue|default|delegate|do|else|enum|event|export|extends|" +
        "extern|false|final|finally|fixed|for|foreach|from|func|function|get|goto|if|implements|import|in|init|interface|internal|" +
        "is|let|lock|match|namespace|new|null|nil|operator|out|override|package|params|private|protected|public|readonly|record|ref|" +
        "return|sealed|set|static|struct|super|switch|this|throw|true|try|typeof|using|var|virtual|void|volatile|when|where|while|" +
        "with|yield|fn|impl|mod|mut|pub|crate|self|trait|type|unsafe|loop|move|dyn|go|chan|defer|select|range|map|fun|val|object|" +
        "companion|data|sealed|lateinit|guard|inout|throws|rethrows|protocol|extension|def|elif|lambda|pass|raise|except|global|" +
        "nonlocal|assert|del|not|and|or|None|True|False|echo|require|include|end|unless|until|begin|rescue|ensure|module|then";

    private const string ShellKeywords =
        "if|then|else|elif|fi|for|while|until|do|done|case|esac|in|function|return|exit|local|export|readonly|declare|source|" +
        "alias|unset|shift|break|continue|echo|cd|sudo|param|foreach|switch|try|catch|finally|throw|begin|process|end|filter";

    private const string SqlKeywords =
        "select|from|where|and|or|not|insert|into|values|update|set|delete|create|table|drop|alter|add|index|view|join|inner|" +
        "left|right|outer|full|on|as|group|by|order|having|limit|offset|union|all|distinct|case|when|then|else|end|null|is|in|" +
        "like|between|exists|primary|key|foreign|references|default|unique|check|constraint|asc|desc|with|returning|begin|commit|rollback";

    /// <summary>Splits code into (text, kind) runs; concatenating the texts gives back the input.</summary>
    public static IEnumerable<(string Text, CodeToken Kind)> Tokenize(string code, string language)
    {
        var lang = Get(language);
        var at = 0;
        foreach (Match m in lang.Pattern.Matches(code))
        {
            if (m.Length == 0) continue;
            if (m.Index > at) yield return (code[at..m.Index], CodeToken.Plain);
            yield return (m.Value, KindOf(m));
            at = m.Index + m.Length;
        }
        if (at < code.Length) yield return (code[at..], CodeToken.Plain);
    }

    /// <summary>Human-readable name for a fence label ("ts" → "TypeScript").</summary>
    public static string DisplayName(string language) => Normalize(language) switch
    {
        "js" => "JavaScript",
        "ts" => "TypeScript",
        "py" => "Python",
        "cs" => "C#",
        "cpp" => "C++",
        "c" => "C",
        "java" => "Java",
        "kotlin" => "Kotlin",
        "go" => "Go",
        "rust" => "Rust",
        "swift" => "Swift",
        "php" => "PHP",
        "ruby" => "Ruby",
        "bash" => "Bash",
        "powershell" => "PowerShell",
        "sql" => "SQL",
        "json" => "JSON",
        "yaml" => "YAML",
        "html" => "HTML",
        "xml" => "XML",
        "css" => "CSS",
        "" => "Code",
        var other => other,
    };

    private static string Normalize(string language) => language.Trim().ToLowerInvariant() switch
    {
        "javascript" or "jsx" or "mjs" or "node" => "js",
        "typescript" or "tsx" => "ts",
        "python" or "python3" => "py",
        "csharp" or "c#" => "cs",
        "c++" or "cc" or "hpp" or "h" => "cpp",
        "kt" or "kts" => "kotlin",
        "golang" => "go",
        "rs" => "rust",
        "rb" => "ruby",
        "sh" or "shell" or "zsh" or "console" or "terminal" => "bash",
        "ps" or "ps1" or "pwsh" => "powershell",
        "postgres" or "postgresql" or "mysql" or "sqlite" => "sql",
        "yml" => "yaml",
        "htm" or "xaml" or "svg" or "vue" => "html",
        "scss" or "less" => "css",
        var l => l,
    };

    private static Language Get(string language)
    {
        var key = Normalize(language);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var lang = new Language(key, new Regex(PatternFor(key), RegexOptions.Compiled | RegexOptions.Multiline));
            Cache[key] = lang;
            return lang;
        }
    }

    private static string PatternFor(string key)
    {
        const string dq = @"""(?:\\.|[^""\\\n])*""";
        const string sq = @"'(?:\\.|[^'\\\n])*'";
        const string bt = @"`(?:\\.|[^`\\])*`";
        const string num = @"\b(?:0[xX][0-9a-fA-F_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?)\b";
        const string func = @"\b[A-Za-z_]\w*(?=\s*\()";
        const string type = @"\b[A-Z][A-Za-z0-9_]*\b";

        static string Kw(string words, bool ignoreCase = false) => (ignoreCase ? "(?i)" : "") + $@"\b(?:{words})\b";

        // Group names map to token kinds (see KindOf). Order = priority.
        return key switch
        {
            "json" => $@"(?<prop>{dq}(?=\s*:))|(?<str>{dq})|(?<num>-?{num})|(?<kw>\b(?:true|false|null)\b)",
            "yaml" => $@"(?<com>#.*$)|(?<prop>^\s*[\w.-]+(?=\s*:))|(?<str>{dq}|{sq})|(?<num>{num})|(?<kw>\b(?:true|false|null|yes|no)\b)",
            "html" or "xml" => $@"(?<com><!--[\s\S]*?-->)|(?<tag></?[\w:.-]+|/?>)|(?<prop>\b[\w:-]+(?==))|(?<str>{dq}|{sq})",
            "css" => $@"(?<com>/\*[\s\S]*?\*/)|(?<str>{dq}|{sq})|(?<prop>[\w-]+(?=\s*:))|(?<num>-?\d+(?:\.\d+)?(?:px|em|rem|%|vh|vw|s|ms)?)|(?<kw>@[\w-]+|!important)|(?<type>[.#][\w-]+)",
            "sql" => $@"(?<com>--.*$|/\*[\s\S]*?\*/)|(?<str>{sq}|{dq})|(?<num>{num})|(?<kw>{Kw(SqlKeywords, ignoreCase: true)})|(?<fn>{func})",
            "bash" => $@"(?<com>(?<![\w$])#.*$)|(?<str>{dq}|{sq})|(?<var>\$\{{[^}}]*\}}|\$\w+|\$[@#?$!*0-9])|(?<num>{num})|(?<kw>{Kw(ShellKeywords)})",
            "powershell" => $@"(?<com><#[\s\S]*?#>|#.*$)|(?<str>{dq}|{sq})|(?<var>\$[\w:]+)|(?<kw>{Kw(ShellKeywords, ignoreCase: true)})|(?<fn>\b[A-Za-z]+-[A-Za-z]+\b)|(?<num>{num})",
            "py" => $@"(?<com>#.*$)|(?<str>(?:[rbfu]{{0,2}})(?:""""""[\s\S]*?""""""|'''[\s\S]*?'''|{dq}|{sq}))|(?<num>{num})|(?<kw>{Kw(CLikeKeywords)})|(?<fn>{func})|(?<type>{type})|(?<var>@\w+)",
            "ruby" => $@"(?<com>#.*$)|(?<str>{dq}|{sq})|(?<var>[@$]\w+|:\w+)|(?<num>{num})|(?<kw>{Kw(CLikeKeywords)})|(?<fn>{func})|(?<type>{type})",
            "php" => $@"(?<com>//.*$|#.*$|/\*[\s\S]*?\*/)|(?<str>{dq}|{sq})|(?<var>\$\w+)|(?<num>{num})|(?<kw>{Kw(CLikeKeywords)})|(?<fn>{func})|(?<type>{type})",
            // C family, JS/TS, Java, Kotlin, Go, Rust, Swift, and anything unknown.
            _ => $@"(?<com>//.*$|/\*[\s\S]*?\*/|(?<=^\s*)#.*$)|(?<str>{dq}|{sq}|{bt})|(?<num>{num})|(?<kw>{Kw(CLikeKeywords)})|(?<fn>{func})|(?<type>{type})|(?<var>@\w+)",
        };
    }

    private static CodeToken KindOf(Match m)
    {
        if (m.Groups["com"].Success) return CodeToken.Comment;
        if (m.Groups["str"].Success) return CodeToken.String;
        if (m.Groups["prop"].Success) return CodeToken.Property;
        if (m.Groups["tag"].Success) return CodeToken.Tag;
        if (m.Groups["var"].Success) return CodeToken.Variable;
        if (m.Groups["num"].Success) return CodeToken.Number;
        if (m.Groups["kw"].Success) return CodeToken.Keyword;
        if (m.Groups["fn"].Success) return CodeToken.Function;
        if (m.Groups["type"].Success) return CodeToken.Type;
        return CodeToken.Plain;
    }
}
