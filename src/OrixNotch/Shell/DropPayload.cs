using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using OrixNotch.Services;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace OrixNotch.Shell;

/// <summary>
/// What a drag onto the notch carries, read while the drop is still live: files from Explorer, an
/// image dragged out of a browser, or plain text. Browsers offer a dragged image as a "virtual file"
/// (FileGroupDescriptorW + FileContents) next to its link and HTML, so without this it would arrive as
/// a URL; <see cref="FilesAsync"/> saves it (or downloads the &lt;img&gt; source) as a real file.
/// </summary>
public sealed partial class DropPayload
{
    private const string Descriptor = "FileGroupDescriptorW";
    private const string Contents = "FileContents";

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".ico", ".tif", ".tiff" };

    private DropPayload()
    {
    }

    /// <summary>Real files on disk.</summary>
    public string[]? Files { get; private init; }

    /// <summary>The drag's text: what's kept for a text drop, and the fallback when an image can't be saved.</summary>
    public string? Text { get; private init; }

    private string? VirtualName { get; init; }
    private byte[]? VirtualBytes { get; init; }
    private string? ImageUrl { get; init; }

    public static bool CanAccept(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(Descriptor) || data.GetDataPresent(DataFormats.UnicodeText);

    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Whether the drag looks like it carries an image: an image file, a browser's virtual file, or an
    /// &lt;img&gt; / image link. Cheap enough for DragEnter/DragOver (doesn't read the file contents).
    /// </summary>
    public static bool HasImage(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop))
            return Try(() => data.GetData(DataFormats.FileDrop)) is string[] files && files.Any(IsImagePath);
        if (data.GetDataPresent(Descriptor)) return true;
        var text = Try(() => data.GetData(DataFormats.UnicodeText)) is string { Length: > 0 } t ? t : null;
        return Try(() => ImageSource(data, text)) is not null;
    }

    /// <summary>Reads the drop now (the data object stops working once the drop returns). Null when there's nothing usable.</summary>
    public static DropPayload? Read(IDataObject data)
    {
        // Each format is read on its own: a source that fails one (browsers are picky about how
        // FileContents is asked for) still delivers the others.
        if (Try(() => data.GetData(DataFormats.FileDrop)) is string[] { Length: > 0 } files) return new DropPayload { Files = files };

        // Text rides along with an image as the fallback when it can't be saved (e.g. the download fails).
        var text = Try(() => data.GetData(DataFormats.UnicodeText)) is string { Length: > 0 } t ? t : null;
        if (Try(() => ReadVirtualFile(data)) is { } file) return new DropPayload { VirtualName = file.Name, VirtualBytes = file.Bytes, Text = text };
        if (Try(() => ImageSource(data, text)) is { } url) return new DropPayload { ImageUrl = url, Text = text };
        return text is null ? null : new DropPayload { Text = text };
    }

    private static T? Try<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) // a source app can hand over broken data
        {
            App.Log(ex);
            return default;
        }
    }

    /// <summary>The dropped files, saving a browser image into <paramref name="saveDir"/> first. Null for text drops.</summary>
    public async Task<string[]?> FilesAsync(string saveDir)
    {
        if (Files is not null) return Files;
        try
        {
            if (VirtualBytes is not null)
            {
                var path = UniquePath(saveDir, VirtualName ?? "image");
                await File.WriteAllBytesAsync(path, VirtualBytes);
                return [path];
            }
            if (ImageUrl is not null) return await DownloadAsync(ImageUrl, saveDir) is { } saved ? [saved] : null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return null;
    }

    /// <summary>True when <see cref="FilesAsync"/> had to save copies (they belong to whoever keeps them).</summary>
    public bool CreatesFiles => Files is null && (VirtualBytes is not null || ImageUrl is not null);

    /// <summary>The first virtual file, as browsers offer a dragged image.</summary>
    private static (string Name, byte[] Bytes)? ReadVirtualFile(IDataObject data)
    {
        if (!data.GetDataPresent(Descriptor) || data.GetData(Descriptor) is not MemoryStream descriptor) return null;
        // FILEGROUPDESCRIPTORW: UINT count, then FILEDESCRIPTORW[count] (592 bytes each; the name is
        // WCHAR[260] at offset 72).
        var raw = descriptor.ToArray();
        if (raw.Length < 4 + 592 || BitConverter.ToInt32(raw, 0) < 1) return null;
        var name = Encoding.Unicode.GetString(raw, 4 + 72, 520);
        var end = name.IndexOf('\0');
        name = end >= 0 ? name[..end] : name;
        // A dragged link comes as an Internet shortcut: keep that as text (the URL) instead.
        if (Path.GetExtension(name).ToLowerInvariant() is ".url" or ".lnk" or ".webloc") return null;

        var bytes = VirtualFileContents(data, 0);
        return bytes is { Length: > 0 } ? (name.Length > 0 ? name : "image", bytes) : null;
    }

    /// <summary>
    /// FileContents for one file of the group. WPF asks with lindex -1, which Chromium-based browsers
    /// refuse, so ask the OLE data object directly with the file's index (HGLOBAL or IStream).
    /// </summary>
    private static byte[]? VirtualFileContents(IDataObject data, int index)
    {
        if (data is not ComTypes.IDataObject com)
            return data.GetData(Contents) is MemoryStream stream ? stream.ToArray() : null;

        var format = new ComTypes.FORMATETC
        {
            cfFormat = (short)DataFormats.GetDataFormat(Contents).Id,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTREAM,
        };
        com.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL)
            {
                var size = (int)GlobalSize(medium.unionmember);
                var source = GlobalLock(medium.unionmember);
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(source, bytes, 0, size);
                    return bytes;
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }
            }
            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM)
            {
                var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    using var output = new MemoryStream();
                    var buffer = new byte[1 << 16];
                    var read = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        while (true)
                        {
                            stream.Read(buffer, buffer.Length, read);
                            var n = Marshal.ReadInt32(read);
                            if (n <= 0) break;
                            output.Write(buffer, 0, n);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(read);
                    }
                    return output.ToArray();
                }
                finally
                {
                    Marshal.ReleaseComObject(stream);
                }
            }
            return null;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    /// <summary>The image a browser drag is about: the &lt;img&gt; in its HTML, or a link straight to an image file.</summary>
    private static string? ImageSource(IDataObject data, string? text)
    {
        // A dragged image's text is just its address; selected text that happens to include a picture is text.
        var textIsUrl = text is null || Uri.TryCreate(text.Trim(), UriKind.Absolute, out _);
        if (textIsUrl && data.GetData(DataFormats.Html) is string html && ImgSrc().Match(html) is { Success: true } img)
        {
            var src = System.Net.WebUtility.HtmlDecode(img.Groups[1].Value);
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return src;
        }
        if (text is not null && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && ImageExtensions.Contains(Path.GetExtension(uri.AbsolutePath)))
            return uri.ToString();
        return null;
    }

    private static async Task<string?> DownloadAsync(string url, string saveDir)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:image/png;base64,....
            var comma = url.IndexOf(',');
            var meta = url[5..Math.Max(5, comma)];
            if (comma < 0 || !meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            var type = meta[..meta.IndexOf(';')];
            var path = UniquePath(saveDir, "image" + ExtensionFor(type));
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(url[(comma + 1)..]));
            return path;
        }

        using var response = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) return null;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        if (name.Length == 0) name = "image";
        if (!ImageExtensions.Contains(Path.GetExtension(name))) name += ExtensionFor(mediaType);
        var target = UniquePath(saveDir, name);
        await using (var output = File.Create(target))
            await response.Content.CopyToAsync(output);
        return target;
    }

    private static string ExtensionFor(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/avif" => ".avif",
        "image/bmp" => ".bmp",
        _ => ".png",
    };

    private static string UniquePath(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "image";
        if (clean.Length > 120) clean = clean[..100] + Path.GetExtension(clean);
        var path = Path.Combine(dir, clean);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(clean)} ({n}){Path.GetExtension(clean)}");
        return path;
    }

    [GeneratedRegex("""<img[^>]+src\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();
}
