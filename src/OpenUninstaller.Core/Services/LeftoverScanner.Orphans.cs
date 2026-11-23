using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Orphaned leftovers: data that no installed application claims any more. Unlike the
/// per-application scan, there is no product name to look for, so every candidate is compared
/// against the whole installed inventory (desktop applications and Store packages) instead.
///
/// Evidence, strongest first:
/// 1. an install trace whose application is no longer installed (exact, preselected);
/// 2. a shortcut whose target is missing on a present fixed drive (high, preselected);
/// 3. an earlier leftover scan of an application that is no longer installed;
/// 4. Store app data for a package that is no longer registered, an empty application folder,
///    or a scheduled task whose program is missing;
/// 5. a folder or HKCU key that no installed application or package name matches (review only).
/// </summary>
public sealed partial class LeftoverScanner
{
    public const string OrphanScanId = "orphaned-leftovers";

    /// <summary>
    /// Stands in for an application in the review workflow, the ignored list and scan history,
    /// so orphan results use the same review, backup and deletion path as any other scan.
    /// </summary>
    public static InstalledProgram OrphanScanTarget { get; } = new()
    {
        Id = OrphanScanId,
        Name = "Orphaned leftovers",
        Location = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64,
            @"Software\OpenUninstaller\OrphanedLeftovers")
    };

    public static bool IsOrphanScanTarget(InstalledProgram program)
        => string.Equals(program.Id, OrphanScanId, StringComparison.Ordinal);

    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(180);

    /// <summary>Windows, runtime and package-manager folders that no uninstall entry owns.</summary>
    private static readonly HashSet<string> OrphanFolderExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Comms", "ConnectedDevicesPlatform", "D3DSCache", "PlaceholderTileLogoFolder", "Publishers",
        "VirtualStore", "CrashDumps", "ElevatedDiagnostics", "PeerDistRepub", "History", "IsolatedStorage",
        "Temporary Internet Files", "SquirrelTemp", "Programs", "Package Cache", "Packages", "ssh",
        "USOPrivate", "USOShared", "SoftwareDistribution", "WindowsHolographicDevices",
        "Uninstall Information", "ModifiableWindowsApps", "WindowsApps", "Internet Explorer", "MSBuild",
        "Reference Assemblies", "Windows Kits", "Windows Sidebar", "Windows Mail", "Windows NT",
        "Windows Photo Viewer", "Windows Media Player", "WindowsPowerShell", "PowerShell", "Common Files",
        "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "Application Data", "Downloaded Installations",
        "Downloaded Program Files", "npm", "npm-cache", "pip", "pnpm", "pnpm-cache", "pnpm-state", "Yarn",
        "node-gyp", "go-build", "ms-playwright", "fontconfig", "cache", "caches", "logs", "log", "Updates",
        "Sun", "regid.1991-06.com.microsoft", "Device Stage", "DeviceSync", "Mozilla Maintenance Service",
        "Crashpad", "CrashReports", "SystemTemp", "WinGet", "Microsoft_Corporation",
        // Shared audio plug-in folders are used by every host application but registered by none.
        "VST", "VST2", "VST3", "VSTPlugins", "Steinberg", "CLAP", "Avid", "AAX", "Designer",
        "Application Verifier", "VulkanRT"
    };

    /// <summary>Direct HKCU\Software children owned by Windows or shared by many products.</summary>
    private static readonly HashSet<string> OrphanRegistryExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppDataLow", "Policies", "Wow6432Node", "Classes", "Clients", "Chromium", "IM Providers", "Netscape",
        "ODBC", "VB and VBA Program Settings", "Khronos", "RegisteredApplications", "System", "Windows",
        "Windows NT", "Microsoft", "OpenUninstaller", "Intel", "Description", "Partner",
        "GitForWindows", "JavaSoft", "Python", "CVSM", "Realtek", "Synaptics", "Conexant", "AMD", "ATI",
        "NVIDIA Corporation", "WinRAR SFX", "Sysinternals"
    };

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageFamilyName();

    /// <summary>
    /// Finds data left behind by applications that are no longer installed. Nothing found here
    /// is owned by an installed application: candidates that one owns or whose name one claims are
    /// skipped, and only exact or structural evidence is preselected.
    /// </summary>
    public Task<LeftoverScanResult> ScanOrphansAsync(
        IReadOnlyList<InstalledProgram> installed,
        IReadOnlyList<LeftoverScanSnapshot>? history = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var target = OrphanScanTarget;
            var result = new LeftoverScanResult { ProgramId = target.Id, ProgramName = target.Name };
            var packageFamilies = RegisteredPackageFamilies(out var packageNames);
            var claims = new NameClaims(installed, packageNames);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Comparing install traces with installed applications"));
            AddOrphanTraceItems(installed, claims, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Comparing earlier leftover scans with installed applications"));
            AddOrphanHistoryItems(history, installed, claims, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Comparing application folders with installed applications"));
            AddOrphanFolderItems(installed, claims, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Comparing user registry keys with installed applications"));
            AddOrphanRegistryItems(claims, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Checking shortcuts and Store app data"));
            AddOrphanShortcutItems(installed, result, cancellationToken);
            AddOrphanPackageDataItems(packageFamilies, installed, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Checking scheduled tasks"));
            AddOrphanTaskItems(installed, result, cancellationToken);

            var consolidated = Consolidate(result.Items, result.Warnings);
            result.Items.Clear();
            result.Items.AddRange(consolidated);
            ApplyIgnoredItems(target, result);
            FillSizes(result.Items, cancellationToken);
            result.Duration = stopwatch.Elapsed;
            result.ApplyDefaultSelection();
            progress?.Report(new OperationProgress(OperationStages.Completed, $"{result.Items.Count} orphaned candidate(s) found"));
            return result;
        }, cancellationToken);
    }

    private void AddOrphanTraceItems(
        IReadOnlyList<InstalledProgram> installed,
        NameClaims claims,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        foreach (var trace in _traces.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (installed.Any(program => TraceStore.MatchScore(trace, program) > 0) || claims.Claims(trace.ProgramName))
            {
                continue;
            }

            AddTraceItems(trace, OrphanScanTarget, installed, result, cancellationToken,
                $" Recorded by the install trace of {trace.ProgramName} ({trace.CreatedAt.LocalDateTime:yyyy-MM-dd}); no installed application matches it.");
        }
    }

    private void AddOrphanHistoryItems(
        IReadOnlyList<LeftoverScanSnapshot>? history,
        IReadOnlyList<InstalledProgram> installed,
        NameClaims claims,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        if (history is null)
        {
            return;
        }

        foreach (var snapshot in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(snapshot.ProgramId, OrphanScanId, StringComparison.Ordinal)
                || installed.Any(program => string.Equals(program.Id, snapshot.ProgramId, StringComparison.Ordinal))
                || claims.Claims(snapshot.ProgramName))
            {
                continue;
            }

            var reason = $"Listed as a leftover of {snapshot.ProgramName} on {snapshot.ScannedAt.LocalDateTime:yyyy-MM-dd}; no installed application matches it.";
            foreach (var item in snapshot.Items.Where(i => !i.IsProtected && i.Confidence >= LeftoverConfidence.Strong))
            {
                if (item.Kind is LeftoverKind.File or LeftoverKind.Directory)
                {
                    var path = item.DisplayPath;
                    var isDirectory = Directory.Exists(path);
                    if ((!isDirectory && !File.Exists(path))
                        || FindOwningProgram(path, OrphanScanTarget, installed) is not null
                        || !_pathGuard.ValidateForDeletion(path, recursive: isDirectory).Allowed)
                    {
                        continue;
                    }

                    result.Items.Add(new LeftoverItem
                    {
                        Id = $"file|{path.ToLowerInvariant()}",
                        Kind = isDirectory ? LeftoverKind.Directory : LeftoverKind.File,
                        DisplayPath = path,
                        FilePath = path,
                        Confidence = LeftoverConfidence.Strong,
                        Reason = reason
                    });
                }
                else if (item.Kind == LeftoverKind.RegistryKey && ParseRegistryId(item.Id) is { } location
                         && _registry.KeyExists(location)
                         && RegistryGuard.ValidateForDeletion(location).Allowed)
                {
                    result.Items.Add(new LeftoverItem
                    {
                        Id = item.Id,
                        Kind = LeftoverKind.RegistryKey,
                        DisplayPath = location.DisplayPath,
                        RegistryLocation = location,
                        Confidence = LeftoverConfidence.Strong,
                        Reason = reason
                    });
                }
            }
        }
    }

    private static RegistryLocation? ParseRegistryId(string id)
    {
        var parts = id.Split('|', 4);
        return parts.Length == 4
               && parts[0] == "registry"
               && Enum.TryParse<RegistryHive>(parts[1], out var hive)
               && Enum.TryParse<RegistryView>(parts[2], out var view)
               && parts[3].Length > 0
            ? new RegistryLocation(hive, view, parts[3])
            : null;
    }

    private void AddOrphanFolderItems(
        IReadOnlyList<InstalledProgram> installed,
        NameClaims claims,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var programRoots = new[]
        {
            _paths.ProgramFiles, _paths.ProgramFilesX86, Path.Combine(_paths.LocalAppData, "Programs"),
            Path.Combine(_paths.ProgramFiles, "Common Files"), Path.Combine(_paths.ProgramFilesX86, "Common Files")
        };
        var roots = programRoots.Select(root => (Path: root, IsProgramRoot: true))
            .Concat(new[] { _paths.ProgramData, _paths.AppData, _paths.LocalAppData, _paths.AppDataLocalLow }
                .Select(root => (Path: root, IsProgramRoot: false)))
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var (root, isProgramRoot) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var folder in OrphanChildFolders(root, result))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = folder.Name;
                if (FindOwningProgram(folder.FullName, OrphanScanTarget, installed) is not null)
                {
                    continue;
                }

                if (claims.IsInstalledVendor(name))
                {
                    // Another product of this vendor is installed. Its siblings may be shared
                    // components, so only an unclaimed product folder is shown.
                    foreach (var product in OrphanChildFolders(folder.FullName, result))
                    {
                        if (!claims.Claims(product.Name) && !claims.IsInstalledVendor(product.Name)
                            && FindOwningProgram(product.FullName, OrphanScanTarget, installed) is null)
                        {
                            AddOrphanFolder(product, isProgramRoot,
                                $"No installed {name} application matches the product folder '{product.Name}'.", result);
                        }
                    }

                    continue;
                }

                if (!claims.Claims(name))
                {
                    AddOrphanFolder(folder, isProgramRoot,
                        "No installed application or Store app matches this folder name or owns this path.", result);
                }
            }
        }
    }

    private static List<DirectoryInfo> OrphanChildFolders(string root, LeftoverScanResult result)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(root)
                .EnumerateDirectories("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System
                })
                .Where(folder => !FileNameExclusions.Contains(folder.Name)
                                 && !OrphanFolderExclusions.Contains(folder.Name)
                                 && !folder.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                                 && !folder.Name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
                                 && !folder.Name.StartsWith('.')
                                 && !IsOpaqueName(folder.Name))
                .Take(4000)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Truncated = true;
            result.Warnings.Add($"Could not inspect '{root}': {ex.Message}");
            return [];
        }
    }

    private void AddOrphanFolder(DirectoryInfo folder, bool isProgramRoot, string reason, LeftoverScanResult result)
    {
        var path = folder.FullName;
        if (!_pathGuard.ValidateForDeletion(path, recursive: true).Allowed)
        {
            return;
        }

        // A name comparison cannot tell a removed application's data from a portable application,
        // a game installed by a launcher or saved games, so only an empty folder rises above Low.
        var confidence = LeftoverConfidence.Low;
        if (IsEmptyFolder(path))
        {
            confidence = LeftoverConfidence.Strong;
            reason += " The folder contains no files.";
        }
        else if (isProgramRoot)
        {
            reason += " It may be a portable application or shared components; review before removal.";
        }
        else if (DateTime.UtcNow - folder.LastWriteTimeUtc > StaleAfter)
        {
            reason += $" Unchanged since {folder.LastWriteTime:yyyy-MM-dd}; it may hold settings or saved games.";
        }
        else
        {
            reason += " Recently changed: an application without an uninstall entry may still use it.";
        }

        result.Items.Add(new LeftoverItem
        {
            Id = $"file|{path.ToLowerInvariant()}",
            Kind = LeftoverKind.Directory,
            DisplayPath = path,
            FilePath = path,
            Confidence = confidence,
            Reason = reason
        });
    }

    private static bool IsEmptyFolder(string path)
    {
        try
        {
            // Inaccessible content is not proof of emptiness, so failures count as "not empty".
            return !Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                MaxRecursionDepth = 16
            }).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void AddOrphanRegistryItems(NameClaims claims, LeftoverScanResult result, CancellationToken cancellationToken)
    {
        var software = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, "Software");
        IReadOnlyList<string> vendors;
        try
        {
            vendors = _registry.GetSubKeyNames(software);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        const string reason = "No installed application or Store app matches this user registry key; review before removal.";
        foreach (var vendor in BoundedEntries(vendors, 2000, software.DisplayPath, result, cancellationToken))
        {
            if (RegistryVendorExclusions.Contains(vendor) || OrphanRegistryExclusions.Contains(vendor)
                || vendor.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only whole product keys are compared. Children of a vendor key are settings of
            // that vendor's products and cannot be attributed reliably.
            if (!claims.Claims(vendor) && !IsOpaqueName(vendor))
            {
                AddOrphanKey(new RegistryLocation(software.Hive, software.View, $"Software\\{vendor}"),
                    productRoot: true, reason, result);
            }
        }
    }

    /// <summary>GUIDs and hashes name no product, so nothing can be compared.</summary>
    private static bool IsOpaqueName(string name)
        => Guid.TryParse(name.Trim('{', '}'), out _)
           || (name.Length >= 16 && name.All(ch => char.IsAsciiHexDigit(ch) || ch == '-'));

    private static void AddOrphanKey(RegistryLocation location, bool productRoot, string reason, LeftoverScanResult result)
    {
        if (!RegistryGuard.ValidateForDeletion(location, allowProductRootEntry: productRoot).Allowed)
        {
            return;
        }

        result.Items.Add(new LeftoverItem
        {
            Id = $"registry|{location.Hive}|{location.View}|{location.Path}",
            Kind = LeftoverKind.RegistryKey,
            DisplayPath = location.DisplayPath,
            RegistryLocation = location,
            IsProductRootEntry = productRoot,
            Confidence = LeftoverConfidence.Low,
            Reason = reason
        });
    }

    private void AddOrphanShortcutItems(
        IReadOnlyList<InstalledProgram> installed,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            _paths.UserStartMenuPrograms, _paths.CommonStartMenuPrograms, _paths.UserDesktop, _paths.PublicDesktop,
            Path.Combine(_paths.AppData, "Microsoft", "Internet Explorer", "Quick Launch")
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IReadOnlyList<string> shortcuts;
            try
            {
                shortcuts = BoundedEntries(Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }), 5000, root, result, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only application shortcuts: a shortcut to a moved document is the user's own.
                var target = ShortcutResolver.TryResolve(shortcut);
                if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !IsOnPresentFixedDrive(target) || File.Exists(target) || Directory.Exists(target)
                    || IsUnderWindowsDirectory(target)
                    || FindOwningProgram(target, OrphanScanTarget, installed) is not null
                    || !_pathGuard.ValidateForDeletion(shortcut, recursive: false).Allowed)
                {
                    continue;
                }

                result.Items.Add(new LeftoverItem
                {
                    Id = $"file|{shortcut.ToLowerInvariant()}",
                    Kind = LeftoverKind.File,
                    DisplayPath = shortcut,
                    FilePath = shortcut,
                    Confidence = LeftoverConfidence.High,
                    Reason = $"Shortcut target is missing ({target}) and no installed application owns it."
                });
            }
        }
    }

    /// <summary>
    /// A missing target only proves a broken shortcut when its drive is present; a shortcut to an
    /// unplugged, removable or network drive is left alone.
    /// </summary>
    private static bool IsOnPresentFixedDrive(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return drive.IsReady && drive.DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Registered Store package families for this user, read from the package repository.</summary>
    private HashSet<string> RegisteredPackageFamilies(out List<string> packageNames)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        packageNames = [];
        try
        {
            foreach (var fullName in _registry.GetSubKeyNames(_paths.MsixPackagesRoot))
            {
                var parts = fullName.Split('_');
                if (parts.Length >= 2)
                {
                    families.Add($"{parts[0]}_{parts[^1]}");
                    packageNames.Add(parts[0]);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            families.Clear();
        }

        return families;
    }

    private void AddOrphanPackageDataItems(
        HashSet<string> registeredFamilies,
        IReadOnlyList<InstalledProgram> installed,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_paths.LocalAppData, "Packages");
        if (!Directory.Exists(root))
        {
            return;
        }

        if (registeredFamilies.Count == 0)
        {
            // Without the package list every folder would look orphaned.
            result.Warnings.Add("The Store package list could not be read; Store app data was not compared.");
            return;
        }

        IReadOnlyList<string> folders;
        try
        {
            folders = BoundedEntries(Directory.EnumerateDirectories(root, "*_*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }), 5000, root, result, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var folder in folders)
        {
            var family = Path.GetFileName(folder);
            if (!PackageFamilyName().IsMatch(family) || registeredFamilies.Contains(family)
                || FindOwningProgram(folder, OrphanScanTarget, installed) is not null
                || !_pathGuard.ValidateForDeletion(folder, recursive: true).Allowed)
            {
                continue;
            }

            result.Items.Add(new LeftoverItem
            {
                Id = $"file|{folder.ToLowerInvariant()}",
                Kind = LeftoverKind.Directory,
                DisplayPath = folder,
                FilePath = folder,
                Confidence = LeftoverConfidence.Strong,
                Reason = $"Store app data for package family {family}, which is no longer registered for this user."
            });
        }
    }

    private void AddOrphanTaskItems(
        IReadOnlyList<InstalledProgram> installed,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.ScheduledTasksDirectory))
        {
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = BoundedEntries(Directory.EnumerateFiles(_paths.ScheduledTasksDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }), 5000, _paths.ScheduledTasksDirectory, result, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskName = Path.GetRelativePath(_paths.ScheduledTasksDirectory, file).Replace(Path.DirectorySeparatorChar, '\\');
            if (taskName.StartsWith("Microsoft\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string xml;
            try
            {
                if (new FileInfo(file).Length > 1_000_000)
                {
                    continue;
                }

                xml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Only a task with a single program action is attributable to that program.
            if (ReadSingleTaskAction(xml) is not { } action || action.Name.LocalName != "Exec")
            {
                continue;
            }

            var command = TaskElement(action, "Command");
            var executable = ExecutablePath(command, _paths.WindowsDirectory);
            if (executable is null || executable.Contains('%')
                || !IsOnPresentFixedDrive(executable) || File.Exists(executable)
                || IsUnderWindowsDirectory(executable)
                || FindOwningProgram(executable, OrphanScanTarget, installed) is not null
                || !ScheduledTaskGuard.ValidateForDeletion(taskName, _paths.ScheduledTasksDirectory, file).Allowed)
            {
                continue;
            }

            result.Items.Add(new LeftoverItem
            {
                Id = $"task|{taskName.ToLowerInvariant()}",
                Kind = LeftoverKind.ScheduledTask,
                DisplayPath = $"Task: {taskName}",
                FilePath = file,
                TaskName = taskName,
                Confidence = LeftoverConfidence.Strong,
                Reason = $"Scheduled task runs a program that no longer exists ({command}) and no installed application owns it."
            });
        }
    }

    /// <summary>
    /// Names claimed by the installed inventory. Matching is deliberately loose: one shared
    /// distinctive token is enough to treat a candidate as claimed, because a missed orphan costs
    /// nothing while a false one could break an installed application.
    /// </summary>
    private sealed class NameClaims
    {
        private readonly HashSet<string> _productNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _productTokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _publisherNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _publisherTokens = new(StringComparer.OrdinalIgnoreCase);

        public NameClaims(IReadOnlyList<InstalledProgram> programs, IEnumerable<string> packageNames)
        {
            foreach (var program in programs)
            {
                AddProduct(program.Name);
                AddProduct(Path.GetFileName(program.InstallLocation?.TrimEnd('\\', '/')));
                AddProduct(Path.GetFileNameWithoutExtension(IconPath(program.DisplayIcon)));
                Add(_publisherNames, _publisherTokens, program.Publisher);
            }

            foreach (var name in packageNames)
            {
                AddProduct(name);
                // Store names are Publisher.Product; the product segment is the useful claim.
                AddProduct(name[(name.LastIndexOf('.') + 1)..]);
            }
        }

        /// <summary>True when an installed application or package could own something by this name.</summary>
        public bool Claims(string? name)
            => Matches(name, _productNames, _productTokens) || Matches(name, _publisherNames, _publisherTokens);

        /// <summary>
        /// True for a folder named after an installed publisher but none of its products, such as a
        /// vendor folder holding several products: its children are compared one by one instead.
        /// </summary>
        public bool IsInstalledVendor(string name)
            => !Matches(name, _productNames, _productTokens) && Matches(name, _publisherNames, _publisherTokens);

        private static bool Matches(string? name, HashSet<string> names, HashSet<string> tokens)
        {
            var compact = NameNormalizer.Compact(name);
            if (compact.Length < 3 || names.Contains(compact))
            {
                return true;
            }

            var candidateTokens = NameNormalizer.Tokens(name);
            // A name made only of generic words or versions cannot be attributed; leave it alone.
            return candidateTokens.Count == 0 || candidateTokens.Any(token => tokens.Contains(token)
                || tokens.Contains(token.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')));
        }

        private void AddProduct(string? name) => Add(_productNames, _productTokens, name);

        private static void Add(HashSet<string> names, HashSet<string> tokens, string? name)
        {
            var compact = NameNormalizer.Compact(name);
            if (compact.Length >= 3)
            {
                names.Add(compact);
            }

            foreach (var token in NameNormalizer.Tokens(name))
            {
                tokens.Add(token);
            }
        }
    }
}
