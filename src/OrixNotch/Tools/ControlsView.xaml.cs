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
    private readonly LevelBar _master = new(30, AppIcon.Speaker);

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

        MasterHost.Content = _master;
        _master.ValueChanged += value =>
        {
            MasterText.Text = $"{value * 100:0}%";
            VolumeService.Instance.MasterVolume = (float)value;
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
        try
        {
            await (_radios ??= InitRadiosAsync()); // shown twice before the radios load: still looked up (and hooked) once
        }
        catch (Exception ex) // async void: radio access can throw (no permission, service off)
        {
            App.Log(ex);
        }
    }

    private Task? _radios;

    private async Task InitRadiosAsync()
    {
        _wifi = await SystemToggles.RadioAsync(RadioKind.WiFi);
        _bluetooth = await SystemToggles.RadioAsync(RadioKind.Bluetooth);
        foreach (var radio in new[] { _wifi, _bluetooth })
            if (radio is not null) radio.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTiles);
        UpdateTiles();
    }

    public void OnHidden()
    {
        _refresh.Stop();
        ReleaseApps(); // OnShown reads them again
    }

    private List<VolumeService.AppAudio> _apps = [];

    private void ReleaseApps()
    {
        foreach (var a in _apps) a.Release();
        _apps = [];
        _appKey = "";
    }

    private static (bool? On, string Text) RadioState(Radio? radio) =>
        radio is null ? (null, "Not available") : radio.State == global::Windows.Devices.Radios.RadioState.On ? (true, "On") : (false, "Off");

    private static async Task ToggleRadio(Radio? radio)
    {
        if (radio is null) return;
        await SystemToggles.SetRadioAsync(radio, radio.State != global::Windows.Devices.Radios.RadioState.On);
    }

    private void AddTile(AppIcon icon, string label, Func<(bool? On, string Text)> read, Func<Task> toggle)
    {
        var circle = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left };
        var glyph = new HeroIcon { Kind = icon, Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        circle.Child = glyph;
        var name = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var state = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        state.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(circle);
        stack.Children.Add(name);
        stack.Children.Add(state);
        var button = new Button
        {
            Style = (Style)FindResource("RowButton"), Content = stack, Padding = new Thickness(12, 10, 10, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        button.Click += async (_, _) =>
        {
            try
            {
                await toggle();
            }
            catch (Exception ex) // a radio or dark-mode switch Windows refused: the tile shows the real state
            {
                App.Log(ex);
            }
            finally
            {
                UpdateTiles();
            }
        };
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(0), Margin = new Thickness(3), Child = button };
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
        if (_master.Dragging) return; // the user is dragging it
        _master.Value = level;
        MasterText.Text = $"{level * 100:0}%";
    }

    /// <summary>One row per app with sound. Rebuilt only when the apps change, so a slider being dragged stays put.</summary>
    private void RenderApps()
    {
        var apps = VolumeService.Instance.Apps();
        var key = string.Join("|", apps.Select(a => a.App));
        if (key == _appKey)
        {
            foreach (var a in apps) a.Release(); // same apps: the rows keep the ones they have
            return;
        }
        ReleaseApps();
        _apps = apps;
        _appKey = key;
        AppList.Children.Clear();
        foreach (var app in apps)
        {
            // icon · name · slim level bar · percent · mute
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Height = 26 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var label = app.App.Length == 0 ? "System sounds" : ScreenTimeService.Instance.DisplayName(app.App);
            FrameworkElement icon = app.App.Length > 0 && ScreenTimeService.Instance.IconFor(app.App) is { } image
                ? new Image { Source = image, Width = 18, Height = 18 }
                : new HeroIcon { Kind = app.App.Length == 0 ? AppIcon.Bell : AppIcon.Speaker, Width = 15, Height = 15 };
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Center;
            if (icon is HeroIcon glyph) glyph.SetResourceReference(ForegroundProperty, "SubTextBrush");
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var name = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(name, 1);
            var bar = new LevelBar(6) { Value = app.Volume, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
            Grid.SetColumn(bar, 2);
            var percent = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(percent, 3);
            var mute = new Button { Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Right };
            var muteIcon = new HeroIcon { Width = 14, Height = 14 };
            mute.Content = muteIcon;
            Grid.SetColumn(mute, 4);
            void Show()
            {
                muteIcon.Kind = app.Muted ? AppIcon.SpeakerMuted : AppIcon.Speaker;
                mute.ToolTip = app.Muted ? "Unmute" : "Mute";
                mute.SetResourceReference(ForegroundProperty, app.Muted ? "BadBrush" : "SubTextBrush");
                percent.Text = app.Muted ? "Muted" : $"{bar.Value * 100:0}%";
                percent.SetResourceReference(TextBlock.ForegroundProperty, app.Muted ? "BadBrush" : "SubTextBrush");
                bar.Opacity = app.Muted ? 0.45 : 1;
            }
            Show();
            bar.ValueChanged += value =>
            {
                app.Volume = (float)value;
                Show();
            };
            mute.Click += (_, _) =>
            {
                app.Muted = !app.Muted;
                Show();
            };
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(bar);
            row.Children.Add(percent);
            row.Children.Add(mute);
            AppList.Children.Add(row);
        }
        if (apps.Count == 0)
            AppList.Children.Add(new TextBlock { Text = "No apps are making sound.", Style = (Style)FindResource("SubText"), FontWeight = FontWeights.Normal, Margin = new Thickness(0, 4, 0, 0) });
    }
}
