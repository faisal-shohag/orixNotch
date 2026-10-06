using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>Chat with Claude or Gemini: suggestion chips, streamed replies, code blocks with copy.</summary>
public partial class AskAiView : UserControl, IToolView
{
    private static readonly string[] SuggestionTexts = ["Explain something simply", "Summarize my clipboard", "Draft a short reply"];

    private readonly List<(bool FromUser, string Text)> _conversation = new();
    private CancellationTokenSource? _cts;
    private string? _keySetupFor;

    public AskAiView()
    {
        InitializeComponent();
        foreach (var text in SuggestionTexts)
        {
            var chip = new Button { Content = text, Margin = new Thickness(3, 0, 3, 0), Height = 24, FontSize = 11.5 };
            chip.Click += (_, _) => OnSuggestion(text);
            Suggestions.Children.Add(chip);
        }
    }

    public void OnShown() => UpdateKeyState();

    /// <summary>No key yet for the active provider → show the key field instead of suggestions.</summary>
    private void UpdateKeyState()
    {
        var provider = AiService.ActiveProvider;
        var gemini = provider == AiService.ProviderGemini;
        var hasKey = AiService.HasKeyFor(provider);
        EmptySub.Text = hasKey
            ? gemini ? "Powered by Gemini. Messages go to Google's API."
                     : "Powered by Claude. Messages go to Anthropic's API."
            : gemini ? "Paste your Google AI Studio key to start chatting."
                     : "Paste your Anthropic API key to start chatting.";
        KeyHint.Text = gemini
            ? "Get a key at aistudio.google.com → Get API key. It's stored encrypted on this PC."
            : "Get a key at console.anthropic.com → API keys. It's stored encrypted on this PC.";
        Suggestions.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        KeySetup.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        Input.IsEnabled = hasKey;
        if (!hasKey && (KeyInputHost.Content is null || _keySetupFor != provider))
            KeyInputHost.Content = OrixNotch.Settings.SettingsView.ApiKeyInput(
                () => AiService.HasKeyFor(provider),
                key => AiService.SaveKeyFor(provider, key),
                () =>
                {
                    KeyInputHost.Content = null;
                    _keySetupFor = null;
                    UpdateKeyState();
                    Input.Focus();
                },
                gemini ? "AIza…" : "sk-ant-…",
                gemini ? "Google AI Studio API key (AIza…)" : "Anthropic API key (sk-ant-…)");
        if (!hasKey) _keySetupFor = provider;
        if (hasKey && _conversation.Count == 0) Input.Focus();
    }

    public void OnHidden()
    {
    }

