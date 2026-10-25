using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

public sealed record FileSnapshotEntry(string Path, bool IsDirectory, long SizeBytes, DateTime LastWriteUtc);

public sealed class FileSnapshot
{
    public Dictionary<string, FileSnapshotEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Truncated { get; set; }
}

/// <summary>
/// Indexes file names and sizes under selected roots. Junction points are never followed, and
/// entries under OpenUninstaller's own data directories or temporary folders are skipped.
/// </summary>
public sealed class FileSnapshotter
{
    private readonly AppPaths _paths;

    public FileSnapshotter(AppPaths paths)
    {
        _paths = paths;
    }

    public FileSnapshot Capture(
        IReadOnlyList<string> roots,
        int maxEntries = 150_000,
        int maxDepth = 8,
        CancellationToken cancellationToken = default)
    {
        var snapshot = new FileSnapshot();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Entries.Count >= maxEntries)
                {
                    snapshot.Truncated = true;
                    return snapshot;
                }

                var (current, depth) = stack.Pop();
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (snapshot.Entries.Count >= maxEntries)
                    {
                        snapshot.Truncated = true;
                        return snapshot;
                    }

                    if (IsExcluded(child))
                    {
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    long size = 0;
                    DateTime lastWrite;
                    try
                    {
                        if (isDirectory)
                        {
                            lastWrite = Directory.GetLastWriteTimeUtc(child);
                        }
                        else
                        {
                            var info = new FileInfo(child);
                            size = info.Length;
                            lastWrite = info.LastWriteTimeUtc;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    snapshot.Entries[child] = new FileSnapshotEntry(child, isDirectory, size, lastWrite);
                    if (isDirectory && depth + 1 < maxDepth)
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }
        }

        return snapshot;
    }

    public static FileSnapshotDiff Diff(FileSnapshot before, FileSnapshot after, int maxEntries = 5000)
    {
        var added = new List<TracedFile>();
        var truncated = before.Truncated || after.Truncated;
        foreach (var (path, entry) in after.Entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (added.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            if (!before.Entries.ContainsKey(path))
            {
                added.Add(new TracedFile
                {
                    Path = entry.Path,
                    IsDirectory = entry.IsDirectory,
                    SizeBytes = entry.SizeBytes
                });
            }
        }

        var shallowest = DropChildrenOfAddedDirectories(added);
        return new FileSnapshotDiff { AddedPaths = shallowest, Truncated = truncated };
    }

    private static List<TracedFile> DropChildrenOfAddedDirectories(List<TracedFile> entries)
    {
        var directories = entries
            .Where(e => e.IsDirectory)
            .Select(e => e.Path.TrimEnd('\\') + "\\")
            .ToList();

        return entries
            .Where(entry => !directories.Any(dir =>
                entry.Path.Length > dir.Length
                && entry.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static readonly string[] ExcludedFilePatterns =
    [
        @"\Microsoft\Windows\ClipSVC",
        @"\System Volume Information",
        @"\$Recycle.Bin",
        @"\Windows\WinSxS",
        @"\Windows\assembly"
    ];

    private bool IsExcluded(string path)
    {
        if (IsUnderRoot(path, _paths.DataDirectory) || IsUnderRoot(path, _paths.LocalDataDirectory))
        {
            return true;
        }

        if (IsUnderRoot(path, _paths.Temp))
        {
            return true;
        }

        foreach (var pattern in ExcludedFilePatterns)
        {
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderRoot(string path, string root)
        => !string.IsNullOrWhiteSpace(root)
           && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
