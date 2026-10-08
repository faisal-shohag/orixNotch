using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;

namespace OrixNotch.Tools;

/// <summary>
/// Chat with Claude, ChatGPT, Gemini or DeepSeek: suggestion chips, streamed replies rendered as light Markdown
/// (headings, lists, bold, inline code, fenced code cards), and a copy button on every message.
/// </summary>
public partial class AskAiView : UserControl, IToolView
{
    private static readonly (string Text, AppIcon Icon)[] SuggestionItems =
    [
        ("Explain something simply", AppIcon.LightBulb),
        ("Summarize my clipboard", AppIcon.ClipboardList),
        ("Draft a short reply", AppIcon.Pencil),
    ];

    private static readonly Regex InlineToken = new(@"(`[^`\n]+`)|(\*\*[^*\n]+\*\*)|(__[^_\n]+__)|(\*[^*\s][^*\n]*?\*)", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.Compiled);

    /// <summary>One turn of the saved chat. <see cref="Display"/> is what the bubble showed when it differs from the prompt.</summary>
    public sealed record ChatEntry(bool FromUser, string Text, string? Display, DateTime At);

    private const string HistoryFile = "askai-chat.json";

    private readonly List<(bool FromUser, string Text)> _conversation = new();
    private List<ChatEntry> _history = new();
    private CancellationTokenSource? _cts;
    private string? _keySetupFor;
    /// <summary>Bumped by New chat, so a reply still streaming from the old chat knows to drop itself.</summary>
    private int _chatId;

