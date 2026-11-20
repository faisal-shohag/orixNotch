using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrixNotch.Services;
using OrixNotch.Shell;
using Windows.Devices.Radios;

namespace OrixNotch.Tools;

/// <summary>
/// Controls: quick toggles (Wi-Fi, Bluetooth, microphone, keep awake, quiet, dark mode) and volume for the
/// speakers and each app with sound.
/// </summary>
public partial class ControlsView : UserControl, IToolView
{
    private sealed record Tile(Border Circle, HeroIcon Icon, TextBlock State, Func<(bool? On, string Text)> Read);

    private readonly List<Tile> _tiles = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private Radio? _wifi, _bluetooth;
    private string _appKey = "";
    private bool _settingMaster;

    public ControlsView()
    {
        InitializeComponent();
        AddTile(AppIcon.Wifi, "Wi-Fi", () => RadioState(_wifi), () => ToggleRadio(_wifi));
        AddTile(AppIcon.Bluetooth, "Bluetooth", () => RadioState(_bluetooth), () => ToggleRadio(_bluetooth));
        AddTile(AppIcon.Microphone, "Microphone",
            () => VolumeService.Instance.HasMic ? (!VolumeService.Instance.MicMuted, VolumeService.Instance.MicMuted ? "Muted" : "On") : (null, "No mic"),
            () => { VolumeService.Instance.ToggleMic(); return Task.CompletedTask; });
        AddTile(AppIcon.Coffee, "Keep awake", () => (KeepAwakeService.On, KeepAwakeService.On ? "On" : "Off"),
            () => { KeepAwakeService.Toggle(); return Task.CompletedTask; });
        AddTile(AppIcon.Moon, "Quiet", () => NotchWindow.QuietSnoozed() ? (true, "1 hour")
                : NotchWindow.QuietScheduled() ? (true, $"Until {App.Settings.QuietTo}") : (false, "Off"),
            () => { NotchWindow.ToggleQuietHour(); return Task.CompletedTask; });
        AddTile(AppIcon.Palette, "Dark mode", () => (SystemToggles.DarkMode, SystemToggles.DarkMode ? "On" : "Off"),
            () => { SystemToggles.DarkMode = !SystemToggles.DarkMode; return Task.CompletedTask; });

        MasterSlider.ValueChanged += (_, e) =>
        {
            MasterText.Text = $"{e.NewValue * 100:0}%";
            if (!_settingMaster) VolumeService.Instance.MasterVolume = (float)e.NewValue;
        };
        VolumeService.Instance.Changed += (level, _) => ShowMaster(level);
        VolumeService.Instance.MicChanged += _ => UpdateTiles();
        KeepAwakeService.Changed += _ => UpdateTiles();
        _refresh.Tick += (_, _) =>
        {
            UpdateTiles(); // quiet hours end, dark mode changed elsewhere
            RenderApps();
        };
    }

    public async void OnShown()
    {
        ShowMaster(VolumeService.Instance.MasterVolume ?? 0);
        _appKey = "";
        RenderApps();
        UpdateTiles();
        _refresh.Start();
        if (_wifi is null && _bluetooth is null)
        {
            _wifi = await SystemToggles.RadioAsync(RadioKind.WiFi);
            _bluetooth = await SystemToggles.RadioAsync(RadioKind.Bluetooth);
            foreach (var radio in new[] { _wifi, _bluetooth })
                if (radio is not null) radio.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTiles);
            UpdateTiles();
        }
    }

    public void OnHidden() => _refresh.Stop();

    private static (bool? On, string Text) RadioState(Radio? radio) =>
        radio is null ? (null, "Not available") : radio.State == global::Windows.Devices.Radios.RadioState.On ? (true, "On") : (false, "Off");

    private static async Task ToggleRadio(Radio? radio)
    {
        if (radio is null) return;
        await SystemToggles.SetRadioAsync(radio, radio.State != global::Windows.Devices.Radios.RadioState.On);
    }

    private void AddTile(AppIcon icon, string label, Func<(bool? On, string Text)> read, Func<Task> toggle)
    {
        var circle = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = new HeroIcon { Kind = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        circle.Child = glyph;
        var name = new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var state = new TextBlock { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        state.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var stack = new StackPanel();
        stack.Children.Add(circle);
        stack.Children.Add(name);
        stack.Children.Add(state);
        var button = new Button { Style = (Style)FindResource("RowButton"), Content = stack, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 8, 4, 6) };
        button.Click += async (_, _) =>
        {
            await toggle();
            UpdateTiles();
        };
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Margin = new Thickness(3, 0, 3, 0), Child = button };
        Toggles.Children.Add(card);
        _tiles.Add(new Tile(circle, glyph, state, read));
    }

    private void UpdateTiles()
    {
        foreach (var tile in _tiles)
        {
            var (on, text) = tile.Read();
            tile.State.Text = text;
            tile.Circle.SetResourceReference(Border.BackgroundProperty, on == true ? "AccentBrush" : "Surface2Brush");
            tile.Icon.SetResourceReference(ForegroundProperty, on == true ? "OnAccentBrush" : on is null ? "SubTextBrush" : "TextBrush");
            tile.Circle.Opacity = on is null ? 0.5 : 1;
        }
    }

    private void ShowMaster(float level)
    {
        if (MasterSlider.IsMouseCaptureWithin) return; // the user is dragging it
        _settingMaster = true;
        MasterSlider.Value = level;
        MasterText.Text = $"{level * 100:0}%";
        _settingMaster = false;
    }

    /// <summary>One row per app with sound. Rebuilt only when the apps change, so a slider being dragged stays put.</summary>
    private void RenderApps()
    {
        var apps = VolumeService.Instance.Apps();
        var key = string.Join("|", apps.Select(a => a.App));
        if (key == _appKey) return;
        _appKey = key;
        AppList.Children.Clear();
        foreach (var app in apps)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var name = new TextBlock
            {
                Text = app.App.Length == 0 ? "System sounds" : ScreenTimeService.Instance.DisplayName(app.App),
                FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = app.Volume, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            Grid.SetColumn(slider, 1);
            var mute = new Button { Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Right };
            var muteIcon = new HeroIcon { Width = 14, Height = 14 };
            mute.Content = muteIcon;
            void ShowMute()
            {
                muteIcon.Kind = app.Muted ? AppIcon.SpeakerMuted : AppIcon.Speaker;
                mute.ToolTip = app.Muted ? "Unmute" : "Mute";
                mute.SetResourceReference(ForegroundProperty, app.Muted ? "BadBrush" : "SubTextBrush");
            }
            ShowMute();
            slider.ValueChanged += (_, e) => app.Volume = (float)e.NewValue;
            mute.Click += (_, _) =>
            {
                app.Muted = !app.Muted;
                ShowMute();
            };
            Grid.SetColumn(mute, 2);
            row.Children.Add(name);
            row.Children.Add(slider);
            row.Children.Add(mute);
            AppList.Children.Add(row);
        }
        if (apps.Count == 0)
            AppList.Children.Add(new TextBlock { Text = "No apps are making sound.", Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, Margin = new Thickness(0, 4, 0, 0) });
    }
}
