using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenUninstaller.App.Infrastructure;

/// <summary>Loads a file's associated icon through the Windows shell, without extra packages.</summary>
public static class IconLoader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x00000080;

    public static ImageSource? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidate = path.Trim().Trim('"');
        var comma = candidate.LastIndexOf(',');
        if (comma > 2 && int.TryParse(candidate[(comma + 1)..], out _))
        {
            candidate = candidate[..comma];
        }

        if (!File.Exists(candidate))
        {
            return null;
        }

        var info = new ShFileInfo();
        try
        {
            var result = SHGetFileInfo(
                candidate,
                FileAttributeNormal,
                ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon | ShgfiUseFileAttributes);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero)
            {
                DestroyIcon(info.hIcon);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
