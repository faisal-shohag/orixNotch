using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrixNotch.Services;
using OrixNotch.Shell;
using Forms = System.Windows.Forms;

namespace OrixNotch.Settings;

/// <summary>
/// Settings shown inside the notch: sidebar on the left, one page of rows on the right.
/// Every row is "title + one-line description" with its control on the right.
/// </summary>
public partial class SettingsView : UserControl, IToolView
{
    private static readonly (string Id, string Name, AppIcon Icon, string? Section)[] Pages =
    [
        ("general", "General", AppIcon.Adjust, null),
        ("appearance", "Appearance", AppIcon.Palette, null),
        ("display", "Display", AppIcon.Monitor, "NOTCH"),
        ("tabs", "Tabs", AppIcon.Grid, null),
        ("keyboard", "Keyboard", AppIcon.Keyboard, null),
        ("ai", "Ask AI", AppIcon.Sparkles, "TOOLS"),
        ("data", "Clipboard & Shelf", AppIcon.ClipboardList, null),
        ("about", "About", AppIcon.Info, ""),
    ];

    private string _page = "general";
    private string? _recordingAction;

    private static AppSettings S => App.Settings;

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = $"OrixNotch {typeof(App).Assembly.GetName().Version?.ToString(3)}";

        foreach (var (id, name, icon, section) in Pages)
        {
            if (section is not null)
                Nav.Children.Add(new TextBlock
                {
                    Text = section,
                    Style = (Style)FindResource("Caps"),
                    FontSize = 10.5,
                    Margin = new Thickness(10, section.Length == 0 ? 8 : 14, 0, 4),
                });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new HeroIcon { Kind = icon, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var nav = new RadioButton
            {
                Style = (Style)Resources["NavButton"],
                Content = content,
                GroupName = "settingsNav",
                IsChecked = id == _page,
            };
            var pageId = id;
            nav.Checked += (_, _) =>
            {
                if (_page == pageId) return;
                _page = pageId;
                Render(resetScroll: true);
            };
            Nav.Children.Add(nav);
        }

        ThemeService.Changed += () =>
        {
            if (_page == "appearance") Render();
        };
        PreviewKeyDown += OnRecordKey;
        Render();
    }

    public void OnShown() => Render();

    public void OnHidden() => _recordingAction = null;

    // ---------------------------------------------------------------- page rendering

