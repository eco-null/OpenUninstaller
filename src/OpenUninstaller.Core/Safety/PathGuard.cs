using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Safety;

public sealed record GuardResult(bool Allowed, string? Reason, string? NormalizedPath)
{
    public static GuardResult Allow(string normalizedPath) => new(true, null, normalizedPath);
    public static GuardResult Reject(string reason, string? normalizedPath = null) => new(false, reason, normalizedPath);
}

/// <summary>
/// Decides whether a filesystem path may be deleted. This is the only place that answers that
/// question; services must not delete before a successful guard result.
/// </summary>
public sealed class PathGuard
{
    private readonly AppPaths _paths;
    private readonly string[] _protectedRoots;
    private readonly string[] _ownRoots;

    public PathGuard(AppPaths paths)
    {
        _paths = paths;
        _protectedRoots =
        [
            paths.SystemDrive,
            paths.WindowsDirectory,
            paths.ProgramFiles,
            paths.ProgramFilesX86,
            paths.ProgramData,
            paths.UserProfile,
            paths.AppData,
            paths.LocalAppData,
            paths.Public,
            paths.UserDesktop,
            paths.PublicDesktop,
            paths.UserStartMenuPrograms,
            paths.CommonStartMenuPrograms,
            paths.Temp,
            Path.Combine(paths.ProgramFiles, "Common Files"),
            Path.Combine(paths.ProgramFilesX86, "Common Files"),
            Path.Combine(paths.ProgramFiles, "Microsoft Shared"),
            Path.Combine(paths.ProgramFilesX86, "Microsoft Shared"),
            Path.Combine(paths.ProgramData, "Microsoft"),
            Path.Combine(paths.ProgramData, "Package Cache"),
            Path.Combine(paths.AppData, "Microsoft"),
            Path.Combine(paths.LocalAppData, "Microsoft"),
            Path.Combine(paths.LocalAppData, "Programs"),
            Path.Combine(paths.LocalAppData, "Packages"),
            Path.Combine(paths.UserProfile, "Documents"),
            Path.Combine(paths.UserProfile, "Downloads"),
            Path.Combine(paths.UserProfile, "Desktop"),
            Path.Combine(paths.UserProfile, "Pictures"),
            Path.Combine(paths.UserProfile, "Music"),
            Path.Combine(paths.UserProfile, "Videos"),
            Path.Combine(paths.UserProfile, "Saved Games"),
            Path.Combine(paths.UserProfile, "Favorites"),
            Path.Combine(paths.UserProfile, "Contacts"),
            Path.Combine(paths.UserProfile, "Links"),
            Path.Combine(paths.UserProfile, "Searches"),
            Path.Combine(paths.UserProfile, ".ssh"),
            Path.Combine(paths.UserProfile, ".aws"),
            Path.Combine(paths.UserProfile, ".azure")
        ];
        _ownRoots = [paths.DataDirectory, paths.LocalDataDirectory, paths.AppBaseDirectory];
    }

    public GuardResult ValidateForDeletion(string path, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GuardResult.Reject("Path is empty.");
        }

        var raw = path.Trim().Trim('"');
        if (ContainsParentTraversal(raw))
        {
            return GuardResult.Reject("Path contains a parent-directory traversal ('..').");
        }

        if (raw.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return GuardResult.Reject("UNC and device paths are not supported.");
        }

        if (!Path.IsPathRooted(raw))
        {
            return GuardResult.Reject("Path must be absolute.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardResult.Reject("Path is not valid.");
        }

        full = TrimTrailingSeparator(full);
        if (full.Length > 30000)
        {
            return GuardResult.Reject("Path is too long.");
        }

        if (!Path.IsPathFullyQualified(full))
        {
            return GuardResult.Reject("Path must be absolute.");
        }

        if (IsDriveRoot(full))
        {
            return GuardResult.Reject("Drive roots cannot be deleted.");
        }

        var depth = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 3)
        {
            return GuardResult.Reject("Refusing to delete a top-level folder.");
        }

        foreach (var own in _ownRoots)
        {
            if (IsSameOrUnder(full, own))
            {
                return GuardResult.Reject("This path belongs to OpenUninstaller itself.");
            }
        }

        if (IsUnder(full, _paths.WindowsDirectory))
        {
            return GuardResult.Reject("Windows system files are protected.");
        }

        if (IsSameOrUnder(full, Path.Combine(_paths.UserProfile, ".ssh"))
            || IsSameOrUnder(full, Path.Combine(_paths.UserProfile, ".aws"))
            || IsSameOrUnder(full, Path.Combine(_paths.UserProfile, ".azure"))
            || IsSameOrUnder(full, Path.Combine(_paths.UserProfile, ".kube"))
            || IsSameOrUnder(full, Path.Combine(_paths.UserProfile, ".gnupg"))
            || IsSameOrUnder(full, Path.Combine(_paths.UserProfile, ".docker")))
        {
            return GuardResult.Reject("Protected user credential directory.");
        }

