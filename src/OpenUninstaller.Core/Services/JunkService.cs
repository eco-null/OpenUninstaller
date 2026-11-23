using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Allow-listed junk scanning and cleaning. Only children of known roots are touched; recent files
/// are skipped so an active application's temp data is not pulled out from under it. Every root is
/// either a well-known cache location or a folder that Windows recreates automatically.
/// </summary>
public sealed class JunkService
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);

    public static readonly string[] JunkPatterns =
    [
        "*.tmp", "*.temp", "*.old", "*.gid", "*.fts", "*.$$$",
        "*.---", "*.??$", "*.___", "*.~mp", "*._mp", "*.$db",
        "*.db$", "*.dmp", "thumbs.db", "*.??~"
    ];

    // Default exclusion masks for the pattern search: system, cache and quarantine areas.
    // These are report filters, never an allow-list for deletion.
    public static readonly string[] DefaultExcludedPatterns =
    [
        "*/sendto/", "*/i386/", "*/resources/themes/", "*/temporary internet files/",
        "*/application data/aim/", "*/system32/usmt/", "*/system32/dtclog/",
        "*/norton antivirus/quarantine/", "*/temp/incredimail/", "%CommonProgramFiles%/",
        "%ProgramFiles%/uninstall information/", "%windir%/security/", "?:/recycle?/",
        "?:/_restore/", "?:/system volume information/", "*/system32/catroot2/",
        "*/symantec shared/virusdefs/", "*/paprport/data/", "*/chaos32/",
        "*/microsoft/money/webcache/", "*/microsoft/office/data/", "*/microsoft publisher/pagewiz/",
        "*/application data/microsoft/", "?:/$recycle*/", "*.~enc", "%windir%/twain_32/",
        "%CommonProgramFiles(x86)%/", "%ProgramFiles(x86)%/uninstall information/",
        "%windir%/system32/catroot/", "%windir%/ServiceProfiles/LocalService/AppData/Local/FontCache/"
    ];

    // Always-on exclusions applied alongside the configurable default list.
    private static readonly string[] BuiltInExcludedPatterns =
    [
        "%localappdata%/Microsoft/Windows/Notifications/",
        "%windir%/System32/config/systemprofile/AppData/Local/Microsoft/Windows/Notifications/",
        "%ProgramFiles%/WindowsApps/",
        "%windir%/System32/drivers/"
    ];

    public static readonly string[] ExcludedDirectories =
    [
        "$Recycle.Bin",
        "Recycled",
        "System Volume Information",
        "WinSxS",
        "assembly",
        "servicing",
        "System32",
        "SysWOW64",
        "INF",
        "Installer",
        "Panther",
        "SoftwareDistribution",
        "CatRoot",
        "CatRoot2",
        "Crypto",
        "Windows Defender",
        "Microsoft\\Protect",
        "Security",
        "Config",
        "DriverStore",
        "Boot",
        "Recovery",
        "CSC",
        "Prefetch",
        "WindowsApps",
        "Package Cache",
        "Documents",
        "Desktop",
        "Downloads",
        "Pictures",
        "Music",
        "Videos",
        "Saved Games",
        ".ssh",
        ".aws",
        ".azure",
        ".kube",
        ".gnupg",
        ".docker",
        ".gcloud",
        ".oci",
        ".terraform.d"
    ];

    public static bool IsExcluded(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        foreach (var excluded in ExcludedDirectories)
        {
            if (normalized.Contains(@"\" + excluded + @"\", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(@"\" + excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] ChromiumCacheSubdirectories =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Media Cache",
        "GrShaderCache",
        "ShaderCache"
    ];

    private static readonly string[] ApplicationCacheSubdirectories = ["Cache", "Code Cache", "GPUCache", "DawnCache"];

    private static readonly string[] BrowserProcessNames =
    [
        "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "chromium"
    ];

    private readonly AppPaths _paths;
    private readonly PathGuard _guard;
    private readonly IOperationLog _log;
    private readonly TimeProvider _timeProvider;

    public JunkService(AppPaths paths, PathGuard guard, IOperationLog log, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _guard = guard;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public JunkScanResult Scan(CancellationToken cancellationToken = default)
        => Scan(new JunkScanOptions(), null, cancellationToken);

    public JunkScanResult Scan(IProgress<JunkCategory>? progress, CancellationToken cancellationToken = default)
        => Scan(new JunkScanOptions(), progress, cancellationToken);

    public JunkScanResult Scan(JunkScanOptions options, IProgress<JunkCategory>? progress = null,
        CancellationToken cancellationToken = default)
        => ScanCore(options, progress, null, cancellationToken);

    private JunkScanResult ScanCore(JunkScanOptions options, IProgress<JunkCategory>? progress,
        HashSet<string>? selectedCategories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinimumAge < TimeSpan.Zero || options.MinimumAge > TimeSpan.FromDays(36500))
            throw new ArgumentOutOfRangeException(nameof(options), L("MinimumAgeMustBeBetweenZeroAnd100"));
        ValidatePatterns(options.Patterns);
        var stopwatch = Stopwatch.StartNew();
        var result = new JunkScanResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = options.IncludeKnownLocations ? BuildDefinitions().ToList() : [];
        if (options.SearchRoots.Count > 0)
            definitions.Add(new Definition(new JunkCategory
            {
                Id = "pattern-search", Title = "Additional folder and drive matches",
                Description = L("WildcardMatchesInSelectedLocationsReportOnlyFilename"),
                CanClean = false
            }, options.SearchRoots, options.Patterns.Count > 0 ? options.Patterns : JunkPatterns,
                recurse: true, recentWindow: options.MinimumAge)
            {
                CompatibilityExclusions = options.UseDefaultExclusions
                    ? DefaultExcludedPatterns.Concat(BuiltInExcludedPatterns).Select(ExpandMask).ToArray()
                    : []
            });
        if (options.IncludeRoots.Count > 0)
            definitions.Add(new Definition(new JunkCategory
            {
                Id = "included-folders", Title = "Explicitly included folder contents",
                Description = L("AllInspectableRegularFilesInIncludedFoldersRegardless"),
                CanClean = false
            }, options.IncludeRoots, [], recurse: true));
        if (selectedCategories is { Count: > 0 })
            definitions = definitions.Where(definition => selectedCategories.Contains(definition.Category.Id)).ToList();
        var browsers = RunningBrowserNames();
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = definition.Category;
            if (definition.IsRecycleBin)
            {
                var (bytes, items) = RecycleBin.Query();
                category.Bytes = bytes;
                category.ItemCount = items;
            }
            else
            {
                if (category.Id == "browser-cache" && browsers.Count > 0)
                    AddWarning(category, F("IsRunningInUseFilesAreSkipped", string.Join(", ", browsers)));
                var cutoff = _timeProvider.GetUtcNow().UtcDateTime - (definition.RecentWindow ?? RecentWindow);
                foreach (var root in definition.Roots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsValidRoot(root, category)) continue;
                    if (!Directory.Exists(root))
                    {
                        if (!category.CanClean)
                        {
                            category.IsComplete = false;
                            AddWarning(category, F("SelectedSearchFolderDoesNotExistOrIs", root));
                        }
                        continue;
                    }
                    foreach (var entry in ReadChildren(root, category, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Excluded(entry, options, definition)) { category.SkippedItems++; continue; }
                        // A known cache tree is admitted atomically: an excluded, recent or unverifiable
                        // descendant keeps the whole tree intact. Directory timestamps are not file activity.
                        if (!definition.RecurseSubdirectories && Directory.Exists(entry))
                        {
                            if (definition.Patterns.Count > 0) continue;
                            var tree = new List<JunkFile>();
                            if (CollectTree(entry, root, definition, options, cutoff, tree, cancellationToken))
                                foreach (var file in tree) AddFile(category, file, seen);
                        }
                        else
                        {
                            CollectFiles(entry, root, definition, options, cutoff, category, seen, cancellationToken);
                        }
                    }
                }
            }
            result.Categories.Add(category);
            progress?.Report(category);
        }
        return result with { Duration = stopwatch.Elapsed };
    }

    public JunkCleanResult Clean(IEnumerable<string> categoryIds, bool dryRun, CancellationToken cancellationToken = default)
    {
        var requested = categoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scan = ScanCore(new JunkScanOptions(), null, requested, cancellationToken);
        var selected = scan.Categories.Where(category => requested.Count == 0 || requested.Contains(category.Id)).ToList();
        var result = CleanFiles(selected.Where(category => category.CanClean).SelectMany(category => category.Files), dryRun, cancellationToken);
        foreach (var category in selected.Where(category => !category.CanClean))
            result.Messages.Add(F("ReportOnlyInThisVersion", Label(category.Title)));
        if (selected.Count == 0) result.Messages.Add(L("NoMatchingJunkCategories"));
        if (selected.Any(category => category.Id == "recycle-bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (bytes, items) = RecycleBin.Query();
            if (dryRun || RecycleBin.Empty(out _))
            {
                result.FreedBytes += bytes;
                result.DeletedItems += items;
                result.Messages.Add(F(dryRun ? "EmptyRecycleBinPreview" : "EmptyRecycleBinResult", items));
                if (!dryRun) _log.Write("junk", "recycle-bin", "recycle-bin", "emptied", $"{items} items");
            }
            else { result.FailedItems++; result.Messages.Add(L("RecycleBinCouldNotBeEmptied")); }
        }
        return result;
    }

    /// <summary>Cleans exactly the reviewed files, after rechecking identity, timestamps and trusted roots.</summary>
    public JunkCleanResult CleanFiles(IEnumerable<JunkFile> files, bool dryRun, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new JunkCleanResult { DryRun = dryRun };
        var definitions = BuildDefinitions().Where(definition => definition.Category.CanClean && !definition.IsRecycleBin)
            .ToDictionary(definition => definition.Category.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            var deleting = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.CanClean || !definitions.TryGetValue(file.CategoryId, out var definition) ||
                !definition.Roots.Contains(file.Root, StringComparer.OrdinalIgnoreCase) ||
                !IsValidRoot(file.Root, definition.Category) ||
                !_guard.ValidateJunkItem(file.Path, file.Root).Allowed || IsExcluded(file.Path) ||
                !Matches(file.Path, definition.Patterns))
            {
                result.SkippedItems++;
                result.Messages.Add(F("ReportOnlyOrNoLongerWithinATrusted", file.Path));
                continue;
            }
            try
            {
                using var opened = new JunkFileHandle(file.Path, file.Root, file.CategoryId, true, forDeletion: true);
                var current = opened.File;
                var cutoff = _timeProvider.GetUtcNow().UtcDateTime - (definition.RecentWindow ?? RecentWindow);
                if (current.Identity != file.Identity || current.Bytes != file.Bytes ||
                    current.CreationTimeUtc != file.CreationTimeUtc || current.LastWriteUtc != file.LastWriteUtc ||
                    current.LastAccessUtc != file.LastAccessUtc || current.LastWriteUtc > cutoff)
                {
                    result.SkippedItems++;
                    result.Messages.Add(F("ChangedOrUsedSinceTheScanScanAgain", file.Path));
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!dryRun) { deleting = true; opened.Delete(); }
                result.FreedBytes += current.Bytes;
                result.DeletedItems++;
                if (!dryRun) _log.Write("junk", "delete", file.Path, "deleted", $"{current.Bytes} bytes");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (deleting) result.FailedItems++; else result.SkippedItems++;
                result.Messages.Add(F(deleting ? "JunkFileFailed" : "JunkFileSkipped", file.Path, ex.Message));
                _log.Write("junk", "delete", file.Path, deleting ? "failed" : "skipped", ex.Message);
            }
        }
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private void CollectFiles(string entry, string root, Definition definition, JunkScanOptions options,
        DateTime cutoff, JunkCategory category, HashSet<string> seen, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(entry);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Excluded(current, options, definition)) { category.SkippedItems++; continue; }
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                { category.SkippedItems++; continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (definition.RecurseSubdirectories)
                        foreach (var child in ReadChildren(current, category, cancellationToken).AsEnumerable().Reverse())
                            pending.Push(child);
                }
                else if (Matches(current, definition.Patterns) && TryInspect(current, root, definition, options, cutoff, out var file))
                    AddFile(category, file!, seen);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                category.SkippedItems++;
                category.IsComplete = false;
                AddWarning(category, $"{current}: {ex.Message}");
            }
        }
    }

    private bool CollectTree(string entry, string root, Definition definition, JunkScanOptions options,
        DateTime cutoff, List<JunkFile> files, CancellationToken cancellationToken)
    {
        var category = definition.Category;
        var pending = new Stack<string>();
        pending.Push(entry);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Excluded(current, options, definition))
            {
                category.SkippedItems++;
                AddWarning(category, F("SubtreeRetainedBecauseIsExcludedOrProtected", entry, current));
                return false;
            }
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                {
                    category.SkippedItems++;
                    AddWarning(category, F("SubtreeRetainedBecauseADescendantIsAReparse", entry));
                    return false;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!_guard.ValidateJunkCandidate(current, root).Allowed)
                    {
                        category.SkippedItems++;
                        AddWarning(category, F("SubtreeRetainedBecauseIsProtected", entry, current));
                        return false;
                    }
                    // Enumeration must finish successfully before this tree is eligible.
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                    { cancellationToken.ThrowIfCancellationRequested(); pending.Push(child); }
                }
                else
                {
                    if (!TryInspect(current, root, definition, options, cutoff, out var file)) return false;
                    files.Add(file!);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                category.SkippedItems++;
                category.IsComplete = false;
                AddWarning(category, $"{current}: {ex.Message}");
                return false;
            }
        }
        return true;
    }

    private bool TryInspect(string path, string root, Definition definition, JunkScanOptions options,
        DateTime cutoff, out JunkFile? file)
    {
        file = null;
        if (definition.Category.Id is not ("pattern-search" or "included-folders") &&
            !_guard.ValidateJunkCandidate(path, root).Allowed)
        { definition.Category.SkippedItems++; return false; }
        try
        {
            using var handle = new JunkFileHandle(path, root, definition.Category.Id, definition.Category.CanClean, false);
            var candidate = handle.File;
            var activity = definition.Category.Id == "pattern-search" && options.UseLastAccessTime
                ? candidate.LastAccessUtc : candidate.LastWriteUtc;
            // The main traversal admits only access times strictly before the cutoff.
            // Its separate Include traversal never applies the main masks or timestamp filter.
            var patternSearch = definition.Category.Id == "pattern-search";
            if (definition.Category.Id != "included-folders" &&
                (!patternSearch || options.UseAgeFilter) &&
                (patternSearch ? activity >= cutoff : activity > cutoff))
            { definition.Category.SkippedItems++; return false; }
            file = definition.Category.Id == "included-folders"
                ? candidate with { Reason = L("ReportOnlyInclusionRequestsDiscoveryNotPermissionTo") }
                : candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            definition.Category.SkippedItems++;
            definition.Category.IsComplete = false;
            AddWarning(definition.Category, $"{path}: {ex.Message}");
            return false;
        }
    }

    private static void AddFile(JunkCategory category, JunkFile file, HashSet<string> seen)
    {
        if (!seen.Add(file.Path)) return;
        category.Files.Add(file);
        category.Bytes += file.Bytes;
        category.ItemCount++;
    }

    private static List<string> ReadChildren(string root, JunkCategory category, CancellationToken token)
    {
        var entries = new List<string>();
        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(root))
            { token.ThrowIfCancellationRequested(); entries.Add(child); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            category.IsComplete = false;
            AddWarning(category, $"{root}: {ex.Message}");
        }
        return entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool IsValidRoot(string root, JunkCategory category)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
                root.Split(['\\', '/']).Any(part => part == ".."))
                throw new IOException(L("OnlyAbsoluteLocalPathsWithoutParentTraversalAre"));
            var normalized = Path.GetFullPath(root);
            if (category.Id is "user-temp" or "pattern-remnants")
            {
                string[] broadRoots = [_paths.SystemDrive, _paths.UserProfile, _paths.ProgramFiles,
                    _paths.ProgramFilesX86, _paths.WindowsDirectory, _paths.LocalAppData, _paths.AppData,
                    _paths.AppDataLocalLow, _paths.ProgramData, _paths.Public,
                    _paths.DataDirectory, _paths.LocalDataDirectory, _paths.AppBaseDirectory];
                if (string.Equals(Path.TrimEndingDirectorySeparator(normalized),
                        Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!), StringComparison.OrdinalIgnoreCase) ||
                    broadRoots.Any(protectedRoot => !string.IsNullOrWhiteSpace(protectedRoot) &&
                        (string.Equals(Path.TrimEndingDirectorySeparator(normalized), Path.TrimEndingDirectorySeparator(protectedRoot),
                            StringComparison.OrdinalIgnoreCase) || PathGuard.IsUnder(protectedRoot, normalized))))
                    throw new IOException(L("ConfiguredTemporaryFolderIsAProtectedRootOr"));
                if (PathGuard.IsUnder(normalized, _paths.ProgramFiles) || PathGuard.IsUnder(normalized, _paths.ProgramFilesX86) ||
                    PathGuard.IsUnder(normalized, _paths.DataDirectory) || PathGuard.IsUnder(normalized, _paths.LocalDataDirectory) ||
                    PathGuard.IsUnder(normalized, _paths.AppBaseDirectory))
                    throw new IOException(L("ConfiguredTemporaryFolderOverlapsInstalledApplicationsOrOpenUninstaller"));
                var localTemp = Path.Combine(_paths.LocalAppData, "Temp");
                if (string.Equals(Path.TrimEndingDirectorySeparator(normalized), Path.TrimEndingDirectorySeparator(_paths.Temp),
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(normalized), localTemp, StringComparison.OrdinalIgnoreCase) &&
                    !PathGuard.IsUnder(normalized, localTemp) &&
                    !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized)), "Temp", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized)), "Tmp", StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L("ConfiguredTemporaryFolderIsNotARecognizedTemp"));
            }
            for (string? current = normalized; current is not null; current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(L("SearchRootsAndTheirAncestorsMayNotBe"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            category.IsComplete = false;
            AddWarning(category, $"{root}: {ex.Message}");
            return false;
        }
    }

    private bool Excluded(string path, JunkScanOptions options, Definition definition)
    {
        // Included folders are a separate pass after the main/excluded-mask traversal.
        if (definition.Category.Id == "included-folders") return false;
        if (definition.Category.CanClean && IsExcluded(path)) return true;
        if (definition.Category.Id == "pattern-search" &&
            definition.CompatibilityExclusions.Any(pattern => MatchesExcludedPath(path, pattern))) return true;
        foreach (var excluded in options.ExcludedPaths)
        {
            if (excluded.IndexOfAny(['*', '?']) >= 0)
            {
                if (MatchesExcludedPath(path, ExpandMask(excluded))) return true;
            }
            else if (Path.IsPathFullyQualified(excluded) &&
                (string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(excluded),
                    StringComparison.OrdinalIgnoreCase) || PathGuard.IsUnder(path, excluded))) return true;
        }
        return false;
    }

    internal string ExpandMask(string mask)
    {
        var expanded = mask
            .Replace("%localappdata%", _paths.LocalAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%appdata%", _paths.AppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%userprofile%", _paths.UserProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%programdata%", _paths.ProgramData, StringComparison.OrdinalIgnoreCase)
            .Replace("%allusersprofile%", _paths.ProgramData, StringComparison.OrdinalIgnoreCase)
            .Replace("%public%", _paths.Public, StringComparison.OrdinalIgnoreCase)
            .Replace("%systemdrive%", _paths.SystemDrive.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            .Replace("%temp%", _paths.Temp, StringComparison.OrdinalIgnoreCase)
            .Replace("%tmp%", _paths.Temp, StringComparison.OrdinalIgnoreCase)
            .Replace("%systemroot%", _paths.WindowsDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("%windir%", _paths.WindowsDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles(x86)%", _paths.ProgramFilesX86, StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles%", _paths.ProgramFiles, StringComparison.OrdinalIgnoreCase)
            .Replace("%CommonProgramFiles(x86)%", Path.Combine(_paths.ProgramFilesX86, "Common Files"), StringComparison.OrdinalIgnoreCase)
            .Replace("%CommonProgramFiles%", Path.Combine(_paths.ProgramFiles, "Common Files"), StringComparison.OrdinalIgnoreCase);
        // Known injected paths win over process environment values, including during isolated tests.
        if (expanded.Contains('%')) expanded = Environment.ExpandEnvironmentVariables(expanded);
        expanded = expanded.Replace('\\', '/');
        if (mask.StartsWith('%') && expanded.Length >= 2 && expanded[1] == ':' && char.IsAsciiLetter(expanded[0]))
            expanded = "?" + expanded[1..];
        return expanded;
    }

    private static bool MatchesExcludedPath(string path, string pattern)
    {
        var normalized = path.Replace('\\', '/');
        var mask = pattern.Replace('\\', '/').Trim();
        if (mask.EndsWith('/')) mask += "*";
        return MatchesMask(mask, normalized) || MatchesMask(mask, normalized + "/");
    }

    private static bool Matches(string path, IReadOnlyList<string> patterns)
        => patterns.Count == 0 || patterns.Any(pattern => MatchesMask(pattern, Path.GetFileName(path)));

    // Ordinary * / ? backtracking with C-locale ASCII uppercase comparison.
    internal static bool MatchesMask(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        var maskIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var retryIndex = 0;
        while (valueIndex < value.Length)
        {
            if (maskIndex < pattern.Length && pattern[maskIndex] == '*')
            {
                starIndex = ++maskIndex;
                retryIndex = valueIndex;
            }
            else if (maskIndex < pattern.Length &&
                     (pattern[maskIndex] == '?' || Fold(pattern[maskIndex]) == Fold(value[valueIndex])))
            {
                maskIndex++;
                valueIndex++;
            }
            else if (starIndex >= 0)
            {
                maskIndex = starIndex;
                valueIndex = ++retryIndex;
            }
            else return false;
        }
        while (maskIndex < pattern.Length && pattern[maskIndex] == '*') maskIndex++;
        return maskIndex == pattern.Length;

        static char Fold(char value) => value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;
    }

    private static void ValidatePatterns(IReadOnlyList<string> patterns)
    {
        if (patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 255 ||
            pattern.IndexOfAny(['\\', '/', ':', '\0']) >= 0))
            throw new ArgumentException(L("PatternsMustBeFilenameMasksUsingAndWithout"), nameof(patterns));
    }

    private IEnumerable<Definition> BuildDefinitions()
    {
        string Local(params string[] parts) => Path.Combine([_paths.LocalAppData, .. parts]);
        string ProgramData(params string[] parts) => Path.Combine([_paths.ProgramData, .. parts]);

        yield return new Definition(
            new JunkCategory
            {
                Id = "user-temp",
                Title = "User temporary files",
                Description = L("ContentsOfTheUserTempFoldersOlderThan")
            },
            [_paths.Temp, Local("Temp")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "pattern-remnants",
                Title = "Temporary and backup file remnants",
                Description = L("FilesMatchingTemporaryPatternsTmpTempOldGid"),
            },
            [_paths.Temp, Local("Temp"), ProgramData("Temp"), Local("CrashDumps")],
            JunkPatterns,
            recurse: true,
            recentWindow: TimeSpan.FromHours(24));

        yield return new Definition(
            new JunkCategory
            {
                Id = "windows-temp",
                Title = "Windows temporary files",
                Description = L("WindowsSystemAndServiceAccountTemporaryFoldersOlder"),
                RequiresElevation = true
            },
            [
                Path.Combine(_paths.WindowsDirectory, "Temp"),
                Path.Combine(_paths.WindowsDirectory, "SystemTemp"),
                Path.Combine(_paths.WindowsDirectory, "ServiceProfiles", "LocalService", "AppData", "Local", "Temp"),
                Path.Combine(_paths.WindowsDirectory, "ServiceProfiles", "NetworkService", "AppData", "Local", "Temp")
            ],
            []);

        yield return new Definition(new JunkCategory
        {
            Id = "shared-temp", Title = "Shared temporary files",
            Description = L("ProgramDataTempContentsOlderThan24Hours"), RequiresElevation = true
        }, [ProgramData("Temp")], [], recentWindow: TimeSpan.FromHours(24));

        var packageCategory = new JunkCategory
        {
            Id = "app-temp", Title = "Windows app temporary files",
            Description = L("PackagedApplicationTempStateAndACTempFoldersApplication")
        };
        yield return new Definition(packageCategory, PackageTempRoots(packageCategory), []);

        yield return new Definition(new JunkCategory
        {
            Id = "application-caches", Title = "Application render caches",
            Description = L("SlackDiscordAndVisualStudioCodeRenderCaches")
        }, ApplicationCacheRoots(), []);

        var browserCategory = new JunkCategory
        {
            Id = "browser-cache", Title = "Browser caches",
            Description = L("ChromeEdgeBraveVivaldiOperaAndFirefoxCaches")
        };
        yield return new Definition(browserCategory, BrowserCacheRoots(browserCategory), []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "thumbnails",
                Title = "Thumbnail and icon caches",
                Description = L("WindowsExplorerCacheDatabasesRecreatedAutomatically")
            },
            [Local("Microsoft", "Windows", "Explorer")],
            ["thumbcache_*.db", "iconcache_*.db"]);

        yield return new Definition(
            new JunkCategory
            {
                Id = "crash-reports",
                Title = "Crash dumps and error reports",
                Description = L("UserCrashDumpsAndWindowsErrorReportingQueues")
            },
            [
                Local("CrashDumps"),
                Local("Microsoft", "Windows", "WER"),
                ProgramData("Microsoft", "Windows", "WER")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "system-error-reports",
                Title = "System crash dumps",
                Description = L("KernelMinidumpsAndLiveKernelReportsWrittenBy"),
                RequiresElevation = true
            },
            [
                Path.Combine(_paths.WindowsDirectory, "Minidump"),
                Path.Combine(_paths.WindowsDirectory, "LiveKernelReports")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "shader-caches",
                Title = "Graphics shader caches",
                Description = L("Direct3DNVIDIAAMDAndIntelShaderCachesRebuilt")
            },
            [
                Local("D3DSCache"),
                Local("NVIDIA", "DXCache"),
                Local("NVIDIA", "GLCache"),
                Local("NVIDIA", "ComputeCache"),
                Local("AMD", "DxCache"),
                Local("AMD", "DxcCache"),
                Local("AMD", "GLCache"),
                Local("Intel", "ShaderCache")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "internet-cache",
                Title = "Windows internet cache",
                Description = L("WinINetWinHTTPCacheUsedByWindowsComponentsAnd")
            },
            [Local("Microsoft", "Windows", "INetCache")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "delivery-optimization",
                Title = "Delivery Optimization cache",
                Description = L("WindowsUpdatePeerToPeerDownloadCacheWindows"),
                RequiresElevation = true
            },
            [
                Path.Combine(
                    _paths.WindowsDirectory,
                    "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows",
                    "DeliveryOptimization", "Cache")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "developer-caches",
                Title = "Developer package caches",
                Description = "npm, pip, NuGet and Yarn download caches. They are rebuilt on demand and are not project sources."
            },
            [
                Local("npm-cache"),
                Path.Combine(_paths.AppData, "npm-cache"),
                Local("pip", "Cache"),
                Local("NuGet", "v3-cache"),
                Local("NuGet", "plugins-cache"),
                Local("Yarn", "Cache"),
                Local("uv", "cache"),
                Local("go-build")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "windows-logs",
                Title = "Windows servicing logs",
                Description = L("CBSDISMAndWindowsUpdateLogsReportedOnly"),
                CanClean = false,
                RequiresElevation = true
            },
            [
                Path.Combine(_paths.WindowsDirectory, "Logs", "CBS"),
                Path.Combine(_paths.WindowsDirectory, "Logs", "DISM"),
                Path.Combine(_paths.WindowsDirectory, "Logs", "WindowsUpdate")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "prefetch",
                Title = "Windows prefetch data",
                Description = L("BootAndApplicationPrefetchFilesReportedOnlyRemoving"),
                CanClean = false,
                RequiresElevation = true
            },
            [Path.Combine(_paths.WindowsDirectory, "Prefetch")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "windows-update",
                Title = "Windows Update download cache",
                Description = L("ReportedOnlyRemovingItCanInterruptPendingUpdates"),
                CanClean = false,
                RequiresElevation = true
            },
            [Path.Combine(_paths.WindowsDirectory, "SoftwareDistribution", "Download")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "old-windows",
                Title = "Previous Windows installation",
                Description = L("ReportedOnlyWindowsOldEnablesRollbackRemoveIt"),
                CanClean = false,
                RequiresElevation = true
            },
            [Path.Combine(_paths.SystemDrive, "Windows.old")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "recycle-bin",
                Title = "Recycle bin",
                Description = L("EmptiedThroughTheWindowsShellCannotBeUndone")
            },
            [],
            [])
        {
            IsRecycleBin = true
        };
    }

    /// <summary>
    /// Enumerates every Chromium/Firefox/Opera profile that exists, not just the default profile.
    /// Missing roots are harmless: the scanner skips them.
    /// </summary>
    private List<string> BrowserCacheRoots(JunkCategory category)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Local(params string[] parts) => Path.Combine([_paths.LocalAppData, .. parts]);
        string Roaming(params string[] parts) => Path.Combine([_paths.AppData, .. parts]);

        var chromiumBases = new[]
        {
            Local("Google", "Chrome", "User Data"),
            Local("Google", "Chrome Beta", "User Data"),
            Local("Google", "Chrome Dev", "User Data"),
            Local("Google", "Chrome SxS", "User Data"),
            Local("Microsoft", "Edge", "User Data"),
            Local("Microsoft", "Edge Beta", "User Data"),
            Local("Microsoft", "Edge Dev", "User Data"),
            Local("Microsoft", "Edge SxS", "User Data"),
            Local("BraveSoftware", "Brave-Browser", "User Data"),
            Local("Vivaldi", "User Data"),
            Local("Chromium", "User Data")
        };

        foreach (var baseDirectory in chromiumBases)
        {
            foreach (var subdirectory in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
                if (Directory.Exists(Path.Combine(baseDirectory, subdirectory)))
                    roots.Add(Path.Combine(baseDirectory, subdirectory));
            foreach (var profile in EnumerateChromiumProfiles(baseDirectory, category))
            {
                foreach (var subdirectory in ChromiumCacheSubdirectories)
                {
                    var candidate = Path.Combine(profile, subdirectory);
                    if (Directory.Exists(candidate))
                    {
                        roots.Add(candidate);
                    }
                }
            }
        }

        foreach (var operaBase in new[]
                 {
                     Roaming("Opera Software", "Opera Stable"),
                     Roaming("Opera Software", "Opera GX Stable"),
                     Local("Opera Software", "Opera Stable"),
                     Local("Opera Software", "Opera GX Stable")
                 })
        {
            foreach (var subdirectory in new[] { "Cache", "Code Cache", "GPUCache", "Media Cache" })
            {
                var candidate = Path.Combine(operaBase, subdirectory);
                if (Directory.Exists(candidate))
                {
                    roots.Add(candidate);
                }
            }
        }

        foreach (var firefoxBase in new[]
                 {
                     Roaming("Mozilla", "Firefox", "Profiles"),
                     Local("Mozilla", "Firefox", "Profiles")
                 })
        {
            foreach (var profile in EnumerateDirectories(firefoxBase, category))
            {
                foreach (var subdirectory in new[] { "cache2", "startupCache", "shader-cache" })
                {
                    var candidate = Path.Combine(profile, subdirectory);
                    if (Directory.Exists(candidate))
                    {
                        roots.Add(candidate);
                    }
                }
            }
        }

        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> PackageTempRoots(JunkCategory category) => EnumerateDirectories(Path.Combine(_paths.LocalAppData, "Packages"), category)
        .SelectMany(package => new[] { Path.Combine(package, "TempState"), Path.Combine(package, "AC", "Temp") })
        .Where(Directory.Exists).ToList();

    private List<string> ApplicationCacheRoots()
    {
        var bases = new[]
        {
            Path.Combine(_paths.AppData, "Slack"), Path.Combine(_paths.AppData, "discord"),
            Path.Combine(_paths.AppData, "discordcanary"), Path.Combine(_paths.AppData, "discordptb"),
            Path.Combine(_paths.AppData, "Code"), Path.Combine(_paths.AppData, "Code - Insiders")
        };
        return bases.SelectMany(root => ApplicationCacheSubdirectories
            .Select(child => Path.Combine(root, child))).Where(Directory.Exists).ToList();
    }

    private IEnumerable<string> EnumerateChromiumProfiles(string userDataDirectory, JunkCategory category)
    {
        if (!Directory.Exists(userDataDirectory))
        {
            return [];
        }

        var profiles = new List<string>();
        foreach (var name in new[] { "Default", "Guest Profile", "System Profile" })
        {
            var candidate = Path.Combine(userDataDirectory, name);
            if (Directory.Exists(candidate))
            {
                profiles.Add(candidate);
            }
        }

        profiles.AddRange(EnumerateDirectories(userDataDirectory, category)
            .Where(path => Path.GetFileName(path).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)));
        return profiles.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private List<string> EnumerateDirectories(string root, JunkCategory category)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        if (!IsValidRoot(root, category)) return [];
        try
        {
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            category.IsComplete = false;
            AddWarning(category, F("ProfileDiscoveryFailed", root, ex.Message));
            return [];
        }
    }

    private static HashSet<string> RunningBrowserNames()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BrowserProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    running.Add(name);
                }
                foreach (var process in processes) process.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return running;
    }

    private static void AddWarning(JunkCategory category, string warning)
        => category.AddWarning(warning);

    private sealed class Definition
    {
        public Definition(
            JunkCategory category,
            IReadOnlyList<string> roots,
            IReadOnlyList<string> patterns,
            bool recurse = false,
            TimeSpan? recentWindow = null)
        {
            Category = category;
            Roots = roots.Select(Path.TrimEndingDirectorySeparator)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Patterns = patterns;
            RecurseSubdirectories = recurse;
            RecentWindow = recentWindow;
        }

        public JunkCategory Category { get; }
        public IReadOnlyList<string> Roots { get; }
        public IReadOnlyList<string> Patterns { get; }
        public bool RecurseSubdirectories { get; }
        public TimeSpan? RecentWindow { get; }
        public IReadOnlyList<string> CompatibilityExclusions { get; init; } = [];
        public bool IsRecycleBin { get; init; }
    }
}
