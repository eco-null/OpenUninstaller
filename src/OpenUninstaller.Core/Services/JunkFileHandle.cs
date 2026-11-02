using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Services;

// Deletion is attached to the inspected file handle. A path rename or junction swap cannot
// redirect the deletion after validation. No recursive directory deletion is performed.
internal sealed class JunkFileHandle : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly bool _canDelete;
    public JunkFile File { get; }

    public JunkFileHandle(string path, string root, string category, bool canClean, bool forDeletion)
    {
        if (forDeletion && !canClean) throw new InvalidOperationException("A report-only file cannot be opened for deletion.");
        _canDelete = canClean && forDeletion;
        var reportOnly = !canClean && !forDeletion;
        // Discovery needs attributes, never file contents. Share the metadata handle so an open
        // application does not hide a report candidate; cleanup retains exclusive read/delete access.
        _handle = CreateFileW(path, (reportOnly ? 0x80u : 0x81u) | (forDeletion ? 0x10000u : 0),
            reportOnly ? 7u : 0u, IntPtr.Zero, 3,
            0x00300000, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw Failure("File is in use or cannot be inspected", error);
        }
        try
        {
            if (!GetFileInformationByHandle(_handle, out var info)) throw Failure("Could not inspect file identity");
            // Reports may describe encrypted or multiply linked files without reading their data.
            // Never follow directories/reparse points or recall offline content. Cleanup must still
            // reject encrypted data and multiply linked files, in addition to its exclusive handle.
            var forbiddenAttributes = 0x10u | 0x400u | 0x1000u | (reportOnly ? 0u : 0x4000u);
            if ((info.Attributes & forbiddenAttributes) != 0 || (!reportOnly && info.NumberOfLinks != 1))
                throw new IOException("File is a directory, reparse point, offline/encrypted file or has multiple hard links.");
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(_handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) throw Failure("Could not resolve file handle");
            var resolved = new string(buffer, 0, (int)length);
            if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
            if (!string.Equals(resolved, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("File resolved outside its declared path.");
            File = new JunkFile
            {
                Id = Path.GetFullPath(path), CategoryId = category, Root = root, Path = Path.GetFullPath(path),
                Identity = string.Create(CultureInfo.InvariantCulture,
                    $"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}"),
                Bytes = checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)),
                CreationTimeUtc = Stamp(info.CreationLow, info.CreationHigh),
                LastAccessUtc = Stamp(info.AccessLow, info.AccessHigh),
                LastWriteUtc = Stamp(info.WriteLow, info.WriteHigh), CanClean = canClean,
                Reason = canClean ? "Known temporary/cache location; unchanged file identity is required for cleaning."
                    : "Report-only: a matching filename does not establish that a file is disposable."
            };
        }
        catch { _handle.Dispose(); throw; }
    }

    public void Delete()
    {
        if (!_canDelete) throw new InvalidOperationException("This file handle is only authorized for inspection.");
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(_handle, 4, ref disposition, 1)) throw Failure("Could not delete inspected file");
    }

    public void Dispose() => _handle.Dispose();
    private static DateTime Stamp(uint low, uint high) => DateTime.FromFileTimeUtc((long)(((ulong)high << 32) | low));
    private static IOException Failure(string message, int? error = null)
        => new($"{message}: {new Win32Exception(error ?? Marshal.GetLastWin32Error()).Message}");

    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
}
