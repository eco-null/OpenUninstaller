using System.Runtime.InteropServices;

namespace OpenUninstaller.Core.Support;

/// <summary>Thin wrapper over the Shell recycle-bin APIs.</summary>
public static class RecycleBin
{
    private const uint NoConfirmation = 0x00000001;
    private const uint NoProgressUi = 0x00000002;
    private const uint NoSound = 0x00000004;

    public static (long Bytes, int Items) Query()
    {
        try
        {
            var info = new ShQueryRbInfo { cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
            var hr = SHQueryRecycleBin(null, ref info);
            return hr == 0 ? (info.i64Size, checked((int)info.i64NumItems)) : (0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (0, 0);
        }
    }

    public static bool Empty(out string? error)
    {
        error = null;
        try
        {
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
            if (hr == 0)
            {
                return true;
            }

            error = $"Shell returned 0x{hr:X8}.";
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, BestFitMapping = false)]
    private static extern int SHQueryRecycleBin(
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
        ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, BestFitMapping = false)]
    private static extern int SHEmptyRecycleBin(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
        uint dwFlags);
}