    private void Render(bool resetScroll = false)
    {
        // Rebuilding the page clears its children, which collapses the scroll extent and
        // drops the offset to 0. Preserve it so toggles / restore / hotkey edits don't
        // yank the user back to the top. Only page switches pass resetScroll: true.
        var offset = resetScroll ? 0 : PageScroll.VerticalOffset;
        Page.Children.Clear();
        if (resetScroll) PageScroll.ScrollToTop();
        switch (_page)
        {
            case "general": RenderGeneral(); break;
            case "appearance": RenderAppearance(); break;
            case "display": RenderDisplay(); break;
            case "tabs": RenderTabs(); break;
            case "keyboard": RenderKeyboard(); break;
            case "ai": RenderAi(); break;
            case "data": RenderData(); break;
            case "about": RenderAbout(); break;
        }
        if (!resetScroll && offset > 0)
            Dispatcher.BeginInvoke(() => PageScroll.ScrollToVerticalOffset(offset),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RenderGeneral()
    {
        Title("General", "How OrixNotch behaves on your PC.");
        Section("PROFILE");
        Row("Name", "Shown in the greeting on Home.",
            TextInput(App.Settings.DisplayName, Environment.UserName, ProfileService.SetName));
        Row("Picture", ProfileService.HasAvatar ? "Shown next to the greeting. Click Home's avatar to change it too." : "Pick a photo for Home; otherwise your initial is shown.",
            AvatarPicker());

        Section("BEHAVIOR");
        Row("Open notch", "Open when the pointer rests on the notch, or only when you click it.",
            Combo(["On Hover", "On Click"], (int)S.OpenMode, i => { S.OpenMode = (OpenMode)i; Save(); }));
        Row("Hover delay", "How long the pointer rests on the notch before it opens.",
            Combo(["Instant", "Fast", "Normal", "Slow"], Array.IndexOf(new[] { 0, 80, 150, 400 }, S.HoverDelayMs) is var d and >= 0 ? d : 2,
                i => { S.HoverDelayMs = new[] { 0, 80, 150, 400 }[i]; Save(); }));
        Row("Hide in full screen", "Stay out of the way of full-screen apps, games and videos.",
            Toggle(S.HideInFullscreen, v => { S.HideInFullscreen = v; Save(); }));

        Section("SYSTEM");
        Row("Open at login", "Start OrixNotch when you sign in to Windows.",
            Toggle(S.LaunchAtStartup, v => { S.LaunchAtStartup = v; StartupService.Apply(v); Save(); }));

        Section("NOW PLAYING");
        Row("Lyrics", "Show time-synced lyrics on Now Playing. Songs are looked up on LRCLIB, a free lyrics library.",
            Toggle(S.LyricsEnabled, v => { S.LyricsEnabled = v; Save(false); }));
    }

    private void RenderAppearance()
    {
        Title("Appearance", "Color scheme and accent. Changes apply instantly.");
        Section("COLOR SCHEME");
        var schemes = new WrapPanel();
        foreach (var scheme in ThemeService.Schemes) schemes.Children.Add(SchemeCard(scheme));
        Page.Children.Add(schemes);

        Section("ACCENT");
        var accents = new WrapPanel();
        foreach (var accent in ThemeService.Accents) accents.Children.Add(AccentSwatch(accent));
        Page.Children.Add(accents);

        Section("UNITS");
        Row("Fahrenheit & mph", "Weather units.", Toggle(S.UseFahrenheit, v => { S.UseFahrenheit = v; Save(false); }));
    }

    private void RenderDisplay()
    {
        Title("Display", "Where the notch appears, and how big it is.");
        Section("LOCATION");
        var screens = Forms.Screen.AllScreens;
        var names = new List<string> { "Primary display" };
        for (var i = 0; i < screens.Length; i++)
            names.Add($"Display {i + 1} · {screens[i].Bounds.Width}×{screens[i].Bounds.Height}{(screens[i].Primary ? " (primary)" : "")}");
        Row("Show notch on", "Which monitor hosts the notch.",
            Combo(names, S.MonitorIndex >= 0 && S.MonitorIndex < screens.Length ? S.MonitorIndex + 1 : 0,
                i => { S.MonitorIndex = i - 1; Save(); }, 210));

        Section("SIZE");
        var sizes = new[] { "Small", "Default", "Large", "ExtraLarge" };
        Row("Notch size", "Size of the closed notch. Larger is easier to hit on big monitors.",
            Combo(["Small", "Default", "Large", "Extra Large"], Math.Max(0, Array.IndexOf(sizes, S.NotchSize)),
                i => { S.NotchSize = sizes[i]; Save(); }));
    }

    private void RenderTabs()
    {
        Title("Tabs", "The tools in the notch's bottom bar, and their order.");
        Section("IN THE NOTCH");
        var visible = ToolRegistry.Ordered();
        var list = new StackPanel();
        for (var i = 0; i < visible.Count; i++) list.Children.Add(ToolRow(visible[i], true, i, visible.Count));
        Page.Children.Add(Group(list));
        Page.Children.Add(Hint("Use the arrows to reorder. Settings always stays at the end of the bar."));

        var hidden = ToolRegistry.Ordered(includeHidden: true).Where(t => S.HiddenTools.Contains(t.Id)).ToList();
        if (hidden.Count > 0)
        {
            Section("MORE TOOLS");
            var more = new StackPanel();
            foreach (var tool in hidden) more.Children.Add(ToolRow(tool, false, -1, 0));
            Page.Children.Add(Group(more));
        }

        var restore = new Button { Content = "Restore defaults", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        restore.Click += (_, _) =>
        {
            S.ToolOrder = [];
            S.HiddenTools = [];
            Save();
            Render();
        };
        Page.Children.Add(restore);
    }

    private FrameworkElement ToolRow(ToolDef tool, bool shown, int index, int count)
    {
        var row = new Grid { Height = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
            Child = new HeroIcon { Kind = tool.Icon, Width = 13, Height = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        icon.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        var label = new TextBlock { Text = tool.Name, FontSize = 13.5, FontWeight = FontWeights.Medium, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);

        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        if (shown)
        {
            arrows.Children.Add(IconButton(AppIcon.ChevronUp, "Move up", index > 0, () => Move(tool.Id, -1)));
            arrows.Children.Add(IconButton(AppIcon.ChevronDown, "Move down", index < count - 1, () => Move(tool.Id, 1)));
        }
        Grid.SetColumn(arrows, 2);

        var toggle = Toggle(shown, v =>
        {
            if (v) S.HiddenTools.Remove(tool.Id);
            else if (!S.HiddenTools.Contains(tool.Id) && ToolRegistry.Ordered().Count > 1) S.HiddenTools.Add(tool.Id);
            Save();
            Dispatcher.BeginInvoke(Render);
        });
        Grid.SetColumn(toggle, 3);

        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(arrows);
        row.Children.Add(toggle);
        return row;
    }

    private void Move(string id, int delta)
    {
        var ids = ToolRegistry.Ordered(includeHidden: true).Select(t => t.Id).ToList();
        var visible = ToolRegistry.Ordered().Select(t => t.Id).ToList();
        var vi = visible.IndexOf(id);
        var target = vi + delta;
        if (vi < 0 || target < 0 || target >= visible.Count) return;
        var a = ids.IndexOf(id);
        var b = ids.IndexOf(visible[target]);
        (ids[a], ids[b]) = (ids[b], ids[a]);
        S.ToolOrder = ids;
        Save();
        Render();
    }

    private void RenderKeyboard()
    {
        Title("Keyboard", "Global shortcuts work from any app. Click a shortcut, then press the new keys.");
        Section("SHORTCUTS");
        foreach (var action in HotkeyService.Actions)
        {
            S.Hotkeys.TryGetValue(action.Id, out var gesture);
            var recording = _recordingAction == action.Id;
            var chip = new Button
            {
                Content = recording ? "Press keys…" : string.IsNullOrEmpty(gesture) ? "Not set" : gesture.Replace("+", " + "),
                MinWidth = 110,
            };
            if (recording) chip.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
            var id = action.Id;
            chip.Click += (_, _) =>
            {
                _recordingAction = id;
                Focusable = true;
                Focus();
                Render();
            };
            var clear = IconButton(AppIcon.XMark, "Remove shortcut", !string.IsNullOrEmpty(gesture), () =>
            {
                S.Hotkeys[id] = "";
                SaveHotkeys();
            });
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(chip);
            controls.Children.Add(clear);
            Row(action.Name, action.Description, controls);
        }

        Section("INSIDE THE NOTCH");
        Row("Switch tabs", "1–9 jump to a tab; ← and → step through tabs.", Badge("1 – 9  ·  ← →"));
        Row("Close", "Esc closes the notch (or goes back from a sub-page).", Badge("Esc"));
        Row("Teleprompter", "Space plays or pauses the script.", Badge("Space"));
    }

    private void OnRecordKey(object sender, KeyEventArgs e)
    {
        if (_recordingAction is null) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _recordingAction = null;
            Render();
            return;
        }
        var gesture = HotkeyService.FromKeyEvent(e);
        if (gesture is null) return; // still holding modifiers
        S.Hotkeys[_recordingAction] = gesture;
        _recordingAction = null;
        SaveHotkeys();
    }

    private void SaveHotkeys()
    {
        Save(false);
        var failed = HotkeyService.RegisterAll();
        Render();
        if (failed.Count > 0)
            Page.Children.Insert(2, Hint($"Another app already uses: {string.Join(", ", failed.Select(f => HotkeyService.Actions.First(a => a.Id == f).Name))}. Pick a different combination.", bad: true));
    }

    private void RenderAi()
    {
        var gemini = S.AiProvider == AiService.ProviderGemini;
        Title("Ask AI", gemini
            ? "Chat with Gemini from the notch. Messages are sent to Google's API using your own key."
            : "Chat with Claude from the notch. Messages are sent to Anthropic's API using your own key.");
        Section("ACCOUNT");
        Row("Claude API key", AiService.HasKey
                ? "Saved and encrypted for your Windows account. Paste a new key to replace it."
                : "Paste your key from console.anthropic.com. It's stored encrypted on this PC.",
            ApiKeyInput(() => AiService.HasKey, AiService.SaveKey, () => Render(),
                "sk-ant-…", "Anthropic API key (sk-ant-…)"));
        if (AiService.HasKey)
        {
            var remove = new Button { Content = "Remove key" };
            remove.Click += (_, _) =>
            {
                AiService.SaveKey("");
                Render();
            };
            Row("Forget Claude key", "Delete the saved Anthropic key from this PC.", remove);
        }
        Row("Gemini API key", AiService.HasGeminiKey
                ? "Saved and encrypted for your Windows account. Paste a new key to replace it."
                : "Paste your key from aistudio.google.com. It's stored encrypted on this PC.",
            ApiKeyInput(() => AiService.HasGeminiKey, AiService.SaveGeminiKey, () => Render(),
                "AIza…", "Google AI Studio API key (AIza…)"));
        if (AiService.HasGeminiKey)
        {
            var removeGemini = new Button { Content = "Remove key" };
            removeGemini.Click += (_, _) =>
            {
                AiService.SaveGeminiKey("");
                Render();
            };
            Row("Forget Gemini key", "Delete the saved Google key from this PC.", removeGemini);
        }

        Section("MODEL");
        Row("Provider", "Which AI answers in the notch.",
            Combo(["Claude", "Gemini"], gemini ? 1 : 0, i =>
            {
                S.AiProvider = i == 1 ? AiService.ProviderGemini : AiService.ProviderAnthropic;
                S.AiModel = AiService.DefaultModelFor(S.AiProvider);
                Save(false);
                Render();
            }));
        var models = AiService.ModelsFor(S.AiProvider);
        Row("Model", gemini
                ? "Flash is fast and cheap; Pro is the most capable."
                : "Opus is the most capable; Sonnet and Haiku are faster and cheaper.",
            Combo(AiService.LabelsFor(S.AiProvider), Math.Max(0, Array.IndexOf(models, S.AiModel)),
                i => { S.AiModel = models[i]; Save(false); }));

        Section("AI USAGE");
        Row("Claude session limit", "Tokens per 5-hour session, to show “% used”. 0 hides the bar.",
            NumberInput(S.ClaudeSessionTokenLimit, v => { S.ClaudeSessionTokenLimit = v; Save(false); }));
        Row("Claude weekly limit", "Tokens per 7 days. 0 hides the bar.",
            NumberInput(S.ClaudeWeeklyTokenLimit, v => { S.ClaudeWeeklyTokenLimit = v; Save(false); }));
    }

    private void RenderData()
    {
        Title("Clipboard & Shelf", "History and dropped files are stored only on this PC.");
        Section("CLIPBOARD");
        Row("Record history", "Keep a list of what you copy.", Toggle(S.ClipboardEnabled, v => { S.ClipboardEnabled = v; Save(false); }));
        Row("Skip private clips", "Ignore copies that password managers mark as sensitive.",
            Toggle(S.ClipboardSkipSensitive, v => { S.ClipboardSkipSensitive = v; Save(false); }));
        var limits = new[] { 50, 100, 200, 500 };
        Row("Keep up to", "Older unpinned clips are removed.",
            Combo(["50 items", "100 items", "200 items", "500 items"], Math.Max(0, Array.IndexOf(limits, S.ClipboardMaxItems)),
                i => { S.ClipboardMaxItems = limits[i]; Save(false); }));
        var clear = new Button { Content = "Clear history" };
        clear.Click += (_, _) => ClipboardService.Instance.Clear();
        Row("Clear history", "Pinned clips are kept.", clear);

        Section("SHELF");
        var days = new[] { 1, 3, 7, 30, 0 };
        Row("Remove items after", "Files you drop on the notch are forgotten after this long.",
            Combo(["1 day", "3 days", "7 days", "30 days", "Never"], Math.Max(0, Array.IndexOf(days, S.ShelfRetentionDays)),
                i => { S.ShelfRetentionDays = days[i]; Save(false); }));

        Section("TIMERS");
        var breaks = new[] { 3, 5, 10, 15 };
        Row("Break length", "Pomodoro break after each focus session.",
            Combo(["3 minutes", "5 minutes", "10 minutes", "15 minutes"], Math.Max(0, Array.IndexOf(breaks, S.PomodoroBreakMin)),
                i => { S.PomodoroBreakMin = breaks[i]; Save(false); }));
    }

    private void RenderAbout()
    {
        Title("About", $"OrixNotch {typeof(App).Assembly.GetName().Version?.ToString(3)}");
        Section("PRIVACY");
        Page.Children.Add(Group(new TextBlock
        {
             Text = "Everything stays on this PC. Network is used only for weather (Open-Meteo), stocks (Yahoo Finance), " +
                   "lyrics (LRCLIB) and Ask AI (Anthropic or Google), and only while those tools are in use.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            LineHeight = 17,
            Margin = new Thickness(0, 8, 0, 8),
        }));
        var data = new Button { Content = "Open" };
        data.Click += (_, _) => Process.Start(new ProcessStartInfo(Storage.Root) { UseShellExecute = true });
        Row("Data folder", "Settings, notes, history and logs.", data);

        var quit = new Button { Content = "Quit OrixNotch", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        quit.SetResourceReference(ForegroundProperty, "BadBrush");
        quit.Click += (_, _) => Application.Current.Shutdown();
        Page.Children.Add(quit);
    }

    // ---------------------------------------------------------------- building blocks

    private void Title(string title, string subtitle)
    {
        Page.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold });
        Page.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("SubText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
    }

    private void Section(string text) =>
        Page.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Caps"), FontSize = 11, Margin = new Thickness(4, 20, 0, 8) });

    private static Border Group(UIElement child)
    {
        var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 2, 14, 2), Child = child };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return border;
    }

    private TextBlock Hint(string text, bool bad = false)
    {
        var tb = new TextBlock { Text = text, Style = (Style)FindResource("SubText"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
        if (bad) tb.SetResourceReference(TextBlock.ForegroundProperty, "BadBrush");
        return tb;
    }

    /// <summary>Settings row: title + description on the left, control on the right. Consecutive rows share a card.</summary>
    private void Row(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 11), MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("SubText"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);

        // Append to the previous card when the last element is one, so related rows group together.
        if (Page.Children.Count > 0 && Page.Children[^1] is Border { Child: StackPanel rows, Tag: "rows" })
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
            rows.Children.Add(line);
            rows.Children.Add(grid);
            return;
        }
        var stack = new StackPanel();
        stack.Children.Add(grid);
        var card = Group(stack);
        card.Tag = "rows";
        Page.Children.Add(card);
    }

    private static CheckBox Toggle(bool value, Action<bool> changed)
    {
        var toggle = new CheckBox { IsChecked = value };
        toggle.SetResourceReference(StyleProperty, "ToggleSwitch");
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed, double width = 140)
    {
        var box = new ComboBox { Width = width };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) changed(box.SelectedIndex);
        };
        return box;
    }