    public AskAiView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        foreach (var (text, icon) in SuggestionItems) Suggestions.Children.Add(SuggestionChip(text, icon));
        RestoreHistory();
    }

    /// <summary>Brings back the last chat after a restart, so follow-up questions keep their context.</summary>
    private void RestoreHistory()
    {
        _history = Storage.Load<List<ChatEntry>>(HistoryFile);
        if (_history.Count == 0) return;
        foreach (var entry in _history)
        {
            _conversation.Add((entry.FromUser, entry.Text));
            if (entry.FromUser)
            {
                Messages.Children.Add(UserMessage(entry.Display ?? entry.Text));
                continue;
            }
            var (row, card, body, actions) = AssistantMessage();
            RevealCard(card, animate: false);
            RenderReply(body, entry.Text);
            ShowActions(actions, entry.Text, entry.At);
            Messages.Children.Add(row);
        }
        EmptyState.Visibility = Visibility.Collapsed;
        ChatScroll.Visibility = Visibility.Visible;
        NewChatButton.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void OnShown()
    {
        ApplyHeight();
        UpdateKeyState();
    }

    // ---------------------------------------------------------------- resize (same handle as the teleprompter)

    private const double DefaultHeight = 220;
    private const double MinChatHeight = 200;
    private const double MaxChatHeight = 520;
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;

    private static void ApplyHeight() =>
        NotchWindow.Instance?.SetToolSize(720, Math.Clamp(App.Settings.AskAiHeight, MinChatHeight, MaxChatHeight));

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Settings.AskAiHeight = DefaultHeight;
            ApplyHeight();
            SettingsService.Save(notify: false);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStartY = PointToScreen(e.GetPosition(this)).Y;
        _dragStartHeight = Math.Clamp(App.Settings.AskAiHeight, MinChatHeight, MaxChatHeight);
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var dy = (PointToScreen(e.GetPosition(this)).Y - _dragStartY) / dpi;
        App.Settings.AskAiHeight = Math.Clamp(_dragStartHeight + dy, MinChatHeight, MaxChatHeight);
        ApplyHeight();
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grip.ReleaseMouseCapture();
        SettingsService.Save(notify: false);
        if (ChatScroll.IsVisible) ChatScroll.ScrollToEnd();
    }

    public void OnHidden()
    {
    }

    private static string ProviderName => AiService.Active.Name;
    private static BrandLogo ProviderLogo => BrandLogos.ForProvider(AiService.ActiveProvider);

    /// <summary>No key yet for the active provider → show the key field instead of suggestions.</summary>
    private void UpdateKeyState()
    {
        var provider = AiService.ActiveProvider;
        var info = AiService.Info(provider);
        var hasKey = AiService.HasKeyFor(provider);
        EmptyLogo.Content = BrandLogos.Create(ProviderLogo, 17);
        EmptyTitle.Text = hasKey ? "How can I help?" : $"Connect {ProviderName}";
        EmptySub.Text = hasKey
            ? $"{info.Name} · messages go to {info.Company}'s API"
            : $"Paste your {info.Company} API key to start chatting.";
        KeyHint.Text = $"Get a key at {info.KeySource}. It's stored encrypted on this PC.";
        Input.Tag = $"Message {ProviderName}…";
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
                info.KeyPlaceholder,
                info.KeyLabel);
        if (!hasKey) _keySetupFor = provider;
        if (hasKey && _conversation.Count == 0) Input.Focus();
        UpdateSendState();
    }

    private void OnSuggestion(string text)
    {
        if (text == "Summarize my clipboard")
        {
            var clip = ClipboardService.Instance.Items.FirstOrDefault(i => i.Kind is ClipKind.Text or ClipKind.Link)?.Text;
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetInput("Summarize: ");
                return;
            }
            _ = SendAsync($"Summarize this in a few bullet points:\n\n{clip}", display: "Summarize my clipboard");
            return;
        }
        SetInput(text + ": ");
    }

    private void SetInput(string text)
    {
        Input.Text = text;
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        OnSend(sender, e);
    }

    /// <summary>Ctrl+N / Ctrl+L start a new chat from anywhere in the view.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.N or Key.L)) return;
        if (NewChatButton.Visibility != Visibility.Visible) return;
        e.Handled = true;
        OnNewChat(sender, e);
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdateSendState();

    /// <summary>Send is live with text to send; while streaming it is the Stop button.</summary>
    private void UpdateSendState()
    {
        var streaming = _cts is not null;
        SendButton.IsEnabled = streaming || (Input.IsEnabled && Input.Text.Trim().Length > 0);
        SendIcon.Kind = streaming ? AppIcon.Stop : AppIcon.ArrowUp;
        SendButton.ToolTip = streaming ? "Stop" : "Send (Enter)";
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
        _chatId++;
        _cts?.Cancel();
        _cts = null; // the old reply disposes its own source when it unwinds
        _conversation.Clear();
        _history.Clear();
        Storage.Save(HistoryFile, _history);
        Messages.Children.Clear();
        ChatScroll.Visibility = Visibility.Collapsed;
        NewChatButton.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        UpdateKeyState();
        Input.Focus();
    }

    private async Task SendAsync(string prompt, string? display = null)
    {
        var chatId = _chatId;
        EmptyState.Visibility = Visibility.Collapsed;
        ChatScroll.Visibility = Visibility.Visible;
        NewChatButton.Visibility = Visibility.Visible;
        _conversation.Add((true, prompt));
        Messages.Children.Add(UserMessage(display ?? prompt));

        var (row, card, body, actions) = AssistantMessage();
        body.Children.Add(StatusShimmer());
        Messages.Children.Add(row);
        ChatScroll.ScrollToEnd();

        var cts = _cts = new CancellationTokenSource();
        UpdateSendState();
        var text = "";
        var failed = false;
        try
        {
            await foreach (var chunk in AiService.StreamReplyAsync(_conversation, cts.Token))
            {
                text += chunk;
                if (text.Length > 0) RevealCard(card);
                RenderReply(body, text);
                ChatScroll.ScrollToEnd();
            }
        }
        catch (OperationCanceledException) when (chatId != _chatId)
        {
            // New chat was started mid-reply; this bubble is already gone.
        }
        catch (OperationCanceledException)
        {
            if (text.Length == 0) body.Children.Clear();
            body.Children.Add(Note("Stopped"));
        }
        catch (Exception ex)
        {
            failed = true;
            RevealCard(card);
            RenderReply(body, FriendlyError(ex), error: true);
        }
        finally
        {
            cts.Dispose();
            if (_cts == cts) _cts = null;
            UpdateSendState();
        }

        if (chatId != _chatId) return;
        if (text.Length > 0 && !failed)
        {
            _conversation.Add((false, text));
            var at = DateTime.Now;
            ShowActions(actions, text, at);
            _history.Add(new ChatEntry(true, prompt, display, at));
            _history.Add(new ChatEntry(false, text, null, at));
            Storage.Save(HistoryFile, _history);
        }
        else
        {
            _conversation.RemoveAt(_conversation.Count - 1);
        }
        ChatScroll.ScrollToEnd();
    }

    private static string FriendlyError(Exception ex)
    {
        var msg = ex.Message;
        if (ex is InvalidOperationException) return msg;
        if (msg.Contains("401") || msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "Your API key was rejected. Check it in Settings → Ask AI.";
        var company = AiService.Active.Company;
        // Out of credit: OpenAI says insufficient_quota (as a 429), DeepSeek returns 402, Anthropic "credit balance".
        if (msg.Contains("insufficient_quota") || msg.Contains("(402)") || msg.Contains("Insufficient Balance", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
            return $"Your {company} account is out of credit. Add funds in the {company} console.";
        if (msg.Contains("429")) return "Rate limited — wait a moment and try again.";
        if (msg.Contains("model_not_found") || msg.Contains("does not exist") || (msg.Contains("(404)") && msg.Contains("model")))
            return "That model isn't available for your key. Pick another in Settings → Ask AI.";
        if (msg.Contains("verified", StringComparison.OrdinalIgnoreCase) && msg.Contains("stream", StringComparison.OrdinalIgnoreCase))
            return $"{company} requires a verified organization to stream this model. Pick another model in Settings → Ask AI.";
        if (ex is System.Net.Http.HttpRequestException)
            return $"Couldn't reach {company}. Check your connection.";
        App.Log(ex);
        return "Something went wrong: " + msg;
    }

    // ---------------------------------------------------------------- message layout

    private FrameworkElement UserMessage(string text)
    {
        var row = new Grid { Margin = new Thickness(60, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = CopyButton(text, withLabel: false);
        copy.VerticalAlignment = VerticalAlignment.Center;
        copy.Margin = new Thickness(0, 0, 6, 0);
        RevealOnHover(row, copy);
        row.Children.Add(copy);

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(14, 14, 4, 14),
            Padding = new Thickness(12, 7, 12, 8),
            MaxWidth = 470,
            Child = Selectable(text),
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        Grid.SetColumn(bubble, 1);
        row.Children.Add(bubble);
        return row;
    }

    /// <summary>
    /// Avatar + reply card. While waiting, the card is invisible and only the shimmering status word
    /// shows; <see cref="RevealCard"/> gives it its grey background once text arrives. Copy and the
    /// time sit in the footer, which appears when the reply is complete.
    /// </summary>
    private (FrameworkElement Row, Border Card, StackPanel Body, Grid Actions) AssistantMessage()
    {
        var row = new Grid { Margin = new Thickness(0, 6, 40, 6), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            // Centered on the first line of text (card padding 10 + half an ~18px line − half the avatar),
            // so it lines up with the status word while waiting and with the reply after.
            Margin = new Thickness(0, 7, 10, 0),
            Child = BrandLogos.Create(ProviderLogo, 13),
        };
        ((FrameworkElement)avatar.Child).HorizontalAlignment = HorizontalAlignment.Center;
        avatar.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        row.Children.Add(avatar);

        var column = new StackPanel();
        var body = new StackPanel();
        column.Children.Add(body);
        var actions = new Grid { Margin = new Thickness(-7, 6, 0, -3), Visibility = Visibility.Collapsed };
        column.Children.Add(actions);

        var card = new Border
        {
            CornerRadius = new CornerRadius(4, 14, 14, 14), // the square corner points at the avatar
            Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, // hug the status word while waiting
            Child = column,
        };
        Grid.SetColumn(card, 1);
        row.Children.Add(card);
        return (row, card, body, actions);
    }

    /// <summary>First text arrived: give the card its grey background, with a quick fade.</summary>
    private static void RevealCard(Border card, bool animate = true)
    {
        if (card.HorizontalAlignment == HorizontalAlignment.Stretch) return;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        if (animate) card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>Footer of a finished reply: Copy on the left, the time it arrived on the right.</summary>
    private void ShowActions(Grid actions, string text, DateTime at)
    {
        actions.Children.Clear();
        var copy = CopyButton(text, withLabel: true);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        actions.Children.Add(copy);
        actions.Children.Add(new TextBlock
        {
            // Today's replies show just the time; older restored ones get the date too.
            Text = at.Date == DateTime.Today ? at.ToString("t") : at.ToString("d MMM, t"),
            Style = (Style)FindResource("SubText"),
            FontWeight = FontWeights.Normal,
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -7, 0), // the footer is pulled left to align Copy's icon
        });
        actions.Visibility = Visibility.Visible;
    }

    private static void RevealOnHover(FrameworkElement owner, UIElement target)
    {
        target.Opacity = owner.IsMouseOver ? 1 : 0;
        owner.MouseEnter += (_, _) => target.Opacity = 1;
        owner.MouseLeave += (_, _) => target.Opacity = 0;
    }

    /// <summary>Copy with a brief "Copied" check as feedback.</summary>
    private Button CopyButton(string text, bool withLabel)
    {
        var icon = new HeroIcon { Kind = AppIcon.Copy, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = "Copy", FontSize = 11, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(withLabel ? 7 : 0, 0, withLabel ? 8 : 0, 0) };
        content.Children.Add(icon);
        if (withLabel) content.Children.Add(label);

        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = withLabel ? double.NaN : 24,
            Height = 24,
            ToolTip = "Copy",
            Content = content,
        };
        button.SetResourceReference(ForegroundProperty, "SubTextBrush");

        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        reset.Tick += (_, _) =>
        {
            reset.Stop();
            icon.Kind = AppIcon.Copy;
            label.Text = "Copy";
            button.SetResourceReference(ForegroundProperty, "SubTextBrush");
        };
        button.Click += (_, _) =>
        {
            ClipboardService.Instance.CopyQuiet(text);
            icon.Kind = AppIcon.Check;
            label.Text = "Copied";
            button.SetResourceReference(ForegroundProperty, "AccentBrush");
            reset.Stop();
            reset.Start();
        };
        return button;
    }

    private SuggestionButton SuggestionChip(string text, AppIcon icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = new HeroIcon { Kind = icon, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var chip = new SuggestionButton { Content = content, Margin = new Thickness(3, 0, 3, 0) };
        chip.Click += (_, _) => OnSuggestion(text);
        return chip;
    }

    /// <summary>Pill-shaped chip: surface fill, hover lift.</summary>
    private sealed class SuggestionButton : Button
    {
        public SuggestionButton()
        {
            Cursor = Cursors.Hand;
            Focusable = false;
            var factory = new FrameworkElementFactory(typeof(Border), "Bd");
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            factory.SetValue(Border.PaddingProperty, new Thickness(11, 5, 12, 5));
            factory.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
            factory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBrush"), "Bd"));
            template.Triggers.Add(hover);
            Template = template;
            SetResourceReference(ForegroundProperty, "TextBrush");
        }
    }

    /// <summary>Waiting state: a random status word ("Razzmatazzing…") with a light band sweeping across it.</summary>
    private static FrameworkElement StatusShimmer()
    {
        var word = StatusWords[Random.Shared.Next(StatusWords.Length)] + "…";
        var label = new TextBlock { Text = word, FontSize = 12.5, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 1, 0, 1) };

        // A bright band sweeps across dimmed text: base → highlight → base, slid by the brush transform.
        var dim = ((SolidColorBrush)ThemeService.Get("SubTextBrush")).Color;
        var bright = ((SolidColorBrush)ThemeService.Get("TextBrush")).Color;
        var sweep = new TranslateTransform(0, 0);
        var shimmer = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = sweep,
            GradientStops =
            {
                new GradientStop(dim, 0),
                new GradientStop(dim, 0.35),
                new GradientStop(bright, 0.5),
                new GradientStop(dim, 0.65),
                new GradientStop(dim, 1),
            },
        };
        // Wrap the gradient so the band re-enters from the left as it leaves on the right; sliding by
        // exactly one width per cycle makes the loop seamless.
        shimmer.SpreadMethod = GradientSpreadMethod.Repeat;
        label.Foreground = shimmer;
        sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.5, 0.5, TimeSpan.FromMilliseconds(1500))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        });
        return label;
    }

    /// <summary>Status words for the waiting shimmer; one is picked at random per reply.</summary>
    private static readonly string[] StatusWords =
    [
        "Thinking", "Cooking", "Wrangling", "Fluttering", "Razzmatazzing", "Pondering", "Brewing", "Conjuring",
        "Noodling", "Percolating", "Simmering", "Mulling", "Ruminating", "Cogitating", "Deliberating", "Musing",
        "Scheming", "Tinkering", "Crafting", "Whittling", "Sculpting", "Forging", "Hatching", "Incubating",
        "Marinating", "Fermenting", "Steeping", "Baking", "Whisking", "Kneading", "Sautéing", "Stewing",
        "Toasting", "Flambéing", "Distilling", "Synthesizing", "Computing", "Calculating", "Crunching", "Churning",
        "Spinning", "Weaving", "Knitting", "Stitching", "Doodling", "Sketching", "Composing", "Orchestrating",
        "Harmonizing", "Riffing", "Jamming", "Improvising", "Choreographing", "Moonwalking", "Shimmying", "Boogieing",
        "Waltzing", "Twirling", "Pirouetting", "Frolicking", "Gallivanting", "Meandering", "Moseying", "Sauntering",
        "Zigzagging", "Spelunking", "Excavating", "Unearthing", "Sleuthing", "Deciphering", "Decoding", "Unraveling",
        "Untangling", "Puzzling", "Finagling", "Jiggering", "Fiddling", "Futzing", "Puttering", "Pottering",
        "Dillydallying", "Lollygagging", "Bamboozling", "Hornswoggling", "Discombobulating", "Flibbertigibbeting", "Kerfuffling", "Befuddling",
        "Bedazzling", "Glimmering", "Shimmering", "Sparkling", "Twinkling", "Glistening", "Effervescing", "Fizzing",
        "Bubbling", "Zesting", "Seasoning", "Garnishing", "Alchemizing", "Transmuting", "Manifesting", "Divining",
        "Channeling", "Beaming", "Booping", "Vibing", "Grooving", "Whooshing", "Ideating", "Envisioning",
        "Daydreaming", "Imagineering", "Brainstorming", "Cerebrating", "Contemplating", "Mind-melding", "Hypothesizing", "Extrapolating",
    ];

    private TextBlock Note(string text) =>
        new() { Text = text, Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };

    // ---------------------------------------------------------------- reply rendering

    /// <summary>Prose as formatted, selectable text; ``` fences as code cards with their own copy.</summary>
    private void RenderReply(StackPanel host, string text, bool error = false)
    {
        host.Children.Clear();
        var parts = text.Split("```");
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i % 2 == 0)
            {
                var prose = part.Trim('\n', '\r');
                if (prose.Length == 0) continue;
                var rich = Prose(prose);
                if (error) rich.SetResourceReference(ForegroundProperty, "BadBrush");
                host.Children.Add(rich);
            }
            else
            {
                var newline = part.IndexOf('\n');
                var language = newline > 0 ? part[..newline].Trim() : "";
                var code = (newline >= 0 ? part[(newline + 1)..] : part).TrimEnd('\n', '\r');
                host.Children.Add(CodeBlock(code, language));
            }
        }
    }

    private static RichTextBox Prose(string markdown)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = 12.5, LineHeight = 18 };
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "UiFont");
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");

        Paragraph? paragraph = null;
        List? list = null;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                paragraph = null;
                list = null;
                continue;
            }

            if (Heading.Match(line) is { Success: true } h)
            {
                list = null;
                var head = new Paragraph { Margin = new Thickness(0, doc.Blocks.Count > 0 ? 8 : 0, 0, 2), FontWeight = FontWeights.SemiBold, FontSize = 13.5 };
                AddInlines(head.Inlines, h.Groups[1].Value);
                doc.Blocks.Add(head);
                paragraph = null;
                continue;
            }

            var bullet = Bullet.Match(line);
            var numbered = bullet.Success ? Match.Empty : Numbered.Match(line);
            if (bullet.Success || numbered.Success)
            {
                var marker = bullet.Success ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal;
                if (list is null || list.MarkerStyle != marker)
                {
                    list = new List { MarkerStyle = marker, Margin = new Thickness(0, doc.Blocks.Count > 0 ? 4 : 0, 0, 4), Padding = new Thickness(18, 0, 0, 0) };
                    doc.Blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(item.Inlines, (bullet.Success ? bullet : numbered).Groups[1].Value);
                list.ListItems.Add(new ListItem(item));
                paragraph = null;
                continue;
            }

            list = null;
            if (paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, doc.Blocks.Count > 0 ? 6 : 0, 0, 0) };
                doc.Blocks.Add(paragraph);
            }
            else
            {
                paragraph.Inlines.Add(new LineBreak());
            }
            AddInlines(paragraph.Inlines, line);
        }

        var box = new RichTextBox { Document = doc };
        box.SetResourceReference(StyleProperty, "BareRich");
        // The editor insets text by 5px; pull it back so prose lines up with code cards and the name.
        box.Margin = new Thickness(-5, 0, 0, 0);
        return box;
    }

    /// <summary>**bold**, __bold__, *italic* and `code` spans.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        var at = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text[at..m.Index]));
            var token = m.Value;
            if (m.Groups[1].Success)
            {
                var code = new Run(" " + token[1..^1] + " ") { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5 };
                code.SetResourceReference(TextElement.BackgroundProperty, "Surface2Brush");
                inlines.Add(code);
            }
            else if (m.Groups[2].Success || m.Groups[3].Success)
            {
                inlines.Add(new Bold(new Run(token[2..^2])));
            }
            else
            {
                inlines.Add(new Italic(new Run(token[1..^1])));
            }
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    private const double CodeFontSize = 11.5;
    private const double CodeLineHeight = 17;
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Cascadia Code, Consolas");

    /// <summary>
    /// Code card: language + copy in a header strip, then line numbers beside syntax-highlighted,
    /// selectable code. Long lines scroll sideways instead of wrapping, so indentation stays readable.
    /// </summary>
    private FrameworkElement CodeBlock(string code, string language)
    {
        var header = new Grid { Margin = new Thickness(12, 4, 5, 4) };
        header.Children.Add(new TextBlock
        {
            Text = CodeHighlighter.DisplayName(language),
            Style = (Style)FindResource("SubText"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var copy = CopyButton(code, withLabel: true);
        copy.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(copy);

        var lines = code.Replace("\r", "").Split('\n');
        var body = new Grid { Margin = new Thickness(0, 8, 0, 10) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (lines.Length > 1)
        {
            var gutter = new TextBlock
            {
                Text = string.Join("\n", Enumerable.Range(1, lines.Length)),
                FontFamily = CodeFont,
                FontSize = CodeFontSize,
                LineHeight = CodeLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0),
                Opacity = 0.55,
                IsHitTestVisible = false, // keep line numbers out of selections
            };
            gutter.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            body.Children.Add(gutter);
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = HighlightedCode(lines, language),
            Margin = new Thickness(lines.Length > 1 ? 14 : 12, 0, 12, 0),
        };
        Grid.SetColumn(scroller, 1);
        body.Children.Add(scroller);

        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(divider);
        stack.Children.Add(body);
        var card = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 6, 0, 6), Child = stack, BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PillBrush"); // darker than the reply card it sits in
        card.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return card;
    }

    /// <summary>Selectable, colored code. The page is as wide as the longest line, so nothing wraps.</summary>
    private RichTextBox HighlightedCode(string[] lines, string language)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var (text, kind) in CodeHighlighter.Tokenize(string.Join("\n", lines), language))
        {
            // Runs can't hold line breaks: split on newlines and insert LineBreaks.
            var parts = text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                if (parts[i].Length == 0) continue;
                var run = new Run(parts[i]);
                var brush = kind switch
                {
                    CodeToken.Keyword => "CodeKeywordBrush",
                    CodeToken.String => "CodeStringBrush",
                    CodeToken.Comment => "CodeCommentBrush",
                    CodeToken.Number => "CodeNumberBrush",
                    CodeToken.Function => "CodeFunctionBrush",
                    CodeToken.Type => "CodeTypeBrush",
                    CodeToken.Property => "CodePropertyBrush",
                    CodeToken.Variable => "CodeVariableBrush",
                    CodeToken.Tag => "CodeTagBrush",
                    _ => null,
                };
                if (brush is not null) run.SetResourceReference(TextElement.ForegroundProperty, brush);
                if (kind == CodeToken.Comment) run.FontStyle = FontStyles.Italic;
                paragraph.Inlines.Add(run);
            }
        }

        var longest = lines.Length == 0 ? "" : lines.MaxBy(l => l.Length)!;
        var measured = new FormattedText(longest.Replace("\t", "    "), System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(CodeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            CodeFontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var doc = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = CodeFont,
            FontSize = CodeFontSize,
            LineHeight = CodeLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        // Inside a sideways ScrollViewer the box gets infinite width and collapses; size it to the code.
        var width = Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + 16;
        doc.PageWidth = width;
        var box = new RichTextBox { Document = doc, HorizontalAlignment = HorizontalAlignment.Left, Width = width };
        box.SetResourceReference(StyleProperty, "BareRich");
        box.Margin = new Thickness(-5, 0, 0, 0); // the editor insets text by 5px; line up with the gutter
        return box;
    }

    /// <summary>Read-only TextBox so user messages and code can be selected and copied.</summary>
    private static TextBox Selectable(string text, double size = 12.5)
    {
        var box = new TextBox { Text = text, FontSize = size };
        box.SetResourceReference(StyleProperty, "BareText");
        return box;
    }
}
