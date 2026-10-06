using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrixNotch.Services;

/// <summary>Name and photo shown in the Home greeting.</summary>
public static class ProfileService
{
    private const string AvatarFile = "avatar.png";

    public static event Action? Changed;

    public static string DisplayName =>
        string.IsNullOrWhiteSpace(App.Settings.DisplayName) ? Environment.UserName : App.Settings.DisplayName.Trim();

    public static string FirstName
    {
        get
        {
            var first = DisplayName.Split(' ', '.', '_')[0];
            return first.Length == 0 ? "there" : char.ToUpper(first[0]) + first[1..];
        }
    }

    public static void SetName(string name)
    {
        App.Settings.DisplayName = name.Trim();
        SettingsService.Save(notify: false);
        Changed?.Invoke();
    }

    public static bool HasAvatar => File.Exists(Storage.PathOf(AvatarFile));

    /// <summary>Loads the saved photo without locking the file (so it can be replaced).</summary>
    public static ImageSource? LoadAvatar()
    {
        var path = Storage.PathOf(AvatarFile);
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Center-crops the chosen picture to a square, scales it to 160 px and stores it.</summary>
    public static void SetAvatar(string sourcePath)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(sourcePath);
        src.EndInit();

        var side = Math.Min(src.PixelWidth, src.PixelHeight);
        var crop = new CroppedBitmap(src, new System.Windows.Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
        var scale = 160.0 / side;
        var scaled = new TransformedBitmap(crop, new ScaleTransform(scale, scale));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using (var fs = File.Create(Storage.PathOf(AvatarFile))) encoder.Save(fs);
        Changed?.Invoke();
    }

    public static void RemoveAvatar()
    {
        var path = Storage.PathOf(AvatarFile);
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke();
    }

    /// <summary>Opens a picture picker; returns false if cancelled or the file isn't an image.</summary>
    public static bool PickAvatar(System.Windows.Window? owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a profile picture",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return false;
        try
        {
            SetAvatar(dialog.FileName);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }
}
