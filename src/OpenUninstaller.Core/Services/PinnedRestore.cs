using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenUninstaller.Core.Safety;

namespace OpenUninstaller.Core.Services;

// All modifications are transacted; directory objects remain pinned until commit. Ancestor
// directories each have the next pinned directory as a child, so they cannot become junctions.
// The deepest directory is held as a transactional writer before any child is created.
// Ordinary share modes do not block attribute-only opens; TxF locks reparse metadata:
// https://learn.microsoft.com/en-us/windows/win32/fileio/txf-basic-concepts
// https://learn.microsoft.com/en-us/windows/win32/fileio/programming-considerations-for-transacted-fileio-
// TxF requires supported local NTFS and permissions; unsupported or conflicting cases fail closed.
internal sealed class PinnedRestore : IDisposable
{
    private const uint ReadData = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100;
    private const uint GenericWrite = 0x40000000, ShareReadWrite = 3, ShareAll = 7;
    private const uint OpenExisting = 3, OpenAlways = 4, CreateNew = 1;
    private const uint DirectoryFlag = 0x02000000, NoFollow = 0x00200000;
    private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;
    private readonly PathGuard _guard;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<string, string>? _hook;
    private readonly SafeFileHandle _transaction;
    private readonly List<SafeFileHandle> _handles = [];
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);
    private bool _committed;

    public PinnedRestore(PathGuard guard, Action<string, string>? hook, CancellationToken cancellationToken)
    {
        _guard = guard;
        _cancellationToken = cancellationToken;
        _hook = hook;
        _transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, null);
        if (_transaction.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _transaction.Dispose();
            throw Failure("Could not start a filesystem transaction", error);
        }
    }

    public void Restore(string source, string target, bool overwrite)
    {
        var full = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(full) ?? throw new IOException("The restore target has no parent.");
        var root = Path.GetPathRoot(parent) ?? throw new IOException("The restore target has no root.");
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Network paths cannot be restored safely.");
        var current = root;
        PinExisting(current);
        foreach (var component in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var next = Path.Combine(current, component);
            if (_directories.ContainsKey(current) && !Directory.Exists(next))
            {
                EnsureDirectory(next);
            }
            else if (!PinExisting(next, allowMissing: true))
            {
                LockDirectory(current);
                EnsureDirectory(next);
            }
            current = next;
        }
        // A pin alone cannot block FILE_WRITE_ATTRIBUTES-only handles. TxF supplies that lock.
        LockDirectory(current);
        if (Directory.Exists(source)) CopyDirectory(source, full, overwrite);
        else if (File.Exists(source)) CopyFile(source, full, overwrite);
        else throw new IOException("Archive entry is missing.");
        _cancellationToken.ThrowIfCancellationRequested();
        _hook?.Invoke("before-commit", target);
        _cancellationToken.ThrowIfCancellationRequested();
        if (!CommitTransaction(_transaction)) throw Failure("Could not commit the restore transaction");
        _committed = true;
    }

    private void CopyDirectory(string source, string target, bool overwrite)
    {
        CheckTarget(target, true);
        EnsureDirectory(target, requireNew: !overwrite);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, destination, overwrite);
            else CopyFile(entry, destination, overwrite);
        }
    }

    private void CopyFile(string source, string target, bool overwrite)
    {
        CheckTarget(target, false);
        _hook?.Invoke("before-file-open", target);
        var handle = CreateFileTransactedW(target, GenericWrite | ReadAttributes, 0, IntPtr.Zero,
            overwrite ? OpenAlways : CreateNew, NoFollow, IntPtr.Zero, _transaction, IntPtr.Zero, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure("Could not open the restore file"); }
        try
        {
            ValidateHandle(handle, target, directory: false);
            _hook?.Invoke("after-file-open", target);
            _cancellationToken.ThrowIfCancellationRequested();
            using var output = new FileStream(handle, FileAccess.Write, 81920, isAsync: false);
            output.SetLength(0);
            using var input = File.OpenRead(source);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
            if (!GetFileTime(input.SafeFileHandle, out var creation, out var access, out var write)
                || !SetFileTime(handle, ref creation, ref access, ref write))
                throw Failure("Could not preserve restored file timestamps");
        }
        finally { handle.Dispose(); }
    }

    private void EnsureDirectory(string path, bool requireNew = false)
    {
        if (_directories.ContainsKey(path)) return;
        if (requireNew && !CreateDirectoryTransactedW(null, path, IntPtr.Zero, _transaction))
            throw Failure("Could not create a new restore directory");
        var handle = OpenTransactionDirectory(path);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (2 or 3)) throw Failure("Could not lock a restore directory", error);
            if (!CreateDirectoryTransactedW(null, path, IntPtr.Zero, _transaction))
                throw Failure("Could not create a restore directory");
            handle = OpenTransactionDirectory(path);
            if (handle.IsInvalid) { handle.Dispose(); throw Failure("Could not lock the new restore directory"); }
        }
        try { ValidateHandle(handle, path, directory: true); }
        catch { handle.Dispose(); throw; }
        _handles.Add(handle);
        _directories.Add(path, handle);
    }

    private void LockDirectory(string path)
    {
        if (_directories.ContainsKey(path)) return;
        var handle = OpenTransactionDirectory(path);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure("Could not lock a restore directory"); }
        try { ValidateHandle(handle, path, directory: true); }
        catch { handle.Dispose(); throw; }
        _handles.Add(handle);
        _directories.Add(path, handle);
    }

    private SafeFileHandle OpenTransactionDirectory(string path) => CreateFileTransactedW(path,
        ReadAttributes | WriteAttributes, ShareAll, IntPtr.Zero, OpenExisting,
        DirectoryFlag | NoFollow, IntPtr.Zero, _transaction, IntPtr.Zero, IntPtr.Zero);

    private bool PinExisting(string path, bool allowMissing = false)
    {
        // The held child prevents reparse conversion of each ancestor. Allow unrelated
        // writers but never share delete, so an ancestor or its pinned child cannot be renamed.
        var handle = CreateFileW(path, ReadData | ReadAttributes, ShareReadWrite, IntPtr.Zero,
            OpenExisting, DirectoryFlag | NoFollow, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (allowMissing && error is 2 or 3) return false;
            throw Failure("Could not pin a restore directory", error);
        }
        try { ValidateHandle(handle, path, directory: true); }
        catch { handle.Dispose(); throw; }
        _handles.Add(handle);
        return true;
    }

    private void CheckTarget(string target, bool directory)
    {
        var guard = _guard.ValidateForDeletion(target, recursive: directory);
        if (!guard.Allowed) throw new IOException(guard.Reason);
    }

    private static void ValidateHandle(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Failure("Could not inspect a restore handle");
        if ((info.Attributes & ReparseAttribute) != 0 || ((info.Attributes & DirectoryAttribute) != 0) != directory)
            throw new IOException("The restore target is a reparse point or has an unexpected type.");
        if (!directory && info.NumberOfLinks != 1)
            throw new IOException("The restore file has multiple hard links.");
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw Failure("Could not resolve a restore handle");
        var resolved = new string(buffer, 0, (int)length);
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The restore target resolved outside its declared path.");
    }

    public void Dispose()
    {
        if (!_committed) RollbackTransaction(_transaction);
        for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _transaction.Dispose();
    }

    private static IOException Failure(string message, int? error = null)
        => new($"{message}: {new Win32Exception(error ?? Marshal.GetLastWin32Error()).Message} " +
            "Transactional restore requires local NTFS, TxF support, and permission to lock directory metadata; encrypted files are unsupported.");

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileTransactedW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template, SafeFileHandle transaction, IntPtr miniVersion, IntPtr extended);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryTransactedW(string? template, string name, IntPtr security, SafeFileHandle transaction);
    [StructLayout(LayoutKind.Sequential)] private struct NativeFileTime { public uint Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileTime(SafeFileHandle handle, out NativeFileTime creation, out NativeFileTime access, out NativeFileTime write);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle handle, ref NativeFileTime creation, ref NativeFileTime access, ref NativeFileTime write);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr uow, uint options, uint isolation, uint flags, uint timeout, string? description);
    [DllImport("ktmw32.dll", SetLastError = true)] private static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("ktmw32.dll", SetLastError = true)] private static extern bool RollbackTransaction(SafeFileHandle transaction);
}