    private static TextBox TextInput(string value, string placeholder, Action<string> changed)
    {
        var box = new TextBox { Text = value, Tag = placeholder, Width = 200 };
        var pause = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        pause.Tick += (_, _) =>
        {
            pause.Stop();
            changed(box.Text.Trim());
        };
        box.TextChanged += (_, _) =>
        {
            pause.Stop();
            pause.Start();
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!pause.IsEnabled) return;
            pause.Stop();
            changed(box.Text.Trim());
        };
        return box;
    }

    /// <summary>Password field with a placeholder + Save button; Enter also saves.</summary>
    public static FrameworkElement ApiKeyInput(Func<bool> hasKey, Action<string> saveKey, Action saved,
        string placeholder, string tooltip)
    {
        var box = new PasswordBox { Width = 240, ToolTip = tooltip };
        var hint = new TextBlock
        {
            Text = hasKey() ? "••••••••  saved" : placeholder,
            IsHitTestVisible = false,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var field = new Grid();
        field.Children.Add(box);
        field.Children.Add(hint);

        var save = new Button { Content = "Save", Height = 32, Margin = new Thickness(6, 0, 0, 0) };
        save.SetResourceReference(StyleProperty, "AccentButton");
        void Commit()
        {
            var key = box.Password.Trim();
            if (key.Length == 0) return;
            saveKey(key);
            box.Clear();
            saved();
        }
        save.Click += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(field);
        panel.Children.Add(save);
        return panel;
    }

    private FrameworkElement AvatarPicker()
    {
        var preview = new Grid { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
        var circle = new System.Windows.Shapes.Ellipse();
        circle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        preview.Children.Add(circle);
        var initial = new TextBlock
        {
            Text = ProfileService.FirstName[..1].ToUpper(),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        initial.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        preview.Children.Add(initial);
        if (ProfileService.LoadAvatar() is { } image)
            preview.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } });

        var choose = new Button { Content = ProfileService.HasAvatar ? "Change…" : "Choose photo…" };
        choose.Click += (_, _) =>
        {
            using (NotchWindow.Instance?.HoldOpen()) ProfileService.PickAvatar(NotchWindow.Instance);
            Render();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(preview);
        panel.Children.Add(choose);
        if (ProfileService.HasAvatar)
        {
            var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                ProfileService.RemoveAvatar();
                Render();
            };
            panel.Children.Add(remove);
        }
        return panel;
    }

    private static TextBox NumberInput(long value, Action<long> changed)
    {
        var box = new TextBox { Text = value.ToString(), Tag = "0", Width = 120 };
        box.LostFocus += (_, _) =>
        {
            if (long.TryParse(box.Text.Replace(",", "").Trim(), out var v) && v >= 0) changed(v);
            else box.Text = value.ToString();
        };
        return box;
    }

    private Border Badge(string text)
    {
        var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Medium } };
        badge.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        return badge;
    }

    private Button IconButton(AppIcon kind, string tip, bool enabled, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 22,
            Height = 22,
            ToolTip = tip,
            IsEnabled = enabled,
            Content = new HeroIcon { Kind = kind, Width = 11, Height = 11 },
        };
        button.SetResourceReference(ForegroundProperty, "SubTextBrush");
        button.Click += (_, _) => click();
        return button;
    }

    private FrameworkElement SchemeCard(ColorScheme scheme)
    {
        var selected = scheme.Id == ThemeService.CurrentScheme.Id;
        var preview = new Grid { Height = 58, Width = 104 };
        preview.Children.Add(new Border { Background = new SolidColorBrush(scheme.Surface), CornerRadius = new CornerRadius(6) });
        preview.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 A3,3 0 0 1 3,3 L3,8 A6,6 0 0 0 9,14 L35,14 A6,6 0 0 0 41,8 L41,3 A3,3 0 0 1 44,0 Z"),
            Fill = new SolidColorBrush(scheme.Pill),
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, scheme.Text.R, scheme.Text.G, scheme.Text.B)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        });
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 8, 7) };
        lines.Children.Add(new Border { Height = 4, Width = 38, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(scheme.Text), HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(new Border { Height = 4, Width = 24, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ThemeService.CurrentAccent.Color), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) });
        preview.Children.Add(lines);

        var frame = new Border { Child = preview, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Padding = new Thickness(1) };
        if (selected) frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else frame.BorderBrush = Brushes.Transparent;

        var card = new StackPanel { Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        card.Children.Add(frame);
        card.Children.Add(new TextBlock { Text = scheme.Name, FontSize = 10.5, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        card.MouseLeftButtonUp += (_, _) => ThemeService.SetScheme(scheme.Id);
        return card;
    }

    private FrameworkElement AccentSwatch(AccentColor accent)
    {
        var selected = accent.Id == ThemeService.CurrentAccent.Id;
        var swatch = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = accent.Name };
        swatch.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = selected ? new SolidColorBrush(accent.Color) : Brushes.Transparent, StrokeThickness = 2 });
        swatch.Children.Add(new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(accent.Color) });
        swatch.MouseLeftButtonUp += (_, _) => ThemeService.SetAccent(accent.Id);
        return swatch;
    }

    private static void Save(bool notify = true) => SettingsService.Save(notify);
}
