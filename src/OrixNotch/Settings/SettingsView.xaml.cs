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
    internal static readonly (string Id, string Name, AppIcon Icon, string? Section)[] Pages =
    [
        ("general", "General", AppIcon.Adjust, null),
        ("automations", "Automations", AppIcon.Bolt, null),
        ("appearance", "Appearance", AppIcon.Palette, null),
        ("display", "Display", AppIcon.Monitor, "NOTCH"),
        ("tabs", "Tabs", AppIcon.Grid, null),
        ("keyboard", "Keyboard", AppIcon.Keyboard, null),
        ("ai", "Ask AI", AppIcon.Sparkles, "TOOLS"),
        ("data", "Clipboard & Shelf", AppIcon.ClipboardList, null),
        ("about", "About", AppIcon.Info, ""),
    ];

    /// <summary>
    /// Settings added in this release, by page: each gets a "New" chip (and its page a dot in the sidebar) until
    /// that page has been opened once. Update the list with each release; a page id alone ("automations") marks a new page.
    /// </summary>
    private static readonly Dictionary<string, string[]> NewRows = new()
    {
        ["general"] = ["Add a daily goal", "Rest your eyes", "Stand up", "Drink water", "Quiet hours", "Screenshots", "Track screen time", "Evening summary", "Calendar reminders", "Focus sessions", "Timers"],
        ["automations"] = ["Pause music", "Join from the reminder", "Quiet while on a call", "Focus events start a session", "Subscription renewals", "Alerts from scripts", "orixnotch:// links", "Match the app in front", "Morning brief", "Quiet during focus"],
        ["appearance"] = ["Follow album art", "Tint from wallpaper", "Music bars follow the sound", "Share this look", "Celebrations", "Glow"],
        ["display"] = ["Clock style", "Second clock", "Sunrise & sunset", "Day timeline", "Unread notifications", "Screen time today", "On a call", "Microphone muted", "Keep awake"],
        ["keyboard"] = ["Command palette", "Explain selected text", "Start focus", "Add a to-do", "Copy text from screen", "Mute microphone", "Keep awake"],
        ["about"] = ["Report a problem", "Request a tool"],
    };

    /// <summary>Sidebar icon colours, macOS System Settings style: fixed, so they read the same in every scheme.</summary>
    private static readonly Dictionary<string, string> NavColors = new()
    {
        ["general"] = "#8E8E93", ["automations"] = "#FF9F0A", ["appearance"] = "#0A84FF", ["display"] = "#30B0C7",
        ["tabs"] = "#5E5CE6", ["keyboard"] = "#636366", ["ai"] = "#BF5AF2", ["data"] = "#34C759", ["about"] = "#0B0E17",
    };

    /// <summary>A page's icon on a small coloured rounded tile (About: the OrixNotch mark on the brand's dark tile).</summary>
    private static Border NavTile(string id, AppIcon icon)
    {
        var color = (Color)ColorConverter.ConvertFromString(NavColors.GetValueOrDefault(id, "#8E8E93"));
        FrameworkElement glyph = id == "about"
            ? new BrandMark { Variant = BrandVariant.Gradient, Width = 13, Height = 13 }
            : new HeroIcon { Kind = icon, Width = 13, Height = 13, Foreground = Brushes.White };
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center, Child = glyph,
            Background = new LinearGradientBrush(Lighten(color, 0.12), color, 90), // a hint of depth, like the Mac's tiles
        };

        static Color Lighten(Color c, double by) => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * by), (byte)(c.G + (255 - c.G) * by), (byte)(c.B + (255 - c.B) * by));
    }

    private string _rendering = ""; // the page Row() is adding to, for its "New" chip

    private static bool IsNew(string page, string title) =>
        NewRows.TryGetValue(page, out var titles) && titles.Contains(title) && !App.Settings.SeenNew.Contains($"{page}|{title}");

    private static bool HasNew(string page) => NewRows.TryGetValue(page, out var titles) && titles.Any(t => IsNew(page, t));

    private readonly Dictionary<string, FrameworkElement> _navDots = new();

    private string _page = "general";
    private string? _recordingAction;

    private static AppSettings S => App.Settings;

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = $"OrixNotch {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        if (SettingsService.FirstRun) MarkSeen(NewRows.Keys); // everything is new on a new install: no chips

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
            content.Children.Add(NavTile(id, icon));
            content.Children.Add(new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Visibility = HasNew(id) ? Visibility.Visible : Visibility.Collapsed, ToolTip = "Something new here",
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            content.Children.Add(dot);
            _navDots[id] = dot;
            var nav = new RadioButton
            {
                Style = (Style)Resources["NavButton"],
                Content = content,
                GroupName = "settingsNav",
                IsChecked = id == _page,
                Tag = id,
            };
            var pageId = id;
            nav.Checked += (_, _) =>
            {
                if (_page == pageId) return;
                _page = pageId;
                if (SearchBox.Text.Length > 0) SearchBox.Clear(); // re-renders, now on this page
                else Render(resetScroll: true);
            };
            Nav.Children.Add(nav);
        }

        ThemeService.Changed += () =>
        {
            if (_page == "appearance" && !_themeFromSlider) Render();
        };
        PreviewKeyDown += OnRecordKey;
        // Show the calendar sync result once it lands (not mid-edit: re-rendering drops focus).
        CalendarFeedService.Instance.Updated += () =>
        {
            if (_page == "general" && IsVisible && !IsKeyboardFocusWithin) Render();
        };
        Render();
    }

    public void OnShown() => Render();

    /// <summary>Switches to a page by id ("appearance", "ai"…), as if its nav button was clicked.</summary>
    public void ShowPage(string id)
    {
        foreach (var nav in Nav.Children.OfType<RadioButton>())
            if ((string?)nav.Tag == id) nav.IsChecked = true;
    }

    public void OnHidden()
    {
        _recordingAction = null;
        if (IsLoaded) MarkSeen(_viewed); // only pages actually shown (the first render happens before Settings is on screen)
    }

    private readonly HashSet<string> _viewed = new();

    // ---------------------------------------------------------------- page rendering

    private void Render(bool resetScroll = false)
    {
        // Rebuilding the page clears its children, which collapses the scroll extent and
        // drops the offset to 0. Preserve it so toggles / restore / hotkey edits don't
        // yank the user back to the top. Only page switches pass resetScroll: true.
        var offset = resetScroll ? 0 : PageScroll.VerticalOffset;
        Page.Children.Clear();
        if (resetScroll) PageScroll.ScrollToTop();
        if (_query.Length > 0) RenderSearch();
        else
        {
            RenderPage(_page);
            // Seen once Settings closes: chips stay while it's open, and the page's sidebar dot goes now.
            _viewed.Add(_page);
            if (_navDots.TryGetValue(_page, out var dot)) dot.Visibility = Visibility.Collapsed;
        }
        if (!resetScroll && offset > 0)
            Dispatcher.BeginInvoke(() => PageScroll.ScrollToVerticalOffset(offset),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MarkSeen(IEnumerable<string> pages)
    {
        var added = false;
        foreach (var page in pages)
        {
            if (!NewRows.TryGetValue(page, out var titles)) continue;
            foreach (var title in titles)
                if (!S.SeenNew.Contains($"{page}|{title}"))
                {
                    S.SeenNew.Add($"{page}|{title}");
                    added = true;
                }
        }
        if (added) Save(false);
    }

    private void RenderPage(string id)
    {
        _rendering = id;
        switch (id)
        {
            case "general": RenderGeneral(); break;
            case "automations": RenderAutomations(); break;
            case "appearance": RenderAppearance(); break;
            case "display": RenderDisplay(); break;
            case "tabs": RenderTabs(); break;
            case "keyboard": RenderKeyboard(); break;
            case "ai": RenderAi(); break;
            case "data": RenderData(); break;
            case "about": RenderAbout(); break;
        }
    }

    // ---------------------------------------------------------------- search

    private string _query = "";

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query == _query) return;
        _query = query;
        Render(resetScroll: true);
    }

    /// <summary>
    /// Builds every page, then keeps only the rows whose title or description matches, under their page's
    /// name and section. Everything else (titles, swatches, lists) hides. The rows are the real controls.
    /// </summary>
    private void RenderSearch()
    {
        var caps = FindResource("Caps");
        foreach (var (id, name, _, _) in Pages)
        {
            var start = Page.Children.Count;
            RenderPage(id);
            var added = Page.Children.Cast<UIElement>().Skip(start).ToList();
            var matched = 0;
            TextBlock? section = null;
            foreach (var element in added)
            {
                if (element is Border { Tag: "rows", Child: StackPanel rows })
                {
                    var visible = FilterRows(rows, _query);
                    element.Visibility = visible > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (visible > 0 && section is not null) section.Visibility = Visibility.Visible;
                    matched += visible;
                }
                else
                {
                    element.Visibility = Visibility.Collapsed;
                    if (element is TextBlock tb && tb.Style == caps) section = tb;
                }
            }
            if (matched == 0)
            {
                Page.Children.RemoveRange(start, added.Count);
                continue;
            }
            Page.Children.Insert(start, new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, start == 0 ? 0 : 18, 0, 0) });
        }
        if (Page.Children.Count == 0)
            Page.Children.Add(new TextBlock { Text = $"No settings match \"{_query}\".", Style = (Style)FindResource("SubText"), FontSize = 12.5, Margin = new Thickness(4, 8, 0, 0) });
    }

    /// <summary>Hides a card's rows that don't mention <paramref name="query"/> (and the dividers between hidden rows).</summary>
    private static int FilterRows(StackPanel rows, string query)
    {
        var visible = 0;
        for (var i = 0; i < rows.Children.Count; i++)
        {
            if (rows.Children[i] is not Grid row) continue;
            var text = string.Join(" ", TextsIn(row.Children[0]));
            var match = text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            // The divider above a row shows only when a shown row sits above it too.
            if (i > 0) rows.Children[i - 1].Visibility = match && visible > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (match) visible++;
        }
        return visible;
    }

    private static IEnumerable<string> TextsIn(DependencyObject element)
    {
        if (element is TextBlock { Text: { Length: > 0 } text }) yield return text;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var t in TextsIn(child))
                yield return t;
    }

    /// <summary>Apps with an open window right now, for "Hide for these apps": (process name, friendly name).</summary>
    private static List<(string Name, string Label)> RunningApps()
    {
        var apps = new Dictionary<string, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero || process.MainWindowTitle.Length == 0) continue;
                    var name = process.ProcessName.ToLowerInvariant();
                    if (name == "applicationframehost") continue; // hosts every Store app: can't tell them apart (ForegroundAppWatcher)
                    if (!apps.ContainsKey(name)) apps[name] = FriendlyName(process, name);
                }
                catch (Exception)
                {
                    // exited while we looked
                }
            }
        }
        return apps.Select(a => (a.Key, a.Value)).OrderBy(a => a.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A listed app's name as its exe describes itself: from a running copy, else the exe Windows registered
    /// for it (App Paths, e.g. powerpnt → "Microsoft PowerPoint"), else its process name.</summary>
    private static string AppLabel(string name)
    {
        foreach (var process in Process.GetProcessesByName(name))
            using (process)
                return FriendlyName(process, name);
        foreach (var root in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            if (Microsoft.Win32.Registry.GetValue($@"{root}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}.exe", "", null) is string exe
                && System.IO.File.Exists(exe = exe.Trim('"'))
                && FileVersionInfo.GetVersionInfo(exe).FileDescription is { Length: > 0 } description)
                return description.Trim();
        }
        return name;
    }

    /// <summary>Scrolls the page so the section headed <paramref name="caps"/> is at the top (Store screenshot mode).</summary>
    internal void ScrollToSection(string caps) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (Page.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text == caps) is { } header)
                PageScroll.ScrollToVerticalOffset(Math.Max(0, header.TransformToAncestor(Page).Transform(new Point()).Y - 4));
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    private static string FriendlyName(Process process, string name)
    {
        try
        {
            if (process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description) return description.Trim();
        }
        catch (Exception)
        {
            // elevated or protected process: no access to its file
        }
        return name;
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
        string[] visibility = ["Always", "WhenActive", "HoverOnly"];
        Row("Show notch", "Always, only while music, a timer or a reminder is on, or only when the pointer touches the top edge of the screen.",
            Combo(["Always", "When active", "On top-edge hover"], Math.Max(0, Array.IndexOf(visibility, S.NotchVisibility)),
                i => { S.NotchVisibility = visibility[i]; Save(); }));
        Row("Hide in full screen", "Stay out of the way of full-screen apps and videos; touch the top edge to bring it back. Games hide it completely.",
            Toggle(S.HideInFullscreen, v => { S.HideInFullscreen = v; Save(); }));
        Row("Hide from screenshots", "Leave the notch out of screenshots, recordings and screen sharing.",
            Toggle(S.HideFromCapture, v => { S.HideFromCapture = v; Save(); }));
        Row("Dodge the pointer", "The closed notch dodges the pointer from the sides. Catch it from below.",
            Toggle(S.RepelCursor, v => { S.RepelCursor = v; Save(); }));

        Section("HIDE FOR THESE APPS");
        foreach (var listed in S.HideForApps.ToList())
        {
            var name = listed;
            Row(AppLabel(name), $"{name}.exe",
                new Button { Content = "Remove", Height = RowControlHeight }.Also(b => b.Click += (_, _) =>
                {
                    S.HideForApps.Remove(name);
                    Save();
                    Render();
                }));
        }
        var running = RunningApps().Where(a => !S.HideForApps.Contains(a.Name)).ToList();
        var pick = Combo(running.Select(a => a.Label), running.Count > 0 ? 0 : -1, _ => { }, width: 200);
        var addApp = new Button { Content = "Add", Height = RowControlHeight, Margin = new Thickness(6, 0, 0, 0), IsEnabled = running.Count > 0 };
        addApp.Click += (_, _) =>
        {
            if (pick.SelectedIndex < 0) return;
            S.HideForApps.Add(running[pick.SelectedIndex].Name);
            Save();
            Render();
        };
        var addAppRow = new StackPanel { Orientation = Orientation.Horizontal };
        addAppRow.Children.Add(pick);
        addAppRow.Children.Add(addApp);
        Row("Add an app", "Hide the notch while it's in front, on any display.", addAppRow);
        if (S.HideForApps.Count > 0)
        {
            Row("When one is in front", "Fade out (touch the top edge to bring it back), or hide completely.",
                Combo(["Fade out", "Hide completely"], S.HideForAppsMode == "Hidden" ? 1 : 0,
                    i => { S.HideForAppsMode = i == 1 ? "Hidden" : "Fade"; Save(); }, width: 170));
            Row("Alerts", "Hold alerts until you switch away, or let them pop out anyway.",
                Combo(["Wait until I switch away", "Show them anyway"], S.HideForAppsHoldAlerts ? 0 : 1,
                    i => { S.HideForAppsHoldAlerts = i == 0; Save(); }, width: 200));
        }

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
        Row("Announce new songs", "Pop the new song's art and title out of the notch for a moment when the track changes.",
            Toggle(S.TrackPeek, v => { S.TrackPeek = v; Save(false); }));

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

        Section("SOUNDS");
        SoundRow("Calendar reminders", "Plays when a reminder appears and when the event starts.",
            () => S.ReminderSound, v => S.ReminderSound = v);
        SoundRow("Focus sessions", "Plays when a focus session or a break ends.",
            () => S.PomodoroSound, v => { S.PomodoroSound = v; PomodoroService.Instance.RefreshSettings(); });
        SoundRow("Timers", "Plays when a countdown finishes.",
            () => S.TimerSound, v => S.TimerSound = v);

        Section("SUBSCRIBED CALENDARS");
        var feeds = CalendarFeedService.Instance;
        foreach (var feed in CalendarFeedService.Feeds)
        {
            var link = feed;
            Row(CalendarFeedService.Label(link), "Read-only. Shows in Calendar, Home and reminders; refreshes every 30 minutes.",
                new Button { Content = "Remove", Height = RowControlHeight }.Also(b => b.Click += (_, _) =>
                {
                    feeds.Remove(link);
                    Render();
                }));
        }
        var url = TextInput("", "Paste .ics link", _ => { });
        var add = new Button { Content = "Add", Height = RowControlHeight, Margin = new Thickness(6, 0, 0, 0) };
        void AddFeed()
        {
            var text = url.Text.Trim();
            if (!CalendarFeedService.IsValidUrl(text))
            {
                Field.ShowError(url);
                return;
            }
            feeds.Add(text);
            Render();
        }
        add.Click += (_, _) => AddFeed();
        url.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddFeed();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(url);
        addRow.Children.Add(add);
        var help = new TextBlock
        {
            Text = "Google: calendar.google.com → ⚙ Settings → under \"Settings for my calendars\" pick the calendar → "
                + "Integrate calendar → copy \"Secret address in iCal format\". Each calendar has its own link.\n"
                + "Outlook: Settings → Calendar → Shared calendars → Publish → ICS link.\n"
                + "webcal:// links work too. Links are stored encrypted.",
            Style = (Style)FindResource("SubText"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        // (i) shows / hides the step-by-step help under the row.
        var helpButton = new Button
        {
            Width = RowControlHeight,
            Height = RowControlHeight,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "How to find the link",
            Content = new HeroIcon { Kind = AppIcon.Info, Width = 15, Height = 15 },
        };
        helpButton.SetResourceReference(StyleProperty, "IconButton");
        helpButton.SetResourceReference(ForegroundProperty, "SubTextBrush");
        System.Windows.Automation.AutomationProperties.SetName(helpButton, "How to find the calendar link");
        helpButton.Click += (_, _) =>
        {
            var open = help.Visibility != Visibility.Visible;
            help.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            helpButton.SetResourceReference(ForegroundProperty, open ? "AccentBrush" : "SubTextBrush");
        };
        addRow.Children.Add(helpButton);
        Row("Add a calendar", feeds.Status.Length > 0 ? feeds.Status : "Google, Outlook or iCloud link.",
            addRow, footer: help);

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
        Row("Keep history", "Save notifications on this PC to read later in the Notifications tool (last 30 days). Clear them there anytime.",
            Toggle(S.NotifyKeepHistory, v => { S.NotifyKeepHistory = v; Save(false); }));
        Row("Windows banners", "Windows still shows its own pop-up too. Turn banners off per app in Windows Settings if you only want the notch.",
            new Button { Content = "Open settings" }.Also(b =>
                b.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true })));
        Row("AI usage alerts", "A gentle heads-up on the notch when Claude Code, Codex, Cursor or another AI tool reaches 80% and 95% of its session or weekly limit.",
            Toggle(S.AiLimitAlerts, v =>
            {
                S.AiLimitAlerts = v;
                Save(false);
                AiLimitWatcher.Instance.Reconfigure();
            }));
        Row("Battery alerts", "Show on the notch when the charger is plugged in or pulled, and when the battery drops to 20% and 10%.",
            Toggle(S.PowerAlerts, v => { S.PowerAlerts = v; Save(false); }));
        Row("Volume on the notch", "Show the volume level on the notch when it changes. Windows still shows its own volume pop-up too.",
            Toggle(S.VolumeHud, v => { S.VolumeHud = v; Save(false); }));
        Row("Bluetooth devices", "Show on the notch when paired headphones, a mouse or other Bluetooth device connects or disconnects.",
            Toggle(S.BluetoothAlerts, v => { S.BluetoothAlerts = v; Save(false); }));
        Row("Finished downloads", "Show on the notch when a browser download finishes in your Downloads folder. Click to show the file.",
            Toggle(S.DownloadAlerts, v => { S.DownloadAlerts = v; Save(false); }));
        Row("Screenshots", "Show a preview on the notch when you take a screenshot (Win + PrtScn, Snipping Tool). Click it to put it on the Shelf.",
            Toggle(S.ScreenshotAlerts, v => { S.ScreenshotAlerts = v; Save(false); }));

        Section("SCREEN TIME");
        Row("Track screen time", "Counts how long each app is in front while you use the PC, for the Screen Time tool. Kept only on this PC for 60 days.",
            Toggle(S.ScreenTimeEnabled, v => ScreenTimeService.Instance.SetEnabled(v)));
        var hours = Enumerable.Range(17, 7).ToArray(); // 5 PM – 11 PM
        Row("Evening summary", "One alert with the day's screen time and the app you used most.",
            Toggle(S.ScreenTimeSummary, v => { S.ScreenTimeSummary = v; Save(false); }));
        Row("Summary at", "When the evening summary shows.",
            Combo(hours.Select(h => DateTime.Today.AddHours(h).ToString("t", System.Globalization.CultureInfo.CurrentCulture)),
                Math.Max(0, Array.IndexOf(hours, S.ScreenTimeSummaryHour)), i => { S.ScreenTimeSummaryHour = hours[i]; Save(false); }));

        Section("DAILY GOALS");
        var st = ScreenTimeService.Instance;
        foreach (var (app, minutes) in S.ScreenTimeGoals.ToList())
        {
            var name = app;
            Row(st.DisplayName(name), $"An alert after {CommandRegistry.FormatMinutes(minutes)} a day",
                new Button { Content = "Remove", Height = RowControlHeight }.Also(b => b.Click += (_, _) =>
                {
                    S.ScreenTimeGoals.Remove(name);
                    Save(false);
                    Render();
                }));
        }
        // Apps used recently or running now, most used first.
        var goalApps = Enumerable.Range(0, 7).SelectMany(i => st.Day(DateTime.Today.AddDays(-i))).GroupBy(x => x.App)
            .OrderByDescending(g => g.Sum(x => x.Seconds)).Select(g => g.Key)
            .Concat(RunningApps().Select(a => a.Name)).Distinct().Where(a => !S.ScreenTimeGoals.ContainsKey(a)).ToList();
        var goalPick = Combo(goalApps.Select(st.DisplayName), goalApps.Count > 0 ? 0 : -1, _ => { }, width: 170);
        int[] goalMinutes = [15, 30, 45, 60, 90, 120, 180, 240];
        var goalLength = Combo(goalMinutes.Select(CommandRegistry.FormatMinutes), 3, _ => { }, width: 100);
        goalLength.Margin = new Thickness(6, 0, 0, 0);
        var addGoal = new Button { Content = "Add", Height = RowControlHeight, Margin = new Thickness(6, 0, 0, 0), IsEnabled = goalApps.Count > 0 };
        addGoal.Click += (_, _) =>
        {
            if (goalPick.SelectedIndex < 0) return;
            S.ScreenTimeGoals[goalApps[goalPick.SelectedIndex]] = goalMinutes[goalLength.SelectedIndex];
            if (!S.ScreenTimeEnabled) st.SetEnabled(true); // a goal needs tracking
            Save(false);
            Render();
        };
        var goalRow = new StackPanel { Orientation = Orientation.Horizontal };
        goalRow.Children.Add(goalPick);
        goalRow.Children.Add(goalLength);
        goalRow.Children.Add(addGoal);
        Row("Add a daily goal", "A gentle alert once a day when an app passes its time. Apps only; websites aren't counted separately.", goalRow);

        Section("BREAKS");
        foreach (var kind in BreakService.Kinds)
        {
            var id = kind.Id;
            var (title, what) = id switch
            {
                "eyes" => ("Rest your eyes", "Every 20 minutes: look 20 feet (6 m) away for 20 seconds."),
                "stand" => ("Stand up", "After an hour at the screen."),
                _ => ("Drink water", "Every hour and a half."),
            };
            Row(title, what + " Only time you're using the PC counts, and a few minutes away resets it. Waits out full screen, quiet hours and focus.",
                Toggle(BreakService.IsOn(id), v =>
                {
                    S.BreakReminders.Remove(id);
                    if (v) S.BreakReminders.Add(id);
                    Save(false);
                    BreakService.Instance.Reconfigure();
                }));
        }

        Section("QUIET HOURS");
        var times = Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{i % 2 * 30:00}").ToList();
        int Slot(string t) => Math.Max(0, times.IndexOf(t));
        Row("Quiet hours", "Hold alerts during these hours. When they end, one alert says how many you missed. The tray menu also has \"Quiet for 1 hour\".",
            Toggle(S.QuietEnabled, v => { S.QuietEnabled = v; Save(false); }));
        Row("From", "Quiet starts at this time.",
            Combo(times, Slot(S.QuietFrom), i => { S.QuietFrom = times[i]; Save(false); }, 100));
        Row("Until", "Quiet ends at this time, the next morning if it's earlier than the start.",
            Combo(times, Slot(S.QuietTo), i => { S.QuietTo = times[i]; Save(false); }, 100));
        Row("Let calendar reminders through", "Meeting reminders still show during quiet hours.",
            Toggle(S.QuietLetRemindersThrough, v => { S.QuietLetRemindersThrough = v; Save(false); }));

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

    private void RenderAutomations()
    {
        Title("Automations", "Small things OrixNotch does for you, each with its own switch.");

        Section("BEFORE A MEETING");
        Row("Pause music", "Pauses what's playing shortly before a calendar event starts.",
            Toggle(S.AutoPauseMusic, v => { S.AutoPauseMusic = v; Save(false); }));
        var leads = new[] { 0, 1, 2, 5 };
        Row("Pause it", "How long before the event.",
            Combo(["When it starts", "1 minute before", "2 minutes before", "5 minutes before"], Math.Max(0, Array.IndexOf(leads, S.AutoPauseLeadMin)),
                i => { S.AutoPauseLeadMin = leads[i]; Save(false); }, 170));
        Row("Join from the reminder", "When a subscribed calendar's event has a Meet, Zoom, Teams or Webex link, clicking its reminder opens the meeting.",
            Toggle(S.AutoJoinLink, v => { S.AutoJoinLink = v; Save(false); }));

        Section("DURING A CALL");
        Row("Quiet while on a call", "Holds alerts while any app (including your browser) is using the microphone or camera, then sums them up afterwards.",
            Toggle(S.AutoQuietOnMic, v => { S.AutoQuietOnMic = v; Save(false); }));

        Section("FOCUS");
        Row("Quiet during focus", "Alerts wait while a focus session runs, then one alert sums them up. Reminders follow your quiet hours setting.",
            Toggle(S.FocusQuiet, v => { S.FocusQuiet = v; Save(false); }));
        Row("Focus events start a session", "A calendar event with \"Focus\" in its name starts a Pomodoro focus session when it begins.",
            Toggle(S.AutoFocusEvents, v => { S.AutoFocusEvents = v; Save(false); }));

        Section("HEADS-UPS");
        Row("AI usage alerts", "A heads-up when Claude Code, Codex, Cursor or another AI tool reaches 80% and 95% of its limit.",
            Toggle(S.AiLimitAlerts, v =>
            {
                S.AiLimitAlerts = v;
                Save(false);
                AiLimitWatcher.Instance.Reconfigure();
            }));
        Row("Subscription renewals", "An alert 2 days before something in the Subscriptions tool renews.",
            Toggle(S.SubscriptionAlerts, v => { S.SubscriptionAlerts = v; Save(false); }));

        Section("MORNING");
        Row("Morning brief", "When you first unlock your PC each day, the notch opens for a few seconds on the weather, your next event, open to-dos, AI usage and a stock. Not during quiet hours.",
            Toggle(S.MorningBrief, v => { S.MorningBrief = v; Save(false); }));

        Section("OPEN THE RIGHT TOOL");
        Row("Match the app in front", "Opening the notch shows the tool you picked for the app you're using, like AI Usage in your code editor.",
            Toggle(S.ContextTools, v => { S.ContextTools = v; Save(false); Render(); }));
        if (S.ContextTools)
        {
            foreach (var (app, toolId) in S.AppTools.ToList())
            {
                var name = app;
                var tool = ToolRegistry.All.FirstOrDefault(t => t.Id == toolId);
                Row(AppLabel(name), $"Opens {tool?.Name ?? toolId}",
                    new Button { Content = "Remove", Height = RowControlHeight }.Also(b => b.Click += (_, _) =>
                    {
                        S.AppTools.Remove(name);
                        Save(false);
                        Render();
                    }));
            }
            var apps = RunningApps().Where(a => !S.AppTools.ContainsKey(a.Name)).ToList();
            var appPick = Combo(apps.Select(a => a.Label), apps.Count > 0 ? 0 : -1, _ => { }, width: 150);
            var tools = ToolRegistry.All.Where(t => t.Id != "home").ToList();
            var toolPick = Combo(tools.Select(t => t.Name), 0, _ => { }, width: 130);
            toolPick.Margin = new Thickness(6, 0, 0, 0);
            var add = new Button { Content = "Add", Height = RowControlHeight, Margin = new Thickness(6, 0, 0, 0), IsEnabled = apps.Count > 0 };
            add.Click += (_, _) =>
            {
                if (appPick.SelectedIndex < 0) return;
                S.AppTools[apps[appPick.SelectedIndex].Name] = tools[toolPick.SelectedIndex].Id;
                Save(false);
                Render();
            };
            var addRow = new StackPanel { Orientation = Orientation.Horizontal };
            addRow.Children.Add(appPick);
            addRow.Children.Add(toolPick);
            addRow.Children.Add(add);
            Row("Add an app", "Pick a running app and the tool to show for it.", addRow);
        }

        Section("FOR SCRIPTS");
        var api = PeekApiService.Instance;
        Row("Alerts from scripts", "Lets builds, CI or a coding agent show an alert on the notch through a local web address. Only this PC can reach it, and only with your token.",
            Toggle(S.PeekApiEnabled, async v => { S.PeekApiEnabled = v; Save(false); await api.SyncAsync(); Render(); }),
            footer: S.PeekApiEnabled ? Hint(api.Error ?? $"POST http://127.0.0.1:{S.PeekApiPort}/peek with {{\"title\": \"…\", \"sub\": \"…\"}}", bad: api.Error is not null) : null);
        if (S.PeekApiEnabled)
        {
            var tokenButtons = new StackPanel { Orientation = Orientation.Horizontal };
            tokenButtons.Children.Add(IconButton(AppIcon.Copy, "Copy an example curl command", true, () => ClipboardService.Instance.CopyQuiet(api.CurlExample)));
            tokenButtons.Children.Add(IconButton(AppIcon.Refresh, "New token (scripts using the old one stop working)", true, () => { api.NewToken(); ClipboardService.Instance.CopyQuiet(api.CurlExample); }));
            Row("Token", "Copy a ready curl command with your token. Keep the token private, like a password.", tokenButtons);
            var ports = new[] { 53340, 53341, 53342, 53343 };
            Row("Port", "Change it if another app already uses this one.",
                Combo(ports.Select(p => p.ToString()), Math.Max(0, Array.IndexOf(ports, S.PeekApiPort)),
                    async i => { S.PeekApiPort = ports[i]; Save(false); await api.SyncAsync(); Render(); }, 110));
        }
        Row("orixnotch:// links", "Links that open a tool or show an alert, from a script, a shortcut or the Run box: orixnotch://open/timer, orixnotch://timer?min=10, orixnotch://peek?text=Hello",
            IconButton(AppIcon.Copy, "Copy an example link", true, () => ClipboardService.Instance.CopyQuiet("orixnotch://peek?text=Hello%20from%20a%20link")));
    }

    /// <summary>A sound picker that previews on change, plus a play button to hear it again.</summary>
    private void SoundRow(string title, string description, Func<string> get, Action<string> set)
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Combo(SoundService.Names, SoundService.IndexOf(get()), i =>
        {
            set(SoundService.Names[i]);
            Save(false);
            SoundService.Play(get());
        }));
        controls.Children.Add(IconButton(AppIcon.Play, "Play", true, () => SoundService.Play(get())));
        Row(title, description, controls);
    }

    private void RenderAppearance()
    {
        Title("Appearance", "Color scheme and accent. Changes apply instantly.");
        Section("LOOKS");
        var looks = new System.Windows.Controls.Primitives.UniformGrid { Columns = ThemeService.Looks.Length, Margin = new Thickness(-4, 0, -4, 12) }; // room before "Share this look"
        for (var i = 0; i < ThemeService.Looks.Length; i++) looks.Children.Add(LookCard(i));
        Page.Children.Add(looks);
        Row("Share this look", "Copies a link with your colours, glass and size. Anyone with OrixNotch can open it to try it, with Undo.",
            IconButton(AppIcon.Copy, "Copy link", true, () => ClipboardService.Instance.CopyQuiet(ThemeService.ShareLink())));

        Section("COLOR SCHEME");
        var schemes = new WrapPanel();
        foreach (var scheme in ThemeService.Schemes.Where(s => !s.IsGradient)) schemes.Children.Add(SchemeCard(scheme));
        Page.Children.Add(schemes);

        Section("GRADIENT SCHEMES");
        var gradientSchemes = new WrapPanel();
        foreach (var scheme in ThemeService.Schemes.Where(s => s.IsGradient)) gradientSchemes.Children.Add(SchemeCard(scheme));
        Page.Children.Add(gradientSchemes);

        Section("ACCENT");
        Page.Children.Add(new TextBlock
        {
            Text = "Every scheme comes with its own accent (Auto). Pick another to override it.",
            Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 11.5, Margin = new Thickness(4, -4, 0, 8),
        });
        var accents = new WrapPanel();
        accents.Children.Add(AutoAccentSwatch());
        foreach (var accent in ThemeService.Accents.Where(a => !a.IsGradient)) accents.Children.Add(AccentSwatch(accent));
        Page.Children.Add(accents);

        Section("GRADIENT ACCENTS");
        var gradientAccents = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; // room before "Follow album art"
        foreach (var accent in ThemeService.Accents.Where(a => a.IsGradient)) gradientAccents.Children.Add(AccentSwatch(accent));
        Page.Children.Add(gradientAccents);
        Row("Follow album art", "While music plays, the accent takes the colour of the cover. The accent above comes back when it stops.",
            Toggle(S.AlbumAccent, v => { S.AlbumAccent = v; Save(false); ThemeService.Apply(); }));
        Section("EFFECTS");
        Row("Celebrations", "Confetti when you finish your last to-do, a cheer at focus streaks (3, 7, 14, 30 days…), and birthdays from your calendar.",
            Toggle(S.Celebrations, v => { S.Celebrations = v; Save(false); }));
        Row("Glow", "A soft glow around the closed notch: the album's colour while music plays, your accent during a focus session, red while you're on a call.",
            Toggle(S.NotchGlow, v => { S.NotchGlow = v; Save(false); NotchWindow.Instance?.RefreshGlow(); }));
        Row("Music bars follow the sound", "The bars on the closed notch move with what's playing. Off, or on battery saver, they sway on their own.",
            Toggle(S.ReactivePill, v => { S.ReactivePill = v; Save(false); }));

        Section("GLASS");
        var strength = new Slider { Minimum = 0, Maximum = 1, Value = S.GlassStrength, Width = 170, VerticalAlignment = VerticalAlignment.Center, IsEnabled = S.Glass };
        var saveLater = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveLater.Tick += (_, _) =>
        {
            saveLater.Stop();
            Save(false);
        };
        strength.ValueChanged += (_, e) =>
        {
            S.GlassStrength = e.NewValue;
            // Live preview without rebuilding this page (that would drop the slider mid-drag).
            _themeFromSlider = true;
            try
            {
                ThemeService.Apply();
            }
            finally
            {
                _themeFromSlider = false;
            }
            saveLater.Stop();
            saveLater.Start();
        };
        Row("Glass", "See-through notch tinted by the color scheme.",
            Toggle(S.Glass, v => { S.Glass = v; Save(false); ThemeService.Apply(); }));
        Row("Tint", "Lower shows more of the background; higher keeps text easier to read.", strength);
        Row("Tint from wallpaper", "The glass takes on the colour of your wallpaper behind the notch, and follows it when the wallpaper changes.",
            Toggle(S.GlassWallpaperTint, v => { S.GlassWallpaperTint = v; Save(false); ThemeService.Apply(); }));

        Section("UNITS");
        Row("Fahrenheit & mph", "Weather units.", Toggle(S.UseFahrenheit, v => { S.UseFahrenheit = v; Save(false); }));
    }

    /// <summary>A one-click look: a small notch preview in its colours, and its name.</summary>
    private FrameworkElement LookCard(int index)
    {
        var look = ThemeService.Looks[index];
        var scheme = ThemeService.Schemes.First(s => s.Id == look.Scheme);
        var accent = ThemeService.Accents.FirstOrDefault(a => a.Id == scheme.Accent) ?? ThemeService.Accents[0];
        // A tiny desktop: a colourful wallpaper strip with the notch hanging from the top, so glass reads as glass.
        var notch = new Border
        {
            Width = 74, Height = 20, CornerRadius = new CornerRadius(0, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Background = scheme.PillBrush(), Opacity = look.Glass ? 0.6 + 0.3 * look.Strength : 1,
            BorderThickness = new Thickness(1, 0, 1, 1),
        };
        notch.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        var inside = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inside.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = accent.Brush() });
        inside.Children.Add(new Border
        {
            Width = 22, Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(6, 0, 0, 0),
            Background = new SolidColorBrush(scheme.IsLight ? Color.FromArgb(0x70, 0, 0, 0) : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
        });
        notch.Child = inside;
        var wallpaper = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x1D, 0x2B, 0x64), 0), new(Color.FromRgb(0x6A, 0x3D, 0x9A), 0.55), new(Color.FromRgb(0xF0, 0x7B, 0x5A), 1),
        }, 20);
        var preview = new Border
        {
            Height = 46, CornerRadius = Radii.M, Background = wallpaper, BorderThickness = new Thickness(1), ClipToBounds = true,
            Child = notch,
        };
        preview.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        var stack = new StackPanel();
        stack.Children.Add(preview);
        stack.Children.Add(new TextBlock { Text = look.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 0) });
        stack.Children.Add(new TextBlock
        {
            Text = look.Glass ? (look.WallpaperTint ? "Glass, wallpaper tint" : "Glass") : scheme.IsGradient ? "Gradient" : "Solid",
            Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 11,
        });
        var button = new Button { Style = (Style)FindResource("RowButton"), Content = stack, Padding = new Thickness(10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => ThemeService.ApplyLook(index); // Changed re-renders this page
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Margin = new Thickness(4, 0, 4, 0), Child = button };
    }

    private void RenderDisplay()
    {
        Title("Display", "Where the notch appears, and how big it is.");
        Section("LOCATION");
        var screens = Forms.Screen.AllScreens;
        // Combo items: primary (-1), follow the pointer (-2), then each display by index.
        var names = new List<string> { "Primary display", "Follow the pointer" };
        for (var i = 0; i < screens.Length; i++)
            names.Add($"Display {i + 1} · {screens[i].Bounds.Width}×{screens[i].Bounds.Height}{(screens[i].Primary ? " (primary)" : "")}");
        var selected = S.MonitorIndex == -2 ? 1 : S.MonitorIndex >= 0 && S.MonitorIndex < screens.Length ? S.MonitorIndex + 2 : 0;
        Row("Show notch on", "Which monitor hosts the notch. Follow the pointer moves it to whichever display you're using.",
            Combo(names, selected, i => { S.MonitorIndex = i switch { 0 => -1, 1 => -2, _ => i - 2 }; Save(); }, 210));

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
            if (id == "clock2" && S.IdleItems.Contains("clock2")) SecondClockChoice();
            if (id == "clock" && S.IdleItems.Contains("clock"))
            {
                string[] styles = ["Plain", "Flip"];
                Row("Clock style", "Flip shows the time on a small split tile that flips over each minute.",
                    Combo(styles, Math.Max(0, Array.IndexOf(styles, S.ClockStyle)), i => { S.ClockStyle = styles[i]; SaveIdle(); }));
            }
        }
    }

    private void SecondClockChoice()
    {
        var zones = TimeZoneInfo.GetSystemTimeZones();
        var index = zones.Select(z => z.Id).ToList().IndexOf(S.SecondClockZone);
        // Not picked yet (-1): the item stays hidden until it is.
        Row("Time zone", "The second clock shows this time zone, named after its main city.",
            Combo(zones.Select(z => z.DisplayName), index, i => { S.SecondClockZone = zones[i].Id; SaveIdle(); }, 300));
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
            Page.Children.Add(Hint("Looking for AI tools…"));
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
            Row(toolName, logo == BrandLogo.OpenCode ? $"Show {toolName}'s tokens today on the closed notch." : $"Show {toolName}'s usage on the closed notch.",
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
        ["clock2"] = "The time somewhere else, such as a teammate's city. Pick the time zone below.",
        ["date"] = "Weekday and date.",
        ["sun"] = "The next sunrise or sunset in your Weather city.",
        ["timeline"] = "Today on one line: the time gone by, your events and now.",
        ["call"] = "Shows in red while any app uses your microphone or camera.",
        ["mic"] = "Shows only while your microphone is muted.",
        ["awake"] = "Shows only while Keep awake is on.",
        ["weather"] = "Temperature for your Weather city. Updates every 10 minutes.",
        ["nextEvent"] = "Today's next calendar event and its time.",
        ["todos"] = "How many to-dos are still open.",
        ["battery"] = "Charge level; green while charging. Hidden on PCs without a battery.",
        ["cpu"] = "Processor and memory use.",
        ["network"] = "Download speed.",
        ["stock"] = "Your Stocks tickers, one after another, with today's change.",
        ["aiUsage"] = "Plan usage from the AI Usage tool, or today's tokens for OpenCode.",
        ["unread"] = "Notifications that arrived since you last opened the Notifications tool. Hidden when there are none.",
        ["clipboard"] = "Number of items in clipboard history.",
        ["shelf"] = "Files waiting on the Shelf. Hidden when empty.",
        ["streak"] = "Pomodoro focus sessions finished today.",
        ["screentime"] = "Time on screen today, from Screen Time (when it's on).",
        ["avatar"] = "Your profile picture and name.",
    };

    private static void SaveIdle()
    {
        Save(false);
        IdleInfoService.Instance.Reconfigure();
    }

    private void RenderTabs()
    {
        Title(SettingsService.FirstRun ? "Welcome! Pick your tools" : "Tabs",
            SettingsService.FirstRun
                ? "A few tools start switched off: turn on any you want, and move the rest into order with the arrows. "
                    + "Notifications shown on the notch are kept on this PC for 30 days (Settings → General to turn that off)."
                : "The tools in the notch's bottom bar, and their order.");

        Section("QUICK START");
        var presets = new System.Windows.Controls.Primitives.UniformGrid { Columns = Presets.Length, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var preset in Presets) presets.Children.Add(PresetCard(preset));
        Page.Children.Add(presets);
        Page.Children.Add(Hint("Sets your tabs and what the closed notch shows. Fine-tune everything below afterwards."));

        Section("IN THE NOTCH");
        var visible = ToolRegistry.Ordered();
        var list = new StackPanel();
        for (var i = 0; i < visible.Count; i++) list.Children.Add(ToolRow(visible[i], true, i, visible.Count));
        Page.Children.Add(Group(list));
        Page.Children.Add(Hint("Use the arrows, or press and hold a tab in the notch and drag it. Settings always stays at the end of the bar."));

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

    /// <summary>Starting points for the tab bar and idle notch. Null tools = every tool, in the default order.</summary>
    private sealed record Preset(string Name, string Blurb, AppIcon Icon, string[]? Tools, string[] Idle);

    private static readonly Preset[] Presets =
    [
        new("Developer", "AI, clipboard, shortcuts, system", AppIcon.Terminal,
            ["home", "ask", "aiusage", "clipboard", "shortcuts", "notifications", "todos", "timer", "calendar", "controls", "system", "nowplaying"],
            ["clock", "aiUsage", "nextEvent"]),
        new("Creator", "Teleprompter, phone, shelf, music", AppIcon.Teleprompter,
            ["home", "nowplaying", "teleprompter", "phone", "shelf", "note", "calendar", "todos", "controls", "emoji"],
            ["clock", "nextEvent"]),
        new("Student", "To-dos, focus timer, calendar, notes", AppIcon.BookOpen,
            ["home", "todos", "timer", "calendar", "note", "ask", "converter", "weather", "screentime", "nowplaying"],
            ["clock", "nextEvent", "streak"]),
        new("Everything", "Every tool, in the usual order", AppIcon.Grid, null, ["clock"]),
    ];

    private FrameworkElement PresetCard(Preset preset)
    {
        var stack = new StackPanel { Margin = new Thickness(2) };
        var icon = new Border { Width = 28, Height = 28, CornerRadius = Radii.M, HorizontalAlignment = HorizontalAlignment.Left };
        icon.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var glyph = new HeroIcon { Kind = preset.Icon, Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
        icon.Child = glyph;
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = preset.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 1) });
        // Same height on every card (up to three lines), so the cards line up.
        stack.Children.Add(new TextBlock { Text = preset.Blurb, Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, FontSize = 11, TextWrapping = TextWrapping.Wrap, Height = 42 });
        var button = new Button { Style = (Style)FindResource("RowButton"), Content = stack, Padding = new Thickness(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        button.Click += (_, _) => ApplyPreset(preset);
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Margin = new Thickness(4, 0, 4, 0), Child = button };
        return card;
    }

    private void ApplyPreset(Preset preset)
    {
        var all = ToolRegistry.All.Select(t => t.Id).ToList();
        S.ToolOrder = preset.Tools?.Where(all.Contains).ToList() ?? [];
        S.HiddenTools = preset.Tools is null ? [] : all.Where(id => !preset.Tools.Contains(id)).ToList();
        S.IdleItems = IdleInfoService.Catalog.Select(c => c.Id).Where(preset.Idle.Contains).ToList();
        Save();
        IdleInfoService.Instance.Reconfigure();
        Render();
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
            Width = 24, Height = 24, CornerRadius = Radii.S,
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
        var labels = AiService.LabelsFor(info.Id);
        var model = Combo([], Math.Max(0, Array.IndexOf(models, S.AiModel)),
            i => { S.AiModel = models[i]; Save(false); }, 250);
        // Free-tier models carry a "Free · 500/day" chip next to their name.
        model.ItemTemplate = Tools.AskAiView.ModelChoiceTemplate;
        for (var i = 0; i < models.Length; i++) model.Items.Add(new Tools.AskAiView.ModelChoice(labels[i], AiService.FreeTier(info.Id, models[i])));
        model.SelectedIndex = Math.Max(0, Array.IndexOf(models, S.AiModel));
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
            if (!models.Contains(S.AiModel)) { S.AiModel = AiService.DefaultModelFor(provider); Save(false); }
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

        Section("PHONE TRANSFER");
        var phone = PhoneTransferService.Instance;
        Row("Phone transfer", "Send files with phones on the same Wi-Fi: scan a QR code, no app needed.",
            Toggle(S.PhoneEnabled, v => { S.PhoneEnabled = v; Save(false); _ = phone.SyncAsync(); }));
        var browse = new Button { Content = "Change…" };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save received files in", InitialDirectory = phone.ReceiveFolder };
            using (NotchWindow.Instance?.HoldOpen())
            {
                if (dialog.ShowDialog(NotchWindow.Instance) != true) return;
            }
            S.PhoneReceiveFolder = dialog.FolderName;
            Save(false);
            Render();
        };
        Row("Save received files in", phone.ReceiveFolder, browse);

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
            Child = new BrandMark { Variant = BrandVariant.Gradient, Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
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
        // One row: update check, GitHub, feedback.
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        links.Children.Add(UpdateButton());
        links.Children.Add(LinkButton("GitHub", AppIcon.Link, RepoUrl));
        links.Children.Add(LinkButton("Feedback", AppIcon.Flag, RepoUrl + "/issues"));
        info.Children.Add(links);
        Grid.SetColumn(info, 1);
        hero.Children.Add(info);
        var heroCard = new Border { CornerRadius = Radii.L, Padding = new Thickness(18), Child = hero, Margin = new Thickness(0, 16, 0, 0) };
        heroCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Page.Children.Add(heroCard);

        Section("HELP");
        var report = new StackPanel { Orientation = Orientation.Horizontal };
        report.Children.Add(new Button { Content = "Copy details", Height = RowControlHeight, Margin = new Thickness(0, 0, 6, 0) }
            .Also(b => b.Click += (_, _) => ClipboardService.Instance.CopyQuiet(Diagnostics())));
        report.Children.Add(new Button { Content = "Report", Style = (Style)FindResource("AccentButton"), Height = RowControlHeight }
            .Also(b => b.Click += (_, _) => OpenUrl(RepoUrl + "/issues/new?labels=bug&title=" + Uri.EscapeDataString("Problem: ")
                + "&body=" + Uri.EscapeDataString("**What happened?**\n\n\n**What did you expect?**\n\n\n" + Diagnostics()))));
        Row("Report a problem", "Opens a GitHub issue with your version, Windows and the app's recent errors filled in. You see everything before you send it; nothing is sent on its own.", report);
        Row("Request a tool", "Missing something on the notch? Tell us what you'd use it for.",
            new Button { Content = "Request", Height = RowControlHeight }.Also(b => b.Click += (_, _) =>
                OpenUrl(RepoUrl + "/issues/new?labels=tool-request&title=" + Uri.EscapeDataString("Tool request: ")
                    + "&body=" + Uri.EscapeDataString("**What should it do?**\n\n\n**When would you use it?**\n\n"))));

        Section("DETAILS");
        var details = new StackPanel();
        details.Children.Add(InfoRow("Version", version));
        details.Children.Add(Divider());
        details.Children.Add(InfoRow("Runtime", $".NET {Environment.Version}"));
        details.Children.Add(Divider());
        details.Children.Add(InfoRow("System", WindowsName()));
        details.Children.Add(Divider());
        details.Children.Add(InfoRow("Updates", StartupService.IsPackaged ? "From the Microsoft Store" : "From GitHub releases",
            Toggle(S.CheckForUpdates, v => { S.CheckForUpdates = v; Save(false); UpdateService.Instance.Start(); })));
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
        privacy.Children.Add(ServiceRow(AppIcon.ChartBar, "AI Usage", "Anthropic, Cursor or DeepSeek — your usage, using each tool's own sign-in. OpenCode, Codex and Antigravity are read from this PC"));
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
        var chip = new Border { CornerRadius = Radii.M, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 4), Child = label };
        chip.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSoftBrush" : "Surface2Brush");
        label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentBrush" : "SubTextBrush");
        return chip;
    }

    /// <summary>
    /// What a bug report needs: version, Windows, how it was installed, and the last errors from error.log with the
    /// user's folder replaced by %USERPROFILE% (paths there often contain their name).
    /// </summary>
    private static string Diagnostics()
    {
        var lines = new List<string>
        {
            "**Details**",
            $"- OrixNotch {UpdateService.CurrentVersion} ({(StartupService.IsPackaged ? "Microsoft Store" : "desktop")}, {(Environment.Is64BitProcess ? "x64" : "x86")})",
            $"- Windows {Environment.OSVersion.Version}",
        };
        try
        {
            var log = Storage.PathOf("error.log");
            if (System.IO.File.Exists(log))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                // Short enough to fit in a GitHub "new issue" link.
                var tail = System.IO.File.ReadLines(log).Where(l => l.Trim().Length > 0).TakeLast(12)
                    .Select(l => l.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Length > 160 ? l[..160] + "…" : l);
                lines.Add("");
                lines.Add("**Recent errors**");
                lines.Add("```");
                lines.AddRange(tail);
                lines.Add("```");
            }
        }
        catch (System.IO.IOException)
        {
            // The log is optional.
        }
        return string.Join("\n", lines);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
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

    /// <summary>"Check for updates" that really checks, then offers to install; follows <see cref="UpdateService"/>.</summary>
    private Button UpdateButton()
    {
        var updates = UpdateService.Instance;
        var icon = new HeroIcon { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new Button { Content = content, Margin = new Thickness(0, 4, 6, 0) };

        void Show()
        {
            var (text, kind, accent) = updates.Status switch
            {
                UpdateStatus.Checking => ("Checking…", AppIcon.Refresh, false),
                UpdateStatus.Downloading => ("Downloading…", AppIcon.ArrowDown, false),
                UpdateStatus.UpToDate => ("Up to date", AppIcon.Check, false),
                UpdateStatus.Available => (updates.LatestVersion.Length > 0 ? $"Install {updates.LatestVersion}" : "Install update", AppIcon.ArrowDown, true),
                UpdateStatus.Failed => ("Couldn't check", AppIcon.Refresh, false),
                _ => ("Check for updates", AppIcon.ArrowDown, true),
            };
            label.Text = text;
            icon.Kind = kind;
            button.IsEnabled = updates.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading);
            if (accent) button.SetResourceReference(StyleProperty, "AccentButton");
            else button.ClearValue(StyleProperty);
            button.ToolTip = updates.CheckedAt is { } at ? $"Version {UpdateService.CurrentVersion} · checked {at:t}" : $"Version {UpdateService.CurrentVersion}";
        }

        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Show);
        updates.PropertyChanged += OnChanged;
        button.Unloaded += (_, _) => updates.PropertyChanged -= OnChanged;
        button.Click += (_, _) =>
        {
            if (updates.Status == UpdateStatus.Available) _ = updates.InstallAsync();
            else _ = updates.CheckAsync(userAsked: true);
        };
        Show();
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
        var tile = new Border { Width = 24, Height = 24, CornerRadius = Radii.S, Child = glyph, Margin = new Thickness(0, 0, 10, 0) };
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
        var border = new Border { CornerRadius = Radii.L, Padding = new Thickness(14, 2, 14, 2), Child = child };
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
    /// <param name="footer">Full-width line under the title and control (longer help).</param>
    private void Row(string title, string description, FrameworkElement control, FrameworkElement? titleContent = null,
        FrameworkElement? footer = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 11), MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (footer is not null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(footer, 1);
            Grid.SetColumnSpan(footer, 2);
        }
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleContent ?? (IsNew(_rendering, title) ? TitleWithNewChip(title) : new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold }));
        text.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("SubText"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text); // first child: search reads the row's text from it
        grid.Children.Add(control);
        if (footer is not null) grid.Children.Add(footer);

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

    private static FrameworkElement TitleWithNewChip(string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        var label = new TextBlock { Text = "NEW", FontSize = 9, FontWeight = FontWeights.Bold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var chip = new Border { CornerRadius = Radii.S, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = label };
        chip.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        panel.Children.Add(chip);
        return panel;
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
        var badge = new Border { CornerRadius = Radii.S, Padding = new Thickness(8, 3, 8, 3), Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Medium } };
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
        preview.Children.Add(new Border { Background = scheme.PreviewBrush(), CornerRadius = Radii.S });
        preview.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 A3,3 0 0 1 3,3 L3,8 A6,6 0 0 0 9,14 L35,14 A6,6 0 0 0 41,8 L41,3 A3,3 0 0 1 44,0 Z"),
            Fill = scheme.PillBrush(),
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, scheme.Text.R, scheme.Text.G, scheme.Text.B)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        });
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 8, 7) };
        lines.Children.Add(new Border { Height = 4, Width = 38, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(scheme.Text), HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(new Border { Height = 4, Width = 24, CornerRadius = new CornerRadius(2), Background = ThemeService.AccentFor(scheme).Brush(), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) });
        preview.Children.Add(lines);

        var frame = new Border { Child = preview, CornerRadius = Radii.M, BorderThickness = new Thickness(2), Padding = new Thickness(1) };
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
        var selected = !ThemeService.IsAutoAccent && accent.Id == ThemeService.CurrentAccent.Id;
        var swatch = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 2, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = accent.Name };
        swatch.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = selected ? accent.Brush() : Brushes.Transparent, StrokeThickness = 2 });
        swatch.Children.Add(new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = accent.Brush() });
        swatch.MouseLeftButtonUp += (_, _) => ThemeService.SetAccent(accent.Id);
        return swatch;
    }

    /// <summary>"Auto": the current scheme's own accent, marked with sparkles.</summary>
    private FrameworkElement AutoAccentSwatch()
    {
        var scheme = ThemeService.CurrentScheme;
        var own = ThemeService.Accents.FirstOrDefault(a => a.Id == scheme.Accent) ?? ThemeService.Accents[0];
        var selected = ThemeService.IsAutoAccent;
        var swatch = new Grid
        {
            Width = 28, Height = 28, Margin = new Thickness(0, 0, 2, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent,
            ToolTip = $"Auto · {own.Name} for {scheme.Name}",
        };
        swatch.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = selected ? own.Brush() : Brushes.Transparent, StrokeThickness = 2 });
        swatch.Children.Add(new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = own.Brush() });
        var light = (0.299 * own.Mean.R + 0.587 * own.Mean.G + 0.114 * own.Mean.B) / 255 > 0.55;
        swatch.Children.Add(new HeroIcon
        {
            Kind = AppIcon.Sparkles, Width = 11, Height = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = light ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x13)) : Brushes.White,
        });
        swatch.MouseLeftButtonUp += (_, _) => ThemeService.SetAccent(ThemeService.AutoAccent);
        return swatch;
    }

    private static void Save(bool notify = true) => SettingsService.Save(notify);

    /// <summary>The glass slider is re-theming live; don't rebuild the page under it.</summary>
    private bool _themeFromSlider;
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
