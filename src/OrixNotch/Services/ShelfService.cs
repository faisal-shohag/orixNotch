using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace OrixNotch.Services;

public sealed class ShelfItem
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    private ImageSource? _icon;
    private bool _iconLoaded;

    public string Path { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>True when the file lives in OrixNotch's own shelf folder (dropped text etc.).</summary>
    public bool Owned { get; set; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    [JsonIgnore] public bool IsFolder => Directory.Exists(Path);
    [JsonIgnore] public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    [JsonIgnore]
    public ImageSource? Icon
    {
        get
        {
            if (_iconLoaded) return _icon;
            _iconLoaded = true;
            try
            {
                if (ImageExtensions.Contains(System.IO.Path.GetExtension(Path)) && File.Exists(Path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 96;
                    bmp.UriSource = new Uri(Path);
                    bmp.EndInit();
                    bmp.Freeze();
                    _icon = bmp;
                }
                else if (File.Exists(Path))
                {
                    using var icon = Drawing.Icon.ExtractAssociatedIcon(Path);
                    if (icon is not null)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        _icon = src;
                    }
                }
            }
            catch
            {
                // Fall back to the glyph in the view.
            }
            return _icon;
        }
    }

    [JsonIgnore] public bool HasIcon => Icon is not null;
}

/// <summary>Temporary holding area for files dragged onto the notch.</summary>
public sealed class ShelfService
{
    private const string FileName = "shelf.json";

    public static ShelfService Instance { get; } = new();

    public ObservableCollection<ShelfItem> Items { get; }

    private ShelfService()
    {
        var items = Storage.Load<List<ShelfItem>>(FileName);
        var days = App.Settings.ShelfRetentionDays;
        foreach (var item in items.ToList())
        {
            var expired = days > 0 && DateTime.Now - item.AddedAt > TimeSpan.FromDays(days);
            if (expired || !item.Exists)
            {
                items.Remove(item);
                if (item.Owned) TryDelete(item.Path);
            }
        }
        Items = new ObservableCollection<ShelfItem>(items);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Insert(0, new ShelfItem { Path = path });
        }
        Save();
    }

    public void AddText(string text)
    {
        var name = $"Text {DateTime.Now:yyyy-MM-dd HHmmss}.txt";
        var path = System.IO.Path.Combine(Storage.Dir("shelf"), name);
        File.WriteAllText(path, text);
        Items.Insert(0, new ShelfItem { Path = path, Owned = true });
        Save();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        if (item.Owned) TryDelete(item.Path);
        Save();
    }

    public void Clear()
    {
        foreach (var item in Items.Where(i => i.Owned)) TryDelete(item.Path);
        Items.Clear();
        Save();
    }

    public void Save() => Storage.Save(FileName, Items.ToList());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore locked files.
        }
    }
}
