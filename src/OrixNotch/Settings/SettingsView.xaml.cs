using System.Diagnostics;
using System.Globalization;
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
        Row("Dodge the pointer", "The closed notch dodges the pointer from the sides. Catch it from below.",
            Toggle(S.RepelCursor, v => { S.RepelCursor = v; Save(); }));

        Section("SYSTEM");
        Row("Open at login", StartupService.DisabledByUser
                ? "Turned off in Task Manager → Startup apps. Turn it back on there."
                : "Start OrixNotch when you sign in to Windows.",
            Toggle(S.LaunchAtStartup, async v =>
            {
                var on = await StartupService.ApplyAsync(v);
                S.LaunchAtStartup = on;
                Save();
                if (on != v) Render(); // Windows refused (e.g. disabled in Task Manager): show why
            }));

        Section("NOW PLAYING");
        Row("Lyrics", "Show time-synced lyrics on Now Playing. Songs are looked up on LRCLIB, a free lyrics library.",
            Toggle(S.LyricsEnabled, v => { S.LyricsEnabled = v; Save(false); }));

        Section("CALENDAR REMINDERS");
        Row("Event reminders", "Show upcoming calendar events on the closed notch.",
            Toggle(S.RemindersEnabled, v => { S.RemindersEnabled = v; Save(false); EventReminderService.Instance.Recheck(); }));
        var leads = new[] { 5, 10, 15, 30, 60 };
        Row("Remind me", "How long before an event the reminder appears.",
            Combo(["5 minutes before", "10 minutes before", "15 minutes before", "30 minutes before", "1 hour before"],
                Math.Max(0, Array.IndexOf(leads, S.ReminderLeadMin)),
                i => { S.ReminderLeadMin = leads[i]; Save(false); EventReminderService.Instance.Recheck(); }, width: 170));
        var lingers = new[] { 1, 5, 10, 15 };
        Row("Keep showing", "How long the reminder stays once the event has started.",
            Combo(["1 minute", "5 minutes", "10 minutes", "15 minutes"],
                Math.Max(0, Array.IndexOf(lingers, S.ReminderLingerMin)),
                i => { S.ReminderLingerMin = lingers[i]; Save(false); EventReminderService.Instance.Recheck(); }));
        Row("Sound", "Plays when a reminder appears and when the event starts.",
            Combo(EventReminderService.SoundNames, Math.Max(0, Array.IndexOf(EventReminderService.SoundNames, S.ReminderSound)),
                i =>
                {
                    S.ReminderSound = EventReminderService.SoundNames[i];
                    Save(false);
                    EventReminderService.PlaySound(S.ReminderSound); // preview
                }));

        Section("NOTIFICATIONS");
        var watcher = NotificationWatcher.Instance;
        var source = watcher.Source == NotificationWatcher.SourceKind.Listener
            ? "Uses Windows notification access."
            : "Reads Windows' notification history on this PC (read-only).";
        Row("Show on the notch", $"Windows notifications from other apps pop out of the closed notch. {source}",
            Toggle(S.NotifyEnabled, async v =>
            {
                S.NotifyEnabled = v;
                Save(false);
                if (v) await watcher.RequestAccessAsync();
                watcher.Reconfigure();
                Render();
            }));
        if (watcher.Problem is { } problem)
            Row("Not receiving notifications", problem, new Button { Content = "Open settings" }.Also(b =>
                b.Click += (_, _) => Process.Start(new ProcessStartInfo(watcher.ProblemSettingsUri) { UseShellExecute = true })));
        Row("Show message text", "Off shows only the app name, for privacy when sharing your screen.",
            Toggle(S.NotifyShowText, v => { S.NotifyShowText = v; Save(false); }));
        Row("Windows banners", "Windows still shows its own pop-up too. Turn banners off per app in Windows Settings if you only want the notch.",
            new Button { Content = "Open settings" }.Also(b =>
                b.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true })));

        if (S.NotifySeenApps.Count > 0)
        {
            Section("SHOW NOTIFICATIONS FROM");
            foreach (var entry in S.NotifySeenApps)
            {
                var parts = entry.Split('|', 2);
                var appId = parts[0];
                var name = parts.Length > 1 ? parts[1] : appId;
                Row(name, "Recently sent a notification.",
                    Toggle(!S.NotifyMutedApps.Contains(appId, StringComparer.OrdinalIgnoreCase), on =>
                    {
                        S.NotifyMutedApps.RemoveAll(a => a.Equals(appId, StringComparison.OrdinalIgnoreCase));
                        if (!on) S.NotifyMutedApps.Add(appId);
                        Save(false);
                    }));
            }
        }
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

        Section("IDLE NOTCH");
        Row("Layout", "How several items share the closed notch when nothing is playing.",
            Combo(["Rotate one at a time", "Left and right", "All in a row"], Math.Max(0, Array.IndexOf(IdleInfoService.Layouts, S.IdleLayout)),
                i => { S.IdleLayout = IdleInfoService.Layouts[i]; SaveIdle(); }, 180));
        var seconds = new[] { 4, 8, 15, 30 };
        Row("Rotate every", "How long each item stays before the next one slides in.",
            Combo(["4 seconds", "8 seconds", "15 seconds", "30 seconds"], Math.Max(0, Array.IndexOf(seconds, S.IdleRotateSec)),
                i => { S.IdleRotateSec = seconds[i]; SaveIdle(); }));

        Section("SHOW WHEN IDLE");
        foreach (var (id, name) in IdleInfoService.Catalog)
        {
            var itemId = id;
            Row(name, IdleDescriptions.GetValueOrDefault(id, ""),
                Toggle(S.IdleItems.Contains(id), on =>
                {
                    // Keep the catalogue order so the notch shows items in the order listed here.
                    var set = S.IdleItems.ToHashSet();
                    if (on) set.Add(itemId); else set.Remove(itemId);
                    S.IdleItems = IdleInfoService.Catalog.Select(c => c.Id).Where(set.Contains).ToList();
                    SaveIdle();
                    if (itemId == "aiUsage") Render(); // show / hide the AI tool choices below
                }));
            if (id == "aiUsage" && S.IdleItems.Contains("aiUsage")) AiToolChoices();
        }
    }

    /// <summary>Under "AI usage": Auto (closest to limit) or a checklist of the detected tools.</summary>
    private void AiToolChoices()
    {
        var modes = new[] { "Auto", "Choose" };
        Row("Which AI tools", "Auto shows the one closest to its limit. Or pick tools; each becomes its own item.",
            Combo(["Auto · closest to limit", "Choose tools"], S.IdleAiMode == "Choose" ? 1 : 0, i =>
            {
                S.IdleAiMode = modes[i];
                SaveIdle();
                Render();
            }, 190));
        if (S.IdleAiMode != "Choose") return;

        var tools = IdleInfoService.Instance.DetectedAiTools;
        if (tools.Count == 0)
        {
            Page.Children.Add(Hint("Looking for AI tools with usage limits…"));
            // Detection runs in the background; show the list as soon as it lands.
            void Detected()
            {
                IdleInfoService.Instance.AiToolsChanged -= Detected;
                if (_page == "display") Render();
            }
            IdleInfoService.Instance.AiToolsChanged += Detected;
            return;
        }
        foreach (var (toolId, toolName, logo) in tools)
        {
            var id = toolId;
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(BrandLogos.Create(logo, 14));
            title.Children.Add(new TextBlock { Text = toolName, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Row(toolName, $"Show {toolName}'s usage on the closed notch.",
                Toggle(S.IdleAiTools.Contains(id, StringComparer.OrdinalIgnoreCase), on =>
                {
                    S.IdleAiTools.RemoveAll(t => t.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (on) S.IdleAiTools.Add(id);
                    SaveIdle();
                }), title);
        }
    }

    private static readonly Dictionary<string, string> IdleDescriptions = new()
    {
        ["clock"] = "Current time.",
        ["date"] = "Weekday and date.",
        ["weather"] = "Temperature for your Weather city. Updates every 10 minutes.",
        ["nextEvent"] = "Today's next calendar event and its time.",
        ["todos"] = "How many to-dos are still open.",
        ["battery"] = "Charge level; green while charging. Hidden on PCs without a battery.",
        ["cpu"] = "Processor and memory use.",
        ["network"] = "Download speed.",
        ["stock"] = "Your Stocks tickers, one after another, with today's change.",
        ["aiUsage"] = "Claude session usage from the AI Usage tool.",
        ["clipboard"] = "Number of items in clipboard history.",
        ["shelf"] = "Files waiting on the Shelf. Hidden when empty.",
        ["streak"] = "Pomodoro focus sessions finished today.",
        ["avatar"] = "Your profile picture and name.",
    };

    private static void SaveIdle()
    {
        Save(false);
        IdleInfoService.Instance.Reconfigure();
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
            // Lambda, not the method group: Render's optional parameter makes the group an
            // Action<bool>, which the dispatcher would invoke with no arguments and throw.
            Dispatcher.BeginInvoke(() => Render());
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
        var info = AiService.Info(AiService.ActiveProvider);
        Title("Ask AI", $"Chat with {info.Name} from the notch. Messages are sent to {info.Company}'s API using your own key.");

        Section("MODEL");
        var providers = AiService.Providers;
        Row("Provider", "Which AI answers in the notch. Each one uses its own API key.",
            Combo(providers.Select(p => p.Name), Math.Max(0, Array.FindIndex(providers, p => p.Id == info.Id)), i =>
            {
                S.AiProvider = providers[i].Id;
                S.AiModel = AiService.DefaultModelFor(S.AiProvider);
                Save(false);
                Render();
            }));
        var models = AiService.ModelsFor(info.Id);
        var model = Combo(AiService.LabelsFor(info.Id), Math.Max(0, Array.IndexOf(models, S.AiModel)),
            i => { S.AiModel = models[i]; Save(false); }, 200);
        Row("Model", info.ModelHint, model);
        if (AiService.HasKeyFor(info.Id) && models == info.DefaultModels)
        {
            // OpenAI / DeepSeek: swap the built-in list for the models this key can actually use.
            _ = RefreshModelsAsync(info.Id);
        }

        Section("ACCOUNT");
        var hasKey = AiService.HasKeyFor(info.Id);
        Row($"{info.Name} API key", hasKey
                ? "Saved and encrypted for your Windows account. Paste a new key to replace it."
                : $"Paste your key from {info.KeySource}. It's stored encrypted on this PC.",
            ApiKeyInput(() => AiService.HasKeyFor(info.Id), key => AiService.SaveKeyFor(info.Id, key), () => Render(),
                info.KeyPlaceholder, info.KeyLabel));
        if (hasKey)
        {
            var remove = new Button { Content = "Remove key" };
            remove.Click += (_, _) =>
            {
                AiService.SaveKeyFor(info.Id, "");
                Render();
            };
            Row($"Forget {info.Name} key", $"Delete the saved {info.Company} key from this PC.", remove);
        }
        var others = providers.Where(p => p.Id != info.Id && AiService.HasKeyFor(p.Id)).Select(p => p.Name).ToList();
        if (others.Count > 0)
            Page.Children.Add(Hint($"Also saved: {string.Join(", ", others)} key{(others.Count > 1 ? "s" : "")}. Switch provider to manage them."));

        Section("AI USAGE");
        Row("Claude session limit", "Fallback when your plan usage can't be fetched: tokens per 5-hour session, to show “% used”. 0 hides the bar.",
            NumberInput(S.ClaudeSessionTokenLimit, v => { S.ClaudeSessionTokenLimit = v; Save(false); }));
        Row("Claude weekly limit", "Fallback: tokens per 7 days. 0 hides the bar.",
            NumberInput(S.ClaudeWeeklyTokenLimit, v => { S.ClaudeWeeklyTokenLimit = v; Save(false); }));
    }

    private string? _modelsRefreshedFor;

    private async Task RefreshModelsAsync(string provider)
    {
        if (_modelsRefreshedFor == provider) return;
        _modelsRefreshedFor = provider;
        if (await AiService.RefreshModelsAsync(provider) && _page == "ai" && AiService.ActiveProvider == provider)
        {
            // Keep the chosen model if the key has it; otherwise pick the newest listed one.
            var models = AiService.ModelsFor(provider);
            if (!models.Contains(S.AiModel)) { S.AiModel = models[0]; Save(false); }
            Render();
        }
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

    private const string RepoUrl = "https://github.com/faisal-shohag/orixNotch";

    private void RenderAbout()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        Title("About", "Version, privacy and the work OrixNotch builds on.");

        // Hero: logo tile, wordmark, tagline, version chips and project links.
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(18),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1),
            Child = new BrandMark { Variant = BrandVariant.Gradient, Width = 50, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        tile.SetResourceReference(Border.BackgroundProperty, "PillBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        hero.Children.Add(tile);

        var info = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold };
        name.Inlines.Add(new System.Windows.Documents.Run("Orix"));
        name.Inlines.Add(new System.Windows.Documents.Run("Notch") { Foreground = (Brush)FindResource("BrandGradientBrush") });
        info.Children.Add(name);
        info.Children.Add(new TextBlock
        {
            Text = "Your screen, smarter — a dynamic-island toolbox for Windows.",
            Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });
        var chips = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        chips.Children.Add(Chip($"Version {version}", accent: true));
        chips.Children.Add(Chip(Environment.Is64BitProcess ? "Windows · x64" : "Windows · x86"));
        chips.Children.Add(Chip($".NET {Environment.Version.ToString(2)}"));
        info.Children.Add(chips);
        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        links.Children.Add(LinkButton("Check for updates", AppIcon.ArrowDown, RepoUrl + "/releases", primary: true));
        links.Children.Add(LinkButton("GitHub", AppIcon.Link, RepoUrl));
        links.Children.Add(LinkButton("Feedback", AppIcon.Flag, RepoUrl + "/issues"));
        info.Children.Add(links);
        Grid.SetColumn(info, 1);
        hero.Children.Add(info);
        var heroCard = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Child = hero, Margin = new Thickness(0, 16, 0, 0) };
        heroCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Page.Children.Add(heroCard);

        Section("DETAILS");
        var details = new StackPanel();
        details.Children.Add(InfoRow("Version", version));
        details.Children.Add(Divider());
        details.Children.Add(InfoRow("Runtime", $".NET {Environment.Version}"));
        details.Children.Add(Divider());
        details.Children.Add(InfoRow("System", WindowsName()));
        details.Children.Add(Divider());
        details.Children.Add(InfoRow("Data folder", "Settings, notes, history and keys", OpenButton(Storage.Root)));
        var log = Storage.PathOf("error.log");
        if (System.IO.File.Exists(log))
        {
            details.Children.Add(Divider());
            details.Children.Add(InfoRow("Error log", "Useful when reporting an issue", OpenButton(log)));
        }
        Page.Children.Add(Group(details));

        Section("PRIVACY");
        var privacy = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        privacy.Children.Add(new TextBlock
        {
            Text = "Everything stays on this PC. These tools go online only while you use them:",
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        privacy.Children.Add(ServiceRow(AppIcon.Cloud, "Weather", "Open-Meteo — the city you search"));
        privacy.Children.Add(ServiceRow(AppIcon.ChartLine, "Stocks", "Yahoo Finance — your watchlist symbols"));
        privacy.Children.Add(ServiceRow(AppIcon.MusicNote, "Lyrics", "LRCLIB — the title and artist playing"));
        privacy.Children.Add(ServiceRow(AppIcon.Sparkles, "Ask AI", "Anthropic, OpenAI, Google or DeepSeek — only the messages you send"));
        privacy.Children.Add(ServiceRow(AppIcon.ChartBar, "AI Usage", "Anthropic — your plan usage, using Claude Code's sign-in"));
        privacy.Children.Add(new TextBlock
        {
            Text = "API keys are encrypted with Windows DPAPI and only ever sent to their own provider.",
            Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        Page.Children.Add(Group(privacy));

        Section("CREDITS");
        var credits = new StackPanel();
        credits.Children.Add(InfoRow("Heroicons", "Interface icons · MIT"));
        credits.Children.Add(Divider());
        credits.Children.Add(InfoRow("Inter", "Typeface · SIL Open Font License"));
        credits.Children.Add(Divider());
        credits.Children.Add(InfoRow("Noto Sans Bengali", "Bengali typeface · SIL Open Font License"));
        credits.Children.Add(Divider());
        credits.Children.Add(InfoRow("Simple Icons", "AI provider logos · CC0"));
        credits.Children.Add(Divider());
        credits.Children.Add(InfoRow("LobeHub Icons", "Antigravity logo · MIT"));
        credits.Children.Add(Divider());
        credits.Children.Add(InfoRow("Emoji.Wpf", "Color emoji rendering"));
        credits.Children.Add(Divider());
        credits.Children.Add(InfoRow("Open-Meteo · LRCLIB", "Free weather and lyrics data"));
        Page.Children.Add(Group(credits));

        // Footer: copyright on the left, quit on the right.
        var footer = new Grid { Margin = new Thickness(4, 18, 0, 4) };
        footer.Children.Add(new TextBlock
        {
            Text = $"© {DateTime.Now.Year} OrixNotch",
            Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
        });
        var quit = new Button { Content = "Quit OrixNotch", HorizontalAlignment = HorizontalAlignment.Right };
        quit.SetResourceReference(ForegroundProperty, "BadBrush");
        quit.Click += (_, _) => Application.Current.Shutdown();
        footer.Children.Add(quit);
        Page.Children.Add(footer);
    }

    /// <summary>Windows 11 still reports major version 10; build 22000+ is 11.</summary>
    private static string WindowsName()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : $"Windows {v.Major}";
        return $"{name} · build {v.Build}";
    }

    private static Border Chip(string text, bool accent = false)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 4), Child = label };
        chip.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSoftBrush" : "Surface2Brush");
        label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentBrush" : "SubTextBrush");
        return chip;
    }

    private static Button LinkButton(string text, AppIcon icon, string url, bool primary = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new HeroIcon { Kind = icon, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Margin = new Thickness(0, 4, 8, 0), ToolTip = url };
        if (primary) button.SetResourceReference(StyleProperty, "AccentButton");
        button.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return button;
    }

    private static Button OpenButton(string path)
    {
        var button = new Button { Content = "Open", ToolTip = path };
        button.Click += (_, _) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return button;
    }

    /// <summary>Label on the left, value (and optional control) on the right.</summary>
    private FrameworkElement InfoRow(string label, string value, FrameworkElement? control = null)
    {
        var grid = new Grid { MinHeight = 42 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock
        {
            Text = value, Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is not null)
        {
            control.Margin = new Thickness(12, 6, 0, 6);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        return grid;
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
        return line;
    }

    /// <summary>Privacy list entry: icon tile, tool name, and what it sends where.</summary>
    private FrameworkElement ServiceRow(AppIcon icon, string tool, string detail)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = new HeroIcon { Kind = icon, Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
        var tile = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Child = glyph, Margin = new Thickness(0, 0, 10, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        row.Children.Add(tile);
        var name = new TextBlock { Text = tool, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var what = new TextBlock
        {
            Text = detail, Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(what, 2);
        row.Children.Add(what);
        return row;
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
    /// <param name="titleContent">Replaces the plain title text (e.g. a logo beside the name).</param>
    private void Row(string title, string description, FrameworkElement control, FrameworkElement? titleContent = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 11), MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleContent ?? new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
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
        var box = new ComboBox { Width = width, Height = RowControlHeight };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) changed(box.SelectedIndex);
        };
        return box;
    }

    /// <summary>Settings rows are denser than the tools: fields, combos and buttons share this height.</summary>
    private const double RowControlHeight = 30;

    private static void Compact(Control field)
    {
        field.Height = RowControlHeight;
        field.MinHeight = 0;
        field.FontSize = 13.5;
    }

    private static TextBox TextInput(string value, string placeholder, Action<string> changed)
    {
        var box = new TextBox { Text = value, Tag = placeholder, Width = 200 };
        Compact(box);
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
        var idleHint = hasKey() ? "••••••••  saved" : placeholder;
        var box = new PasswordBox { Width = 240, ToolTip = tooltip, Tag = idleHint };
        Compact(box);

        var save = new Button { Content = "Save", Height = RowControlHeight, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        save.SetResourceReference(StyleProperty, "AccentButton");
        box.PasswordChanged += (_, _) =>
        {
            save.IsEnabled = box.Password.Trim().Length > 0;
            // A rejected key's message stays in the hint only until the user tries again.
            if (box.Password.Length > 0 && !Equals(box.Tag, idleHint))
            {
                box.Tag = idleHint;
                box.ToolTip = tooltip;
            }
        };
        void Commit()
        {
            var key = box.Password.Trim();
            if (key.Length == 0) return;
            try
            {
                saveKey(key);
            }
            catch (ArgumentException ex)
            {
                box.Clear();
                box.Tag = ex.Message;
                box.ToolTip = ex.Message;
                Field.ShowError(box);
                return;
            }
            box.Clear();
            saved();
        }
        save.Click += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
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
        var current = value;
        var box = new TextBox { Text = value.ToString("N0"), Tag = "0", Width = 120 };
        Compact(box);
        void Commit()
        {
            var text = box.Text.Trim();
            if (long.TryParse(text, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var v) && v >= 0)
            {
                if (v != current) changed(v);
                current = v;
            }
            box.Text = current.ToString("N0");
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
            box.SelectAll();
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

internal static class SettingsViewExtensions
{
    /// <summary>Configure an object inline: <c>new Button().Also(b => …)</c>.</summary>
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
