using System.Diagnostics;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Cleans a fixed, auditable list of Windows history and cache locations plus OpenUninstaller's own
/// scan state. The category list is hard-coded: nothing from a program scan can add a target. The
/// cleaner is intentionally irreversible for privacy reasons and never follows reparse points.
/// </summary>
public sealed class PrivacyService
{
    private readonly AppPaths _paths;
    private readonly IRegistryStore _registry;
    private readonly IOperationLog _log;
    private readonly RegistryLocation _explorerRoot;

    private static readonly RegistryCategorySpec[] RegistryCategories =
    [
        new("run-history", "Run dialog history",
            "Commands typed into the Windows Run box (RunMRU).", "RunMRU", Recursive: false),
        new("typed-paths", "Explorer address bar history",
            "Paths typed into the File Explorer address bar (TypedPaths).", "TypedPaths", Recursive: false),
        new("recent-docs", "Recent documents",
            "Documents and folders listed under RecentDocs.", "RecentDocs", Recursive: true),
        new("user-assist", "Application launch history",
            "UserAssist counters that record which programs were launched and how often.", "UserAssist", Recursive: true)
    ];

    public PrivacyService(
        AppPaths paths,
        IRegistryStore registry,
        IOperationLog log,
        RegistryLocation? explorerRoot = null)
    {
        _paths = paths;
        _registry = registry;
        _log = log;
        _explorerRoot = explorerRoot ?? new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer");
    }

    public string RecentItemsDirectory => Path.Combine(
        _paths.AppData,
        "Microsoft",
        "Windows",
        "Recent");

    public string ThumbnailCacheDirectory => Path.Combine(
        _paths.LocalAppData,
        "Microsoft",
        "Windows",
        "Explorer");

    public string ScanHistoryDirectory => Path.Combine(_paths.DataDirectory, "ScanHistory");

    public PrivacyScanResult Scan(CancellationToken cancellationToken = default)
    {
        var result = new PrivacyScanResult();
        foreach (var spec in RegistryCategories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = RegistryLocationFor(spec);
            var (values, keys) = CountRegistry(location, spec.Recursive);
            result.Categories.Add(new PrivacyCategory
            {
                Id = spec.Id,
                Title = spec.Title,
                Description = spec.Description,
                ItemCount = values + keys
            });
        }

        result.Categories.Add(MeasureFiles(
            "recent-items",
            "Recent items and jump lists",
            "Shortcuts and jump-list entries under the per-user Recent folder.",
            RecentItemsDirectory,
            recursive: true,
            patterns: ["*"]));

        result.Categories.Add(MeasureFiles(
            "thumbnail-cache",
            "Thumbnail and icon cache",
            "Explorer's cached thumbnails and icons. Files currently held open by Explorer are skipped.",
            ThumbnailCacheDirectory,
            recursive: false,
            patterns: ["thumbcache_*.db", "iconcache_*.db"]));

        result.Categories.Add(MeasureAppHistory());
        return result;
    }

    public PrivacyCleanResult Clean(
        IEnumerable<string> categoryIds,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new PrivacyCleanResult { DryRun = dryRun };
        var selected = categoryIds
            .Select(id => id.Trim().ToLowerInvariant())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (id)
            {
                case "recent-items":
                    CleanFiles(
                        RecentItemsDirectory,
                        recursive: true,
                        patterns: ["*"],
                        dryRun,
                        result,
                        cancellationToken);
                    break;
                case "thumbnail-cache":
                    CleanFiles(
                        ThumbnailCacheDirectory,
                        recursive: false,
                        patterns: ["thumbcache_*.db", "iconcache_*.db"],
                        dryRun,
                        result,
                        cancellationToken);
                    break;
                case "app-history":
                    CleanAppHistory(dryRun, result);
                    break;
                default:
                {
                    var spec = RegistryCategories.FirstOrDefault(c => c.Id == id);
                    if (spec is null)
                    {
                        result.Messages.Add($"Unknown privacy category '{id}'.");
                        result.FailedItems++;
                        continue;
                    }

                    CleanRegistry(spec, dryRun, result);
                    break;
                }
            }
        }