        if (recursive)
        {
            string[] sensitivePersonalRoots =
            [
                Path.Combine(_paths.UserProfile, "Documents"),
                Path.Combine(_paths.UserProfile, "Desktop"),
                Path.Combine(_paths.UserProfile, "Downloads"),
                Path.Combine(_paths.UserProfile, "Pictures"),
                Path.Combine(_paths.UserProfile, "Music"),
                Path.Combine(_paths.UserProfile, "Videos"),
                Path.Combine(_paths.UserProfile, "Saved Games")
            ];

            foreach (var sensitive in sensitivePersonalRoots)
            {
                if (PathsEqual(full, sensitive))
                {
                    return GuardResult.Reject("Cannot recursively delete personal user root directory.");
                }
            }
        }

        foreach (var root in _protectedRoots)
        {
            if (root.Length > 0 && PathsEqual(full, root))
            {
                return GuardResult.Reject("This is a protected system or profile root.");
            }
        }

        return ValidatePathComponents(full);
    }

    /// <summary>
    /// Junk items are always children of an allow-listed category root. This check is stricter
    /// about the relationship to the root and skips the general Windows protection, because the
    /// caller has already established the root (for example <c>%WINDIR%\Temp</c>).
    /// </summary>
    public GuardResult ValidateJunkItem(string path, string categoryRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(categoryRoot))
        {
            return GuardResult.Reject("Path is empty.");
        }

        string fullRoot;
        string full;
        try
        {
            fullRoot = TrimTrailingSeparator(Path.GetFullPath(categoryRoot));
            full = TrimTrailingSeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardResult.Reject("Path is not valid.");
        }

        if (!Path.IsPathFullyQualified(full) || ContainsParentTraversal(path))
        {
            return GuardResult.Reject("Path must be absolute and may not contain '..'.");
        }

        if (PathsEqual(full, fullRoot))
        {
            return GuardResult.Reject("The category root itself is never deleted.");
        }

        if (!IsUnder(full, fullRoot))
        {
            return GuardResult.Reject("Path is outside the junk category root.");
        }

        foreach (var own in _ownRoots)
        {
            if (IsSameOrUnder(full, own))
            {
                return GuardResult.Reject("This path belongs to OpenUninstaller itself.");
            }
        }

        string[] sensitiveRoots =
        [
            Path.Combine(_paths.UserProfile, "Documents"),
            Path.Combine(_paths.UserProfile, "Downloads"),
            Path.Combine(_paths.UserProfile, "Desktop"),
            Path.Combine(_paths.UserProfile, "Pictures"),
            Path.Combine(_paths.UserProfile, "Music"),
            Path.Combine(_paths.UserProfile, "Videos"),
            Path.Combine(_paths.UserProfile, "Saved Games"),
            Path.Combine(_paths.UserProfile, "Favorites"),
            Path.Combine(_paths.UserProfile, "Contacts"),
            Path.Combine(_paths.UserProfile, "Links"),
            Path.Combine(_paths.UserProfile, "Searches"),
            Path.Combine(_paths.UserProfile, ".ssh"),
            Path.Combine(_paths.UserProfile, ".aws"),
            Path.Combine(_paths.UserProfile, ".azure")
        ];

        foreach (var sensitive in sensitiveRoots)
        {
            if (IsSameOrUnder(full, sensitive))
            {
                return GuardResult.Reject("Protected user personal directory.");
            }
        }

        foreach (var root in _protectedRoots)
        {
            if (PathsEqual(full, root))
            {
                return GuardResult.Reject("Path is a protected root directory.");
            }
        }

        return ValidatePathComponents(full);
    }

    private static GuardResult ValidatePathComponents(string full)
    {
        // A regular file can still be reached through a junction in any ancestor.
        for (string? component = full; component is not null; component = Path.GetDirectoryName(component))
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                {
                    return GuardResult.Reject("The path contains a reparse point (junction/symlink).", full);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Restore targets may not exist yet; their existing ancestors still need checking.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GuardResult.Reject("Could not verify the path's attributes.", full);
            }
        }

        return GuardResult.Allow(full);
    }

    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = TrimTrailingSeparator(Path.GetFullPath(path));
        var normalizedRoot = TrimTrailingSeparator(Path.GetFullPath(root));
        if (normalizedPath.Length <= normalizedRoot.Length)
        {
            return false;
        }

        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrUnder(string path, string root)
        => PathsEqual(path, root) || IsUnder(path, root);

    private static bool PathsEqual(string a, string b)
        => string.Equals(TrimTrailingSeparator(a), TrimTrailingSeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsDriveRoot(string path)
        => string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsParentTraversal(string raw)
        => raw.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..");

    private static string TrimTrailingSeparator(string path)
        => path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
