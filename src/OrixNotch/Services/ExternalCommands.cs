using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using OrixNotch.Shell;

namespace OrixNotch.Services;

/// <summary>
/// Commands from outside the app: the command line (<c>--open weather</c>, <c>--settings</c>) and
/// <c>orixnotch://</c> links. A second launch hands its arguments to the running copy over a named pipe,
/// so links and <c>--open</c> also work while OrixNotch is already running.
/// </summary>
public static class ExternalCommands
{
    public const string Scheme = "orixnotch";
    public const int MaxText = 80;

    // Pipes are machine-wide: the session id keeps two signed-in users apart, CurrentUserOnly keeps others out.
    private static readonly string PipeName = $"OrixNotch.Args.{Process.GetCurrentProcess().SessionId}";

    /// <summary>One parsed link: <c>orixnotch://open/weather</c> → ("open", "weather", {}).</summary>
    public sealed record Link(string Verb, string Target, IReadOnlyDictionary<string, string> Query)
    {
        public string Get(string key) => Query.TryGetValue(key, out var v) ? v : "";
    }

    public static Link? ParseLink(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            query[key] = value;
        }
        return new Link(uri.Host.ToLowerInvariant(), Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')).ToLowerInvariant(), query);
    }

#if DEBUG
    static ExternalCommands()
    {
        var a = ParseLink("orixnotch://peek?text=Build%20done&sub=3+tests");
        Debug.Assert(a is { Verb: "peek" } && a.Get("text") == "Build done" && a.Get("sub") == "3 tests");
        var b = ParseLink("ORIXNOTCH://open/Weather/");
        Debug.Assert(b is { Verb: "open", Target: "weather" });
        Debug.Assert(ParseLink("orixnotch://timer?min=10")?.Get("min") == "10");
        Debug.Assert(ParseLink("https://example.com/peek") is null && ParseLink("--open") is null);
        Debug.Assert(Clip("  a  ") == "a" && Clip(new string('x', 200)).Length == MaxText);
    }
#endif

    public static string Clip(string? text)
    {
        var t = (text ?? "").ReplaceLineEndings(" ").Trim();
        return t.Length <= MaxText ? t : t[..(MaxText - 1)] + "…";
    }

    /// <summary>Runs arguments from the command line or a second launch (UI thread).</summary>
    public static void Run(string[] args)
    {
        var window = NotchWindow.Instance;
        if (window is null) return;
        if (args.Length == 0)
        {
            window.Expand(activate: true); // launching it again while it runs opens the notch
            return;
        }
        if (ParseLink(args[0]) is { } link)
        {
            RunLink(window, link);
            return;
        }
        var openAt = Array.IndexOf(args, "--open");
        if (openAt >= 0)
        {
            window.Expand(activate: true);
            if (openAt + 1 < args.Length) window.ShowTool(args[openAt + 1]);
        }
        if (args.Contains("--settings")) window.OpenTool(NotchWindow.SettingsId);
    }

    private static void RunLink(NotchWindow window, Link link)
    {
        switch (link.Verb)
        {
            case "peek":
                var title = Clip(link.Get("title") is { Length: > 0 } t ? t : link.Get("text"));
                if (title.Length > 0) window.ShowExternalPeek(title, Clip(link.Get("sub")), link.Get("icon"));
                break;
            case "open":
                if (link.Target == NotchWindow.SettingsId) window.OpenTool(NotchWindow.SettingsId);
                else if (ToolRegistry.All.Any(t => t.Id == link.Target)) window.OpenTool(link.Target);
                else window.Expand(activate: true);
                break;
            case "timer":
                if (!int.TryParse(link.Get("min"), out var min)) min = App.Settings.TimerMinutes;
                CountdownService.Instance.SetMinutes(Math.Clamp(min, 1, 600));
                CountdownService.Instance.ToggleCommand.Execute(null);
                break;
            default:
                window.Expand(activate: true);
                break;
        }
    }

    // ---------------------------------------------------------------- second launch

    /// <summary>Called by a second launch: passes its arguments to the running copy. False if none answered.</summary>
    public static bool SendToRunning(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for second launches for the life of the app.</summary>
    public static void Listen()
    {
        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    var buffer = new byte[8192]; // arguments are short; anything longer is cut off
                    var length = 0;
                    int read;
                    while (length < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(length))) > 0) length += read;
                    var args = Encoding.UTF8.GetString(buffer, 0, length).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    _ = dispatcher.BeginInvoke(() => Run(args));
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                    await Task.Delay(1000); // don't spin if the pipe can't be created
                }
            }
        });
    }

    // ---------------------------------------------------------------- link registration

    /// <summary>
    /// Registers <c>orixnotch://</c> for this exe (per user, no admin). The MSIX declares it in its manifest
    /// instead, and the MSI also writes this key so uninstall removes it. The last copy launched owns the links.
    /// </summary>
    public static void RegisterLinks()
    {
        if (StartupService.IsPackaged) return;
        try
        {
            var command = $"\"{Environment.ProcessPath}\" \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            using var cmd = key.CreateSubKey(@"shell\open\command");
            if (cmd.GetValue(null) as string == command) return;
            key.SetValue(null, "URL:OrixNotch");
            key.SetValue("URL Protocol", "");
            cmd.SetValue(null, command);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