        result.Duration = stopwatch.Elapsed;
        _log.Write(
            "privacy",
            dryRun ? "preview" : "clean",
            string.Join(", ", selected),
            result.Succeeded ? "completed" : "partial",
            $"{result.ItemsDeleted} item(s), {result.Bytes} byte(s)");
        return result;
    }

    private PrivacyCategory MeasureAppHistory()
    {
        var category = new PrivacyCategory
        {
            Id = "app-history",
            Title = "OpenUninstaller scan history",
            Description = "Stored leftover snapshots, ignored leftover ids and operation logs kept by this app."
        };

        foreach (var file in AppHistoryFiles())
        {
            category.ItemCount++;
            try
            {
                category.Bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The size is cosmetic.
            }
        }

        return category;
    }

    private IEnumerable<string> AppHistoryFiles()
    {
        var files = new List<string>();
        AddFiles(ScanHistoryDirectory, "*.json", recursive: false, files);
        AddFiles(_paths.DataDirectory, "ignored-leftovers.json", recursive: false, files);
        AddFiles(_paths.LogsDirectory, "*.jsonl", recursive: false, files);
        return files.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void CleanAppHistory(bool dryRun, PrivacyCleanResult result)
    {
        var files = AppHistoryFiles().ToList();
        foreach (var file in files)
        {
            long bytes = 0;
            try
            {
                bytes = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The size is cosmetic.
            }

            if (dryRun)
            {
                result.ItemsDeleted++;
                result.Bytes += bytes;
                continue;
            }

            try
            {
                File.Delete(file);
                result.ItemsDeleted++;
                result.Bytes += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.FailedItems++;
                result.Messages.Add($"{file}: {ex.Message}");
            }
        }
    }

    private PrivacyCategory MeasureFiles(
        string id,
        string title,
        string description,
        string root,
        bool recursive,
        string[] patterns)
    {
        var category = new PrivacyCategory { Id = id, Title = title, Description = description };
        foreach (var file in EnumerateFiles(root, recursive, patterns, category, out _))
        {
            category.ItemCount++;
            try
            {
                category.Bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The size is cosmetic; the clean reports per-file failures.
            }
        }

        return category;
    }

    private void CleanFiles(
        string root,
        bool recursive,
        string[] patterns,
        bool dryRun,
        PrivacyCleanResult result,
        CancellationToken cancellationToken)
    {
        var category = new PrivacyCategory { Id = root, Title = root, Description = root };
        var files = EnumerateFiles(root, recursive, patterns, category, out _).ToList();
        result.Messages.AddRange(category.Warning is null ? [] : new[] { category.Warning });

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bytes = 0;
            try
            {
                bytes = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The size is cosmetic.
            }

            if (dryRun)
            {
                result.ItemsDeleted++;
                result.Bytes += bytes;
                continue;
            }

            try
            {
                File.Delete(file);
                result.ItemsDeleted++;
                result.Bytes += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.FailedItems++;
                result.Messages.Add($"{file}: {ex.Message} (in use?)");
            }
        }
    }

    private HashSet<string> EnumerateFiles(
        string root,
        bool recursive,
        string[] patterns,
        PrivacyCategory category,
        out string? warning)
    {
        warning = null;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return found;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(root, pattern, options))
                {
                    if (PathGuard.IsUnder(file, root))
                    {
                        found.Add(file);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"{root}: {ex.Message}";
            category.Warning = warning;
        }

        return found;
    }

    private RegistryLocation RegistryLocationFor(RegistryCategorySpec spec)
        => new(
            _explorerRoot.Hive,
            _explorerRoot.View,
            $"{_explorerRoot.Path.TrimEnd('\\')}\\{spec.RelativePath}");

    private (int Values, int Keys) CountRegistry(RegistryLocation location, bool recursive)
    {
        try
        {
            if (!_registry.KeyExists(location))
            {
                return (0, 0);
            }

            var values = _registry.GetValueNames(location).Count;
            if (!recursive)
            {
                return (values, 0);
            }

            var keys = 0;
            var stack = new Stack<RegistryLocation>();
            stack.Push(location);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var child in _registry.GetSubKeyNames(current))
                {
                    var childLocation = new RegistryLocation(
                        current.Hive,
                        current.View,
                        $"{current.Path}\\{child}");
                    keys++;
                    values += _registry.GetValueNames(childLocation).Count;
                    if (keys < 20_000)
                    {
                        stack.Push(childLocation);
                    }
                }
            }

            return (values, keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (0, 0);
        }
    }

    private void CleanRegistry(RegistryCategorySpec spec, bool dryRun, PrivacyCleanResult result)
    {
        var location = RegistryLocationFor(spec);
        try
        {
            if (!_registry.KeyExists(location))
            {
                return;
            }

            foreach (var valueName in _registry.GetValueNames(location))
            {
                if (!dryRun)
                {
                    _registry.DeleteValue(location, valueName);
                }

                result.ItemsDeleted++;
            }

            if (!spec.Recursive)
            {
                return;
            }

            var subKeys = _registry.GetSubKeyNames(location);
            foreach (var child in subKeys)
            {
                var childLocation = new RegistryLocation(
                    location.Hive,
                    location.View,
                    $"{location.Path}\\{child}");
                var (values, keys) = CountRegistry(childLocation, recursive: true);
                if (!dryRun)
                {
                    _registry.DeleteKey(childLocation, recursive: true);
                }

                result.ItemsDeleted += values + keys + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            result.FailedItems++;
            result.Messages.Add($"{location.DisplayPath}: {ex.Message}");
        }
    }

    private static void AddFiles(string root, string pattern, bool recursive, List<string> files)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            files.AddRange(Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable history folder simply reports nothing.
        }
    }

    private sealed record RegistryCategorySpec(
        string Id,
        string Title,
        string Description,
        string RelativePath,
        bool Recursive);
}
