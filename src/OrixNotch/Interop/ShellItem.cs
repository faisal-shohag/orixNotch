using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrixNotch.Interop;

/// <summary>
/// Display name and icon of an installed app from its AppUserModelID, via the shell's
/// "AppsFolder" (the same place the Start menu reads them from).
/// </summary>
internal static class ShellItem
{
    private const uint SIGDN_NORMALDISPLAY = 0;
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public static string? AppName(string aumid)
    {
        if (!TryGet(aumid, out var item)) return null;
        try
        {
            item.GetDisplayName(SIGDN_NORMALDISPLAY, out var ptr);
            var name = Marshal.PtrToStringUni(ptr);
            Marshal.FreeCoTaskMem(ptr);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    public static ImageSource? AppIcon(string aumid, int size = 64)
    {
        if (!TryGet(aumid, out var item)) return null;
        var bitmap = IntPtr.Zero;
        try
        {
            ((IShellItemImageFactory)item).GetImage(new SIZE { cx = size, cy = size }, 0, out bitmap);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(item);
        }
    }

    private static bool TryGet(string aumid, out IShellItem item)
    {
        item = null!;
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        var iid = IID_IShellItem;
        var hr = SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, ref iid, out var obj);
        if (hr != 0 || obj is not IShellItem shell) return false;
        item = shell;
        return true;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }
}
