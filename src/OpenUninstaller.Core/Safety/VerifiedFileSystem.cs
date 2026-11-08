using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenUninstaller.Core.Safety;

/// <summary>
/// Deletes and pins items in trees that a less privileged user can modify. The maintenance service
/// runs as LocalSystem against %ProgramData%\OpenUninstaller, which the installing user owns, so a
/// path-based delete or create there could be redirected into a system folder by a junction or a
/// rename. Every handle here is opened without following reparse points and without delete
/// sharing, its resolved path must equal the requested path, and the operation is applied to that
/// handle, so the item cannot be swapped between the check and the change.
/// </summary>
public static class VerifiedFileSystem
{
    private const uint ListDirectory = 0x0001;
    private const uint ReadAttributes = 0x0080;
    private const uint WriteAttributes = 0x0100;
    private const uint DeleteAccess = 0x0001_0000;
    // DELETE takes part in share checks, so a held handle also blocks renames of the item.
    private const uint ModifyAccess = DeleteAccess | ReadAttributes | WriteAttributes;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint OpenFlags = 0x0200_0000 /* backup semantics */ | 0x0020_0000 /* open reparse point */;

    private const uint AttributeReadOnly = 0x1;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeNormal = 0x80;
    private const uint AttributeReparsePoint = 0x400;
    private const uint SettableAttributes = 0x1 | 0x2 | 0x4 | 0x20 | 0x100 | 0x1000 | 0x2000;

    private const int FileBasicInfo = 0;
    private const int FileDispositionInfo = 4;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Holds <paramref name="path"/> open until disposed. While it is held, neither the directory
    /// nor any of its ancestors can be renamed, so paths beneath it keep pointing into it.
    /// </summary>
    public static IDisposable PinDirectory(string path)
    {
        var full = Normalize(path);
        var handle = Open(full, ListDirectory | ReadAttributes);
        try
        {
            var info = Inspect(handle, full);
            if ((info.Attributes & (AttributeDirectory | AttributeReparsePoint)) != AttributeDirectory)
            {
                throw new IOException($"{full} is not a plain directory.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Deletes one file (or the reparse point itself), clearing a read-only attribute first.</summary>
    public static void DeleteFile(string path)
    {
        var full = Normalize(path);
        using var handle = Open(full, ModifyAccess);
        var info = Inspect(handle, full);
        if ((info.Attributes & (AttributeDirectory | AttributeReparsePoint)) == AttributeDirectory)
        {
            throw new IOException($"{full} is a directory.");
        }

        MarkForDeletion(handle, full, info);
    }

    /// <summary>
    /// Deletes a directory and everything in it. Reparse points inside the tree are removed
    /// themselves and never followed.
    /// </summary>
    public static void DeleteTree(string path)
    {
        var full = Normalize(path);
        using var handle = Open(full, ModifyAccess);
        DeleteOpened(handle, full, Inspect(handle, full));
    }

    private static void DeleteOpened(SafeFileHandle handle, string full, Information info)
    {
        if ((info.Attributes & (AttributeDirectory | AttributeReparsePoint)) == AttributeDirectory)
        {
            // The open handle pins this directory, so the enumerated names stay inside it; each
            // child is still verified on its own handle before it is touched.
            var names = Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName).ToList();
            foreach (var name in names)
            {
                var child = Path.Combine(full, name!);
                using var childHandle = Open(child, ModifyAccess);
                DeleteOpened(childHandle, child, Inspect(childHandle, child));
            }
        }

        MarkForDeletion(handle, full, info);
    }

    private static void MarkForDeletion(SafeFileHandle handle, string full, Information info)
    {
        if ((info.Attributes & AttributeReadOnly) != 0)
        {
            // Attributes belong to the file, not the link: never change a file other names share.
            // Deleting one name of a hard-linked file is fine; it leaves the other names intact.
            if ((info.Attributes & (AttributeDirectory | AttributeReparsePoint)) == 0 && info.NumberOfLinks != 1)
            {
                throw new IOException($"{full} is read-only and has multiple hard links.");
            }

            var attributes = info.Attributes & SettableAttributes & ~AttributeReadOnly;
            var basic = new BasicInformation { FileAttributes = attributes == 0 ? AttributeNormal : attributes };
            if (!SetFileInformationByHandle(handle, FileBasicInfo, ref basic, (uint)Marshal.SizeOf<BasicInformation>()))
            {
                throw Failure($"Could not clear the read-only attribute of {full}");
            }
        }

        var disposition = new DispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, 1))
        {
            throw Failure($"Could not delete {full}");
        }
    }

    private static SafeFileHandle Open(string full, uint access)
    {
        var handle = CreateFileW(full, access, ShareReadWrite, IntPtr.Zero, OpenExisting, OpenFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw Failure($"Could not open {full}", error);
        }

        return handle;
    }

    private static Information Inspect(SafeFileHandle handle, string full)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw Failure($"Could not inspect {full}");
        }

        var buffer = new char[32_768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw Failure($"Could not resolve {full}");
        }

        var resolved = new string(buffer, 0, (int)length);
        resolved = resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
            ? @"\\" + resolved[8..]
            : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(resolved), full, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"{full} resolves to {resolved}; a junction or rename redirected it.");
        }

        return info;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static Exception Failure(string message, int? error = null)
    {
        var code = error ?? Marshal.GetLastWin32Error();
        var text = $"{message}: {new Win32Exception(code).Message}";
        return code == ErrorAccessDenied ? new UnauthorizedAccessException(text) : new IOException(text);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispositionInformation
    {
        public byte DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle, int informationClass, ref BasicInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle, int informationClass, ref DispositionInformation information, uint size);
}
