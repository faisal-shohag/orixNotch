using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace OrixNotch.Services;

/// <summary>Wraps the Windows global media session (Spotify, browsers, Media Player, …).</summary>
public sealed partial class NowPlayingService : ObservableObject
{
    public static NowPlayingService Instance { get; } = new();

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Session? _session;
    private double _positionBase;
    private DateTimeOffset _positionStamp;

    [ObservableProperty] private string _title = "Nothing playing";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _sourceApp = "";
    [ObservableProperty] private ImageSource? _art;
    [ObservableProperty] private Color? _artTint;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;

    public string PositionText => Format(Position);
    public string DurationText => Format(Duration);

    private NowPlayingService() => _ = InitAsync();

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));

    private async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (m, _) => _ui.BeginInvoke(() => Hook(m.GetCurrentSession()));
            _ui.Invoke(() => Hook(_manager.GetCurrentSession()));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void Hook(Session? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        HasSession = session is not null;
        if (session is null)
        {
            Title = "Nothing playing";
            Artist = "";
            SourceApp = "";
            Art = null;
            ArtTint = null;
            IsPlaying = false;
            Duration = 0;
            Position = 0;
            return;
        }

        session.MediaPropertiesChanged += OnMediaChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        SourceApp = PrettyAppName(session.SourceAppUserModelId);
        _ = RefreshMediaAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    private void OnMediaChanged(Session s, MediaPropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(() => _ = RefreshMediaAsync());

    private void OnPlaybackChanged(Session s, PlaybackInfoChangedEventArgs e) =>
        _ui.BeginInvoke(RefreshPlayback);

    private void OnTimelineChanged(Session s, TimelinePropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(RefreshTimeline);

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _session) return;

            Title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle ?? "" : props.Artist;

            ImageSource? image = null;
            if (props.Thumbnail is not null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var input = stream.AsStreamForRead();
                var buffer = new MemoryStream();
                await input.CopyToAsync(buffer);
                buffer.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 300;
                bmp.StreamSource = buffer;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            Art = image;
            ArtTint = image is BitmapSource src ? AverageColor(src) : null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            IsPlaying = false;
        }
    }

    private void RefreshTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t is null) return;
            Duration = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
            _positionBase = t.Position.TotalSeconds;
            _positionStamp = t.LastUpdatedTime;
            Tick();
        }
        catch
        {
            // Some players do not expose a timeline.
        }
    }

    /// <summary>Extrapolates the playhead between timeline updates; called by the view's timer.</summary>
    public void Tick()
    {
        var pos = _positionBase;
        if (IsPlaying && _positionStamp != default)
            pos += (DateTimeOffset.Now - _positionStamp).TotalSeconds;
        Position = Duration > 0 ? Math.Clamp(pos, 0, Duration) : Math.Max(0, pos);
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (_session is not null) await _session.TryTogglePlayPauseAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_session is not null) await _session.TrySkipNextAsync();
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_session is not null) await _session.TrySkipPreviousAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        if (_session is null) return;
        await _session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks);
    }

    /// <summary>Average color of the artwork, used to tint the Now Playing card.</summary>
    private static Color? AverageColor(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(16.0 / source.PixelWidth, 16.0 / source.PixelHeight));
            var w = small.PixelWidth;
            var h = small.PixelHeight;
            var pixels = new byte[w * h * 4];
            small.CopyPixels(pixels, w * 4, 0);
            long r = 0, g = 0, b = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }
            var n = Math.Max(1, pixels.Length / 4);
            return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch
        {
            return null;
        }
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string PrettyAppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var name = aumid;
        var bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Split('.', '_')[0];
        if (name.Length == 0) return aumid;
        return name.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "spotify" => "Spotify",
            "app" when aumid.Contains("Zune", StringComparison.OrdinalIgnoreCase) => "Media Player",
            "microsoft" when aumid.Contains("Zune", StringComparison.OrdinalIgnoreCase) => "Media Player",
            _ => char.ToUpperInvariant(name[0]) + name[1..],
        };
    }
}