    private void OnSuggestion(string text)
    {
        if (text == "Summarize my clipboard")
        {
            var clip = ClipboardService.Instance.Items.FirstOrDefault(i => i.Kind is ClipKind.Text or ClipKind.Link)?.Text;
            if (string.IsNullOrWhiteSpace(clip))
            {
                Input.Text = "Summarize: ";
                Input.Focus();
                Input.CaretIndex = Input.Text.Length;
                return;
            }
            _ = SendAsync($"Summarize this in a few bullet points:\n\n{clip}", display: "Summarize my clipboard");
            return;
        }
        Input.Text = text + ": ";
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        OnSend(sender, e);
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel(); // acts as Stop while streaming
            return;
        }
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        Input.Clear();
        _ = SendAsync(text);
    }

    private void OnNewChat(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _conversation.Clear();
        Messages.Children.Clear();
        ChatPanel.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        Input.Focus();
    }

    private async Task SendAsync(string prompt, string? display = null)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        _conversation.Add((true, prompt));
        Messages.Children.Add(UserBubble(display ?? prompt));

        var reply = new StackPanel { Margin = new Thickness(0, 6, 40, 6) };
        var thinking = ThinkingIndicator();
        reply.Children.Add(thinking);
        Messages.Children.Add(reply);
        ChatScroll.ScrollToEnd();

        _cts = new CancellationTokenSource();
        SendIcon.Kind = AppIcon.Stop;
        var text = "";
        try
        {
            await foreach (var chunk in AiService.StreamReplyAsync(_conversation, _cts.Token))
            {
                text += chunk;
                RenderReply(reply, text);
                ChatScroll.ScrollToEnd();
            }
        }
        catch (OperationCanceledException)
        {
            text += text.Length > 0 ? "\n\n(stopped)" : "(stopped)";
            RenderReply(reply, text);
        }
        catch (Exception ex)
        {
            text = FriendlyError(ex);
            RenderReply(reply, text, error: true);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SendIcon.Kind = AppIcon.ArrowUp;
        }

        if (text.Length > 0) _conversation.Add((false, text));
        else _conversation.RemoveAt(_conversation.Count - 1);
        ChatScroll.ScrollToEnd();
    }

    private static string FriendlyError(Exception ex)
    {
        var msg = ex.Message;
        if (ex is InvalidOperationException) return msg;
        if (msg.Contains("401") || msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "Your API key was rejected. Check it in Settings → Ask AI.";
        if (msg.Contains("429")) return "Rate limited — wait a moment and try again.";
        if (ex is System.Net.Http.HttpRequestException)
            return AiService.IsGemini ? "Couldn't reach Google. Check your connection." : "Couldn't reach Anthropic. Check your connection.";
        App.Log(ex);
        return "Something went wrong: " + msg;
    }

    // ---------------------------------------------------------------- rendering

    private FrameworkElement UserBubble(string text)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(80, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = Selectable(text, 11.5),
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        return bubble;
    }

    private FrameworkElement ThinkingIndicator()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new HeroIcon { Kind = AppIcon.Refresh, Width = 13, Height = 13, RenderTransformOrigin = new Point(0.5, 0.5) };
        var spin = new RotateTransform();
        icon.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        icon.SetResourceReference(ForegroundProperty, "SubTextBrush");
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = "Thinking…", Style = (Style)FindResource("SubText"), Margin = new Thickness(6, 0, 0, 0) });
        return panel;
    }

    /// <summary>Plain text with ``` fenced code blocks shown as monospace cards with a copy button.</summary>
    private void RenderReply(StackPanel host, string text, bool error = false)
    {
        host.Children.Clear();
        var parts = text.Split("```");
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i % 2 == 0)
            {
                var prose = part.Trim('\n');
                if (prose.Length == 0) continue;
                var tb = Selectable(prose.Replace("**", ""), 11.5);
                if (error) tb.SetResourceReference(ForegroundProperty, "BadBrush");
                host.Children.Add(tb);
            }
            else
            {
                var newline = part.IndexOf('\n');
                var code = (newline >= 0 ? part[(newline + 1)..] : part).TrimEnd('\n');
                host.Children.Add(CodeBlock(code));
            }
        }
    }

    private FrameworkElement CodeBlock(string code)
    {
        var grid = new Grid();
        var box = Selectable(code, 11);
        box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        box.Margin = new Thickness(0, 0, 24, 0);
        var copy = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 20,
            Height = 20,
            ToolTip = "Copy",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new HeroIcon { Kind = AppIcon.Copy, Width = 12, Height = 12 },
        };
        copy.SetResourceReference(ForegroundProperty, "SubTextBrush");
        copy.Click += (_, _) => ClipboardService.Instance.CopyQuiet(code);
        grid.Children.Add(box);
        grid.Children.Add(copy);
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 6, 7), Margin = new Thickness(0, 4, 0, 4), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return card;
    }

    /// <summary>Read-only TextBox so replies can be selected and copied.</summary>
    private static TextBox Selectable(string text, double size)
    {
        var box = new TextBox { Text = text, FontSize = size };
        box.SetResourceReference(StyleProperty, "BareText");
        return box;
    }
}
