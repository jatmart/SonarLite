using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SonarLite.Interop;

/// <summary>
/// Extracts a file's associated icon as a raw HICON via shell32 directly, so getting per-app tile
/// icons and the tray icon doesn't require loading System.Drawing.Common (which otherwise only
/// comes in transitively through UseWindowsForms -- see TrayIcon's doc comment for why that whole
/// framework was dropped).
/// </summary>
internal static class NativeIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Caller owns the returned handle and must pass it to <see cref="Destroy"/> when done.</summary>
    public static IntPtr Extract(string path, bool small = true)
    {
        var large = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        uint count = small
            ? ExtractIconEx(path, 0, null, smallIcons, 1)
            : ExtractIconEx(path, 0, large, null, 1);
        if (count == 0) return IntPtr.Zero;
        return small ? smallIcons[0] : large[0];
    }

    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }

    /// <summary>Converts and destroys the source HICON either way -- callers never need their own try/finally.</summary>
    public static System.Windows.Media.ImageSource? ToFrozenImageSourceAndDestroy(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Destroy(hIcon);
        }
    }
}
