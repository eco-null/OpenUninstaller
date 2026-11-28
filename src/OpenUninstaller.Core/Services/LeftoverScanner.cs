using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Produces review candidates, never deletions. Traced evidence is exact; heuristic matches are
/// scored and require the user's approval.
///
/// Heuristic signals, strongest first:
/// 1. the recorded install location;
/// 2. a direct folder whose compact name equals the product name;
/// 3. a vendor\product folder pair whose combined name equals the product name;
/// 4. a folder containing the product name plus the publisher;
/// 5. a folder containing one distinctive product token;
/// 6. a publisher-only folder (shown low, only after a product folder was not possible);
/// 7. a Start Menu or desktop shortcut with a matching name.
///
/// Nothing is selected below <see cref="LeftoverConfidence.High"/> by default, and any candidate
/// that belongs to another installed application is skipped or shown as protected.
/// </summary>
public sealed partial class LeftoverScanner
{
    // WOW64 redirects HKLM\SOFTWARE but shares HKCU\Software: its 32-bit view returns the same
    // keys again, so scanning it would list every per-user key twice.
    private static readonly (RegistryHive Hive, RegistryView View)[] SoftwareViews =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64)
    ];

    // Low-integrity processes (sandboxed browsers, games) write settings to AppDataLow instead.
    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] VendorRegistryRoots =
    [
        .. SoftwareViews.Select(view => (view.Hive, view.View, "SOFTWARE")),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\AppDataLow\Software")
    ];

    private static readonly HashSet<string> RegistryVendorExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Clients", "Policies", "RegisteredApplications", "OpenUninstaller",
        "Wow6432Node", "Windows NT", "Windows", "Intel", "WOW6432Node",
        "System", "CurrentControlSet", "Control", "Services", "Hardware", "SAM", "SECURITY",
        "App Paths", "Explorer", "Shell", "Shell Extensions"
    };

    private static readonly HashSet<string> FileNameExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "packages", "temp", "tmp", "installer", "common files", "windowsapps",
        "package cache", "nuget", "connecteddevicesplatform", "microsoft shared", "application data",
        "desktop.ini", "openuninstaller", "startup", "public", "default", "default user",
        "all users", "$recycle.bin", "system volume information", "winsxs", "assembly", "clipsvc",
        "deliveryoptimization", "system32", "syswow64", "program files", "program files (x86)",
        "programdata", "appdata", "local", "locallow", "roaming", "users", "windows nt",
        "driverstore", "servicing", "catroot", "catroot2", "system", "dotnet",
        "reference assemblies", "msbuild", "windows kits", "windows defender", "windows powershell",
        "windowspowershell", "internet explorer", "microsoft.net", "windows security", "windows mail",
        "windows media player", "windows portable devices", "windows photo viewer", "windows collaboration",
        "directx", "steamapps", "common"
    };

    public static readonly HashSet<string> KnownMultiProductVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Adobe", "Google", "Mozilla", "Apple", "Oracle", "JetBrains",
        "Autodesk", "Atlassian", "Valve", "Epic Games", "Ubisoft", "Electronic Arts", "EA", "EA Games",
        "Blizzard", "Blizzard Entertainment", "Riot Games", "Amazon", "Dropbox", "Spotify",
        "Discord", "Zoom", "Slack", "Cisco", "VMware", "NVIDIA", "AMD", "Intel",
        "Sony", "Samsung", "Corsair", "Logitech", "Razer", "SteelSeries", "GOG", "GOG.com", "GOG Galaxy",
        "Origin", "Steam", "Bethesda", "Rockstar Games", "2K Games", "Sega", "Bandai Namco",
        "Capcom", "Square Enix", "Unity", "Embarcadero", "Borland", "Apache", "GitHub",
        "GOM", "GOM & Company", "GOMCompany", "Wargaming", "Wargaming.net", "Battlestate Games",
        "CD Projekt Red", "CD Projekt", "LucasArts", "Activision", "Activision Blizzard",
        "Konami", "Bandai", "Namco", "THQ Nordic", "Paradox Interactive", "Deep Silver",
        "Focus Home Interactive", "Focus Entertainment", "Bungie", "Crytek", "Rebellion",
        "IO Interactive", "Remedy", "Remedy Entertainment", "Techland", "Krafton",
        "Bitdefender", "Kaspersky", "Kaspersky Lab", "ESET", "Malwarebytes", "Avast", "Avast Software",
        "AVG", "McAfee", "Symantec", "Norton", "Trend Micro", "Sophos", "F-Secure", "Webroot", "SentinelOne",
        "Affinity", "Serif", "Serif (Europe) Ltd", "Ableton", "Steinberg", "Native Instruments", "Corel",
        "CyberLink", "MAGIX", "Blackmagic Design", "Blender Foundation", "Audacity", "DaVinci", "Avid",
        "Avid Technology", "Canva", "Figma", "SketchUp", "Citrix", "PostgreSQL", "MySQL", "MongoDB",
        "Docker", "Red Hat", "Canonical", "HashiCorp", "DBeaver Corp", "Insomnia", "Postman", "Git",
        "TortoiseSVN", "TortoiseGit", "Wireshark", "Notepad++", "Sublime Text", "Python Software Foundation",
        "Ruby", "Rust", "Node.js", "Eclipse Foundation", "ASUS", "MSI", "Gigabyte", "ASRock", "Dell",
        "HP", "Lenovo", "Acer", "Realtek", "Qualcomm", "Broadcom", "Synaptics", "Creative", "Creative Labs",
        "Creative Technology", "AOC", "Western Digital", "Seagate", "Kingston", "SanDisk", "Crucial",
        "AIDA64", "CPUID", "Futuremark", "UL", "OneDrive", "Google Drive", "iCloud", "Mega", "Nextcloud",
        "OwnCloud", "Proton", "Proton AG", "NordVPN", "ExpressVPN", "Surfshark", "CyberGhost", "TeamViewer",
        "AnyDesk", "Telegram", "WhatsApp", "Signal", "Viber"
    };

    public static bool IsVendorFolder(
        string folderName,
        IReadOnlyList<string>? publisherTokens,
        IReadOnlyList<string>? productTokens = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        var clean = folderName.Trim();
        if (KnownMultiProductVendors.Contains(clean))
        {
            return true;
        }

        if (publisherTokens != null && publisherTokens.Count > 0)
        {
            var folderTokens = NameNormalizer.Tokens(clean);
            if (folderTokens.Count > 0 && folderTokens.All(ft => publisherTokens.Contains(ft, StringComparer.OrdinalIgnoreCase)))
            {
                if (productTokens != null && productTokens.Count > 0)
                {
                    var distinctiveProductTokens = productTokens
                        .Where(pt => !publisherTokens.Contains(pt, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (distinctiveProductTokens.Count > 0)
                    {
                        var hasProductToken = folderTokens.Any(ft =>
                            distinctiveProductTokens.Contains(ft, StringComparer.OrdinalIgnoreCase));
                        if (!hasProductToken)
                        {
                            return true;
                        }
                    }
                }
                else
                {
                    return true;
                }
            }
        }

        return false;
    }

    private readonly IRegistryStore _registry;
    private readonly TraceStore _traces;
    private readonly AppPaths _paths;
    private readonly PathGuard _pathGuard;
    private readonly IgnoredLeftoverStore? _ignored;
    private readonly IReadOnlyList<RegistryLocation> _installerUserDataRoots;

    public LeftoverScanner(
        IRegistryStore registry,
        TraceStore traces,
        AppPaths paths,
        PathGuard pathGuard,
        IgnoredLeftoverStore? ignored = null,
        IReadOnlyList<RegistryLocation>? installerUserDataRoots = null)
    {
        _registry = registry;
        _traces = traces;
        _paths = paths;
        _pathGuard = pathGuard;
        _ignored = ignored;
        _installerUserDataRoots = installerUserDataRoots ??
        [
            new(RegistryHive.LocalMachine, RegistryView.Registry64,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData"),
            new(RegistryHive.LocalMachine, RegistryView.Registry32,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData")
        ];
    }

    public Task<LeftoverScanResult> ScanAsync(
        InstalledProgram program,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ScanAsync(program, otherPrograms: null, progress, cancellationToken);

    public Task<LeftoverScanResult> ScanAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new LeftoverScanResult
            {
                ProgramId = program.Id,
                ProgramName = program.Name
            };

            if (program.CleanupTargetPath is { } target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = Directory.Exists(target);
                var guard = _pathGuard.ValidateForDeletion(target, recursive: isDirectory);
                if (!guard.Allowed)
                {
                    result.Warnings.Add(guard.Reason ?? L("TheSelectedTargetIsProtected"));
                }
                else if (isDirectory || File.Exists(target))
                {
                    result.Items.Add(new LeftoverItem
                    {
                        Id = $"file|{target.ToLowerInvariant()}",
                        Kind = isDirectory ? LeftoverKind.Directory : LeftoverKind.File,
                        DisplayPath = target,
                        FilePath = target,
                        Confidence = LeftoverConfidence.Exact,
                        Reason = L("ExplicitlySelectedCleanupTarget"),
                        SizeBytes = SafeSize(target, isDirectory)
                    });
                }

                ApplyIgnoredItems(program, result);
                result.ApplyDefaultSelection();
                result.Duration = stopwatch.Elapsed;
                return result;
            }

            var trace = _traces.FindFor(program);
            if (trace is not null)
            {
                result = new LeftoverScanResult
                {
                    ProgramId = program.Id,
                    ProgramName = program.Name,
                    UsedTrace = true,
                    TraceId = trace.Id
                };
                progress?.Report(new OperationProgress(OperationStages.Scanning, F("UsingTrace", trace.Id)));
                AddTraceItems(trace, program, otherPrograms, result, cancellationToken);
            }
            else
            {
                result.Warnings.Add(L("NoInstallTraceWasFoundResultsAreHeuristic"));
            }

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("ScanningRegistryAndCommonFolders2")));
            AddHeuristicItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("InspectingWindowsInstallerReferences")));
            AddInstallerReferenceItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("ScanningApplicationRegistrations")));
            AddAppRegistrationItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("ScanningFileAssociationsAndCOMRegistrations")));
            AddAssociationItems(program, otherPrograms, result, cancellationToken);
            AddComItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("ScanningServicesAndDrivers")));
            AddServiceItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("ScanningPATHEntriesAndFirewallRules")));
            AddPathItems(program, otherPrograms, result, cancellationToken);
            AddFirewallItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, L("ScanningScheduledTasks")));
            AddScheduledTaskItems(program, otherPrograms, result, cancellationToken);

            var consolidated = Consolidate(result.Items, result.Warnings);
            result.Items.Clear();
            result.Items.AddRange(consolidated);
            ApplyIgnoredItems(program, result);
            FillSizes(result.Items, cancellationToken);
            result.Duration = stopwatch.Elapsed;
            result.ApplyDefaultSelection();
            progress?.Report(new OperationProgress(OperationStages.Completed, $"{result.Items.Count} candidate(s) found"));
            return result;
        }, cancellationToken);
    }

    private void AddTraceItems(
        TraceDocument trace,
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken,
        string reasonSuffix = "")
    {
        if (trace.SchemaVersion < 2)
        {
            result.Warnings.Add(
                L("ThisTraceUsesLegacySchema1RegistryKey"));
        }

        foreach (var key in trace.RegistryKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = key.ToLocation();
            if (!_registry.KeyExists(location))
            {
                continue;
            }

            var item = new LeftoverItem
            {
                Id = $"registry|{location.Hive}|{location.View}|{location.Path}",
                Kind = LeftoverKind.RegistryKey,
                DisplayPath = location.DisplayPath,
                Confidence = LeftoverConfidence.Exact,
                IsFromTrace = true,
                RegistryLocation = location,
                Reason = L("CreatedDuringTheTracedInstallAndStillPresent") + reasonSuffix
            };

            var guard = RegistryGuard.ValidateForDeletion(location);
            if (!guard.Allowed)
            {
                item.ProtectedReason = guard.Reason;
            }
            else if (trace.SchemaVersion < 2)
            {
                item.ProtectedReason =
                    L("LegacyTraceSchema1CannotProveThisKey");
            }

            result.Items.Add(item);
        }

        if (trace.SchemaVersion >= 2)
        {
            foreach (var entry in trace.RegistryValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = entry.ToLocation();
                if (!_registry.KeyExists(location))
                {
                    continue;
                }

                foreach (var valueName in entry.ValueNames)
                {
                    if (string.IsNullOrEmpty(valueName))
                    {
                        continue;
                    }

                    if (_registry.GetValue(location, valueName) is null)
                    {
                        continue;
                    }

                    var item = new LeftoverItem
                    {
                        Id = $"registry-value|{location.Hive}|{location.View}|{location.Path}|{valueName.ToLowerInvariant()}",
                        Kind = LeftoverKind.RegistryValue,
                        DisplayPath = $"{location.DisplayPath} :: {valueName}",
                        Confidence = LeftoverConfidence.Exact,
                        IsFromTrace = true,
                        RegistryLocation = location,
                        ValueName = valueName,
                        Reason = L("ThisValueWasAddedDuringTheTracedInstall") + reasonSuffix
                    };

                    var valueGuard = RegistryGuard.ValidateValueForDeletion(location);
                    if (!valueGuard.Allowed)
                    {
                        item.ProtectedReason = valueGuard.Reason;
                    }

                    result.Items.Add(item);
                }
            }
        }

        foreach (var file in trace.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = file.IsDirectory || Directory.Exists(file.Path);
            if (!isDirectory && !File.Exists(file.Path))
            {
                continue;
            }

            var item = new LeftoverItem
            {
                Id = $"file|{file.Path.ToLowerInvariant()}",
                Kind = isDirectory ? LeftoverKind.Directory : LeftoverKind.File,
                DisplayPath = file.Path,
                Confidence = LeftoverConfidence.Exact,
                IsFromTrace = true,
                FilePath = file.Path,
                Reason = L("CreatedDuringTheTracedInstallAndStillPresent") + reasonSuffix
            };

            var guard = _pathGuard.ValidateForDeletion(file.Path, recursive: isDirectory);
            if (!guard.Allowed)
            {
                item.ProtectedReason = guard.Reason;
            }
            else if (FindOwningProgram(file.Path, program, otherPrograms) is { } owner)
            {
                item.ProtectedReason = F("AnotherInstalledApplicationUsesThisPath", owner.Name);
            }

            result.Items.Add(item);
        }

        if (trace.RegistryTruncated || trace.FilesTruncated)
        {
            result.Truncated = true;
            result.Warnings.Add(L("TheTraceWasTruncatedSomeLocationsWereNot"));
        }
    }

    private void AddHeuristicItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0)
        {
            result.Warnings.Add(L("TheApplicationNameHasNoDistinctiveTokensHeuristic"));
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var installFolder = string.IsNullOrWhiteSpace(program.InstallLocation)
            ? null
            : Path.GetFileName(program.InstallLocation.TrimEnd('\\'));
        var folderTokens = NameNormalizer.Tokens(installFolder);
        var combinedTokens = tokens.Concat(folderTokens).Distinct(StringComparer.Ordinal).ToList();
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var knownPaths = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int Score(string candidate, bool allowPublisherOnly = true)
            => ScoreName(candidate, nameCompact, combinedTokens, publisherTokens, allowPublisherOnly);

        void AddRegistry(RegistryLocation location, int score, string reason, bool productRoot = false)
        {
            if (knownPaths.Contains(location.DisplayPath))
            {
                return;
            }

            if (!_registry.KeyExists(location))
            {
                return;
            }

            var guard = RegistryGuard.ValidateForDeletion(location, allowProductRootEntry: productRoot);
            if (!guard.Allowed)
            {
                return;
            }

            knownPaths.Add(location.DisplayPath);
            result.Items.Add(new LeftoverItem
            {
                Id = $"registry|{location.Hive}|{location.View}|{location.Path}",
                Kind = LeftoverKind.RegistryKey,
                DisplayPath = location.DisplayPath,
                Confidence = (LeftoverConfidence)score,
                RegistryLocation = location,
                IsProductRootEntry = productRoot,
                Reason = reason
            });
        }

        void AddFile(string path, int score, string reason)
        {
            if (score <= 0)
            {
                return;
            }

            if (knownPaths.Contains(path))
            {
                return;
            }

            if (FindOwningProgram(path, program, otherPrograms) is not null)
            {
                return;
            }

            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                return;
            }

            if (isDirectory && IsVendorFolder(Path.GetFileName(path.TrimEnd('\\')), publisherTokens, combinedTokens))
            {
                result.Warnings.Add(F("VendorFolderWasPreservedToProtectOtherProducts", path));
                return;
            }

            var guard = _pathGuard.ValidateForDeletion(path, recursive: isDirectory);
            if (!guard.Allowed)
            {
                return;
            }

            knownPaths.Add(path);
            result.Items.Add(new LeftoverItem
            {
                Id = $"file|{path.ToLowerInvariant()}",
                Kind = isDirectory ? LeftoverKind.Directory : LeftoverKind.File,
                DisplayPath = path,
                Confidence = (LeftoverConfidence)score,
                FilePath = path,
                Reason = reason
            });
        }

        // ------------------------------------------------------------------ Install Location
        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            var installLocation = program.InstallLocation.TrimEnd('\\');
            if (Directory.Exists(installLocation))
            {
                if (FindOwningProgram(installLocation, program, otherPrograms) is { } owner)
                {
                    result.Warnings.Add(
                        F("TheRecordedInstallLocationIsUsedByIt", owner.Name));
                }
                else
                {
                    AddFile(installLocation, (int)LeftoverConfidence.High,
                        L("TheRecordedInstallLocationStillExistsAfterUninstall"));
                }
            }
        }

        // ------------------------------------------------------------------ Vendor Registry
        foreach (var (hive, view, softwarePath) in VendorRegistryRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var software = new RegistryLocation(hive, view, softwarePath);
            IReadOnlyList<string> vendors;
            try
            {
                vendors = _registry.GetSubKeyNames(software);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var vendor in vendors)
            {
                if (RegistryVendorExclusions.Contains(vendor))
                {
                    continue;
                }

                var vendorMatchesName = NameNormalizer.CountTokenMatches(vendor, combinedTokens) > 0;
                var vendorMatchesPublisher = NameNormalizer.CountTokenMatches(vendor, publisherTokens) > 0;
                if (!vendorMatchesName && !vendorMatchesPublisher)
                {
                    continue;
                }

                var vendorLocation = new RegistryLocation(hive, view, $"{softwarePath}\\{vendor}");

                // Many applications keep a single SOFTWARE\<Product> key (7-Zip, BleachBit). Offer it
                // with the same rules as a top-level folder: never a vendor root, and never a key
                // whose name also identifies another installed application (SOFTWARE\Python).
                if (vendorMatchesName && !IsVendorFolder(vendor, publisherTokens, combinedTokens)
                    && Score(vendor, allowPublisherOnly: false) is var productKeyScore
                    && productKeyScore >= (int)LeftoverConfidence.Medium
                    && !MatchesOtherProgram(vendor, program, otherPrograms))
                {
                    AddRegistry(vendorLocation, productKeyScore, F("RegistryKeyMatchesTheApplication2", vendor), productRoot: true);
                    continue;
                }

                IReadOnlyList<string> products;
                try
                {
                    products = _registry.GetSubKeyNames(vendorLocation);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var product in BoundedEntries(products, 250, vendorLocation.DisplayPath, result, cancellationToken))
                {
                    var combined = $"{vendor} {product}";
                    var score = Score(combined);
                    if (score == 0)
                    {
                        continue;
                    }

                    var location = new RegistryLocation(hive, view, $"{softwarePath}\\{vendor}\\{product}");
                    AddRegistry(
                        location,
                        score,
                        F("RegistryKeyMatchesTheApplication", vendor, product));
                }
            }
        }

        // ----------------------------------------------------------------- Application Folders
        foreach (var (root, rootKind) in ApplicationFolderRoots().DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            // Documents and saved games hold the user's own files next to application data, so a
            // name match there is never preselected. In the profile only dot folders are app data.
            var personal = rootKind == FolderRootKind.Personal;
            int RootScore(int score) => personal ? Math.Min(score, (int)LeftoverConfidence.Medium) : score;
            string RootReason(string reason) => personal
                ? reason + L("ItIsInAPersonalFolderAndMay")
                : reason;

            IEnumerable<string> children;
            try
            {
                children = BoundedEntries(Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }), 4000, root, result, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(child.TrimEnd('\\'));
                if (FileNameExclusions.Contains(folderName)
                    || (rootKind == FolderRootKind.DotFoldersOnly && !folderName.StartsWith('.')))
                {
                    continue;
                }

                if (IsSameOrUnder(child, program.InstallLocation))
                {
                    continue;
                }

                if (FindOwningProgram(child, program, otherPrograms) is not null)
                {
                    continue;
                }

                var isVendor = IsVendorFolder(folderName, publisherTokens, combinedTokens);
                if (!isVendor)
                {
                    var score = Score(folderName, allowPublisherOnly: false);
                    if (score >= (int)LeftoverConfidence.Medium)
                    {
                        AddFile(child, RootScore(score), RootReason(L("FolderNameMatchesTheApplicationName")));
                        continue;
                    }
                }

                if (!isVendor && Score(folderName, allowPublisherOnly: true) == 0 && !LooksLikeVendor(folderName, combinedTokens, publisherTokens))
                {
                    continue;
                }

                IEnumerable<string> grandchildren;
                try
                {
                    grandchildren = BoundedEntries(Directory.EnumerateDirectories(child, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }), 500, child, result, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var grandchild in grandchildren)
                {
                    var grandName = Path.GetFileName(grandchild.TrimEnd('\\'));
                    if (FileNameExclusions.Contains(grandName))
                    {
                        continue;
                    }

                    if (IsVendorFolder(grandName, publisherTokens, combinedTokens))
                    {
                        continue;
                    }

                    if (FindOwningProgram(grandchild, program, otherPrograms) is not null)
                    {
                        continue;
                    }

                    var grandScore = Math.Max(
                        Score(grandName, allowPublisherOnly: false),
                        Score($"{folderName} {grandName}", allowPublisherOnly: false));
                    if (grandScore >= (int)LeftoverConfidence.Medium)
                    {
                        AddFile(
                            grandchild,
                            RootScore(grandScore),
                            RootReason(F("FolderMatchesTheApplicationName", folderName, grandName)));
                    }
                }
            }
        }

        // The finder can descend beyond the vendor/product pair
        // and compare extension-free filenames. Broader name evidence is discovery only;
        // it does not establish ownership or authorize recursive removal.
        AddDeeperNameSuggestions(program, otherPrograms, result, combinedTokens, publisherTokens,
            nameCompact, cancellationToken);

        // WER local dumps carry the executable name, rather than the application's display
        // name. Only exact, app-specific executable identities are used; no broad *.dmp scan.
        var executableNames = new[] { IconPath(program.DisplayIcon) }
            .Where(path => path is not null && IsSameOrUnder(path, program.InstallLocation))
            .Select(path => Path.GetFileName(path!))
            .Where(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dumpRoot = Path.Combine(_paths.LocalAppData, "CrashDumps");
        if (executableNames.Count > 0 && Directory.Exists(dumpRoot))
        {
            try
            {
                foreach (var dump in BoundedEntries(Directory.EnumerateFiles(dumpRoot, "*.dmp"),
                             5000, dumpRoot, result, cancellationToken))
                {
                    var leaf = Path.GetFileName(dump);
                    if (executableNames.Any(name => leaf.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)))
                    {
                        AddFile(dump, (int)LeftoverConfidence.Strong,
                            L("CrashDumpNameIdentifiesAnExecutableInThe"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Truncated = true;
                result.Warnings.Add(F("CouldNotInspectCrashDumpsAt", dumpRoot, ex.Message));
            }
        }

        // Windows Error Reporting keeps one folder per report, named after the executable
        // (AppCrash_<exe>_<hash>...). Only the same exact executable identities are used.
        if (executableNames.Count > 0)
        {
            AddErrorReportItems(executableNames, AddFile, result, cancellationToken);
        }

        // ------------------------------------------------------------------ Shortcuts
        var shortcutRoots = new[]
        {
            _paths.UserStartMenuPrograms,
            _paths.CommonStartMenuPrograms,
            _paths.UserDesktop,
            _paths.PublicDesktop,
            // Taskbar pins and the legacy Quick Launch bar.
            Path.Combine(_paths.AppData, "Microsoft", "Internet Explorer", "Quick Launch")
        };

        foreach (var root in shortcutRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = BoundedEntries(Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
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

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(entry)
                    || !(entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                         || entry.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
                {
                    // A desktop or Start Menu folder can contain personal files. A matching
                    // label alone never grants ownership of the directory or ordinary documents.
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(entry);
                if (FileNameExclusions.Contains(name))
                {
                    continue;
                }

                var target = ShortcutResolver.TryResolve(entry);
                var underInstall = target is not null && IsSameOrUnder(target, program.InstallLocation);
                var targetMissing = target is not null && !File.Exists(target) && !Directory.Exists(target);

                if (underInstall)
                {
                    if (FindOwningProgram(target!, program, otherPrograms) is null)
                    {
                        AddFile(
                            entry,
                            (int)LeftoverConfidence.High,
                            F("ShortcutTargetIsInsideTheRecordedInstallLocation", target));
                    }

                    continue;
                }

                var shortcutScore = Score(name, allowPublisherOnly: false);

                if (targetMissing && shortcutScore >= (int)LeftoverConfidence.Medium)
                {
                    AddFile(
                        entry,
                        (int)LeftoverConfidence.Strong,
                        F("ShortcutTargetIsMissingAndTheNameMatches", target));
                    continue;
                }

                if (shortcutScore >= (int)LeftoverConfidence.Strong)
                {
                    if (target is not null && File.Exists(target) && FindOwningProgram(target, program, otherPrograms) is not null)
                    {
                        continue;
                    }

                    AddFile(entry, Math.Min(shortcutScore, (int)LeftoverConfidence.Strong),
                        L("ShortcutNameMatchesTheApplicationNameTheTarget"));
                }
            }
        }
    }

    private enum FolderRootKind
    {
        Application,
        Personal,
        DotFoldersOnly
    }

    /// <summary>Folders whose direct children (and vendor\product pairs) are compared by name.</summary>
    private IEnumerable<(string Path, FolderRootKind Kind)> ApplicationFolderRoots()
    {
        foreach (var root in new[]
                 {
                     _paths.ProgramData,
                     _paths.LocalAppData,
                     _paths.AppDataLocalLow,
                     _paths.AppData,
                     _paths.Temp,
                     _paths.Public,
                     Path.Combine(_paths.LocalAppData, "Programs"),
                     _paths.ProgramFiles,
                     _paths.ProgramFilesX86,
                     Path.Combine(_paths.ProgramFiles, "Common Files"),
                     Path.Combine(_paths.ProgramFilesX86, "Common Files"),
                     // UAC file virtualization redirects writes of legacy applications here.
                     Path.Combine(_paths.LocalAppData, "VirtualStore", "Program Files"),
                     Path.Combine(_paths.LocalAppData, "VirtualStore", "Program Files (x86)"),
                     Path.Combine(_paths.LocalAppData, "VirtualStore", "ProgramData")
                 })
        {
            yield return (root, FolderRootKind.Application);
        }

        yield return (_paths.UserProfile, FolderRootKind.DotFoldersOnly);
        yield return (Path.Combine(_paths.UserProfile, "Documents"), FolderRootKind.Personal);
        yield return (Path.Combine(_paths.UserProfile, "Documents", "My Games"), FolderRootKind.Personal);
        yield return (Path.Combine(_paths.UserProfile, "Saved Games"), FolderRootKind.Personal);
    }

    private void AddErrorReportItems(
        IReadOnlySet<string> executableNames,
        Action<string, int, string> addFile,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        foreach (var baseRoot in new[] { _paths.LocalAppData, _paths.ProgramData })
        {
            foreach (var queue in new[] { "ReportArchive", "ReportQueue" })
            {
                var root = Path.Combine(baseRoot, "Microsoft", "Windows", "WER", queue);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var report in BoundedEntries(Directory.EnumerateDirectories(root, "App*_*", new EnumerationOptions
                             {
                                 IgnoreInaccessible = true,
                                 AttributesToSkip = FileAttributes.ReparsePoint
                             }), 5000, root, result, cancellationToken))
                    {
                        var leaf = Path.GetFileName(report);
                        if (executableNames.Any(name =>
                                leaf.StartsWith($"AppCrash_{name}_", StringComparison.OrdinalIgnoreCase)
                                || leaf.StartsWith($"AppHang_{name}_", StringComparison.OrdinalIgnoreCase)))
                        {
                            addFile(report, (int)LeftoverConfidence.Strong,
                                L("WindowsErrorReportNamesAnExecutableInThe"));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Truncated = true;
                    result.Warnings.Add(F("CouldNotInspectErrorReportsAt", root, ex.Message));
                }
            }
        }
    }

    private void AddDeeperNameSuggestions(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        IReadOnlyList<string> productTokens,
        IReadOnlyList<string> publisherTokens,
        string nameCompact,
        CancellationToken cancellationToken)
    {
        const int maxDepth = 4;
        const int maxEntriesPerRoot = 20_000;
        var roots = new[] { _paths.ProgramData, _paths.LocalAppData, _paths.AppDataLocalLow,
            _paths.AppData, _paths.Temp };
        var known = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeName = program.Name.Trim();
        var useNativeName = nativeName.Length >= 4 && !nativeName.Contains('\0')
            && !FileNameExclusions.Contains(nativeName) && !KnownMultiProductVendors.Contains(nativeName)
            && NameNormalizer.Tokens(nativeName).Any(token => token.Length >= 4
                && !NameNormalizer.IsStopWord(token)
                && !publisherTokens.Contains(token, StringComparer.OrdinalIgnoreCase));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<(DirectoryInfo Directory, int Depth)>();
            stack.Push((new DirectoryInfo(root), 0));
            var count = 0;
            var depthLimited = false;
            var limitReached = false;
            while (stack.Count > 0 && !limitReached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = stack.Pop();
                if (!visited.Add(Path.TrimEndingDirectorySeparator(directory.FullName))) continue;
                try
                {
                    if (depth == 0 && (directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                        continue;
                    // Attributes come from the directory listing itself; reparse points and
                    // offline (cloud placeholder) entries are skipped by the enumeration options.
                    foreach (var info in directory.EnumerateFileSystemInfos("*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++count > maxEntriesPerRoot)
                        {
                            limitReached = true;
                            result.Truncated = true;
                            result.Warnings.Add(F("DiscoveryLimitReachedAtDeeperNameSuggestionsAre", maxEntriesPerRoot, root));
                            break;
                        }

                        var entry = info.FullName;
                        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                        var leaf = isDirectory ? info.Name : Path.GetFileNameWithoutExtension(info.Name);
                        if (FileNameExclusions.Contains(leaf) || IsSameOrUnder(entry, program.InstallLocation)
                            || FindOwningProgram(entry, program, otherPrograms) is not null) continue;
                        if (!_pathGuard.ValidateDiscoveryCandidate(entry, recursive: isDirectory).Allowed) continue;

                        if (!known.Contains(entry) && MatchesDiscoveryName(leaf, out var nativeMatch)
                            && _pathGuard.ValidateForDeletion(entry, recursive: isDirectory).Allowed)
                        {
                            known.Add(entry);
                            result.Items.Add(new LeftoverItem
                            {
                                Id = $"file|{entry.ToLowerInvariant()}",
                                Kind = isDirectory ? LeftoverKind.Directory : LeftoverKind.File,
                                DisplayPath = entry, FilePath = entry,
                                Confidence = LeftoverConfidence.Low,
                                Reason = nativeMatch
                                    ? L("RecursiveApplicationDataDiscoveryMatchedTheFullApplication")
                                    : L("RecursiveApplicationDataDiscoveryMatchedTheFilenameOr"),
                                ProtectedReason = L("ReportOnlyNameSuggestionAdditionalApplicationOwnershipEvidence")
                            });
                        }

                        if (!isDirectory || known.Contains(entry)) continue;
                        if (depth + 1 < maxDepth) stack.Push(((DirectoryInfo)info, depth + 1));
                        else if (!depthLimited && Directory.EnumerateFileSystemEntries(entry, "*", options).Any()) depthLimited = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Truncated = true;
                    result.Warnings.Add(F("CouldNotInspectDeeperApplicationDataAt", directory.FullName, ex.Message));
                }
            }

            if (depthLimited)
            {
                result.Truncated = true;
                result.Warnings.Add(F("DiscoveryDepthReachedAtDeeperNameSuggestionsAre", maxDepth, root));
            }
        }

        bool MatchesDiscoveryName(string leaf, out bool nativeMatch)
        {
            nativeMatch = false;
            if (IsVendorFolder(leaf, publisherTokens, productTokens))
            {
                return false;
            }

            var nativeNameMatch = useNativeName ? NativeWholeNameMatch(leaf, nativeName) : null;
            // Existing token splitting can discard short prefixes or split case
            // changes (MyWidget / éWidget). For these new discovery suggestions,
            // an embedded full-name occurrence must also meet native boundaries.
            var ordinaryMatch = ScoreName(leaf, nameCompact, productTokens, publisherTokens, false)
                >= (int)LeftoverConfidence.Medium && nativeNameMatch != false;
            nativeMatch = !ordinaryMatch && nativeNameMatch == true;
            return ordinaryMatch || nativeMatch;
        }
    }

    // StrStrIW plus C1_ALPHA at both sides.
    // Digits are boundaries; Unicode alphabetic characters are not. This is used only
    // for protected discovery suggestions, never to authorize selection or deletion.
    private static bool? NativeWholeNameMatch(string candidate, string name)
    {
        if (candidate.Length == 0 || name.Length == 0 || candidate.Contains('\0')) return null;
        var allocation = Marshal.StringToHGlobalUni(candidate);
        try
        {
            var found = StrStrIW(allocation, name);
            if (found == IntPtr.Zero) return null;
            while (found != IntPtr.Zero)
            {
                var precedingIsAlpha = found != allocation
                    && NativeAlphabetic(unchecked((char)Marshal.ReadInt16(found, -2)));
                var followingIsAlpha = NativeAlphabetic(unchecked((char)Marshal.ReadInt16(found, name.Length * 2)));
                if (!precedingIsAlpha && !followingIsAlpha) return true;
                found = StrStrIW(IntPtr.Add(found, 2), name);
            }

            return false;
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private static bool NativeAlphabetic(char character)
        => GetStringTypeW(1, character.ToString(), 1, out var classification) && (classification & 0x100) != 0;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr StrStrIW(IntPtr text, string search);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStringTypeW(uint informationType, string source, int count, out ushort characterType);

    private void AddInstallerReferenceItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var known = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _installerUserDataRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var sid in BoundedEntries(_registry.GetSubKeyNames(root), 1000,
                             root.DisplayPath, result, cancellationToken))
                {
                    if (!sid.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase)
                        || sid[6..].Any(c => !char.IsAsciiDigit(c) && c != '-')) continue;
                    var products = new RegistryLocation(root.Hive, root.View, $"{root.Path}\\{sid}\\Products");
                    foreach (var product in BoundedEntries(_registry.GetSubKeyNames(products), 20_000,
                                 products.DisplayPath, result, cancellationToken))
                    {
                        if (product.Length != 32 || !product.All(Uri.IsHexDigit)) continue;
                        var properties = new RegistryLocation(root.Hive, root.View,
                            $"{products.Path}\\{product}\\InstallProperties");
                        var name = _registry.GetValue(properties, "DisplayName")?.AsString()?.Trim();
                        var location = _registry.GetValue(properties, "InstallLocation")?.AsString()?.Trim().Trim('"');
                        var sameName = !string.IsNullOrWhiteSpace(name)
                            && string.Equals(name, program.Name.Trim(), StringComparison.OrdinalIgnoreCase);
                        var sameLocation = !string.IsNullOrWhiteSpace(location)
                            && IsSameOrUnder(location, program.InstallLocation)
                            && IsSameOrUnder(program.InstallLocation!, location);
                        if ((!sameName && !sameLocation) || known.Contains(properties.DisplayPath)) continue;
                        if (!string.IsNullOrWhiteSpace(location)
                            && FindOwningProgram(location, program, otherPrograms) is not null) continue;
                        if (!_registry.KeyExists(properties)) continue;
                        known.Add(properties.DisplayPath);
                        result.Items.Add(new LeftoverItem
                        {
                            Id = $"registry|{properties.Hive}|{properties.View}|{properties.Path}",
                            Kind = LeftoverKind.RegistryKey, RegistryLocation = properties,
                            DisplayPath = properties.DisplayPath, Confidence = LeftoverConfidence.Low,
                            Reason = F("WindowsInstallerIdentityReferenceMatchedThisIsAn", (sameLocation ? L("TheRecordedInstallLocation") : L("TheExactDisplayName"))),
                            ProtectedReason = L("WindowsInstallerBookkeepingIsReportOnlyAndRemains")
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Truncated = true;
                result.Warnings.Add(F("CouldNotInspectWindowsInstallerReferencesAt", root.DisplayPath, ex.Message));
            }
        }
    }

    /// <summary>
    /// App Paths and Classes\Applications are per-executable registrations. They are offered only
    /// when the executable is missing or lives inside the recorded install location; a registration
    /// that still points at a real, unrelated executable is left alone.
    /// </summary>
    private void AddAppRegistrationItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0)
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var knownPaths = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (Path: _paths.AppPathsRegistryPath, IsAppPaths: true),
            (Path: _paths.ClassesApplicationsRegistryPath, IsAppPaths: false)
        };

        // App Paths and Classes\Applications are shared by both WOW64 views: one pass per hive.
        const RegistryView view = RegistryView.Registry64;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (rootPath, isAppPaths) in roots)
            {
                var root = new RegistryLocation(hive, view, rootPath);
                IReadOnlyList<string> children;
                try
                {
                    children = _registry.GetSubKeyNames(root);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var child in BoundedEntries(children, 500, root.DisplayPath, result, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stem = Path.GetFileNameWithoutExtension(child);
                    var score = ScoreName(stem, nameCompact, tokens, publisherTokens, allowPublisherOnly: false);
                    if (score == 0)
                    {
                        continue;
                    }

                    var location = new RegistryLocation(hive, view, $"{rootPath}\\{child}");
                    if (!_registry.KeyExists(location))
                    {
                        continue;
                    }

                    string? target;
                    if (isAppPaths)
                    {
                        target = _registry.GetValue(location, string.Empty)?.AsString();
                    }
                    else
                    {
                        var command = new RegistryLocation(
                            hive, view, $"{rootPath}\\{child}\\shell\\open\\command");
                        target = _registry.GetValue(command, string.Empty)?.AsString()
                                 ?? _registry.GetValue(location, string.Empty)?.AsString();
                    }

                    var executable = ExecutablePath(target, _paths.WindowsDirectory);
                    if (executable is null)
                    {
                        continue;
                    }

                    var stillExists = File.Exists(executable);
                    var underInstall = IsSameOrUnder(executable, program.InstallLocation);
                    if (stillExists && !underInstall)
                    {
                        continue;
                    }

                    if (FindOwningProgram(executable, program, otherPrograms) is not null)
                    {
                        continue;
                    }

                    var guard = RegistryGuard.ValidateForDeletion(location, allowAppCompatEntry: true);
                    if (!guard.Allowed)
                    {
                        continue;
                    }

                    if (!knownPaths.Add(location.DisplayPath))
                    {
                        continue;
                    }

                    result.Items.Add(new LeftoverItem
                    {
                        Id = $"registry|{location.Hive}|{location.View}|{location.Path}",
                        Kind = LeftoverKind.RegistryKey,
                        DisplayPath = location.DisplayPath,
                        Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                        RegistryLocation = location,
                        IsAppCompatEntry = true,
                        Reason = stillExists
                            ? F("RegistrationPointsIntoTheRecordedInstallLocation", executable)
                            : F("RegistrationPointsToAMissingExecutable", executable)
                    });
                }
            }
        }
    }

    /// <summary>
    /// A ProgID under <c>Classes</c> is a file-association registration. It is offered when its
    /// <c>shell\open\command</c> points inside the app's install folder, or when the executable is
    /// gone and the ProgID or executable name matches the product. Extension keys and UserChoice
    /// hashes are never touched: only the app's own ProgID registration is removed.
    /// </summary>
    private void AddAssociationItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0)
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var knownPaths = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ProgIDs under SOFTWARE\Classes are shared by both WOW64 views (only CLSID-like subkeys
        // are redirected), so a 32-bit pass would offer every association twice.
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry64)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classes = new RegistryLocation(hive, view, _paths.ClassesRegistryPath);
            foreach (var progId in BoundedEntries(SafeSubKeys(classes), 4000, classes.DisplayPath, result, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progId.StartsWith('.') || AssociationContainers.Contains(progId))
                {
                    continue;
                }

                var location = new RegistryLocation(hive, view, $"{_paths.ClassesRegistryPath}\\{progId}");
                var commandLocation = new RegistryLocation(
                    hive, view, $"{location.Path}\\shell\\open\\command");
                var target = _registry.GetValue(commandLocation, string.Empty)?.AsString();
                var executable = ExecutablePath(target, _paths.WindowsDirectory);
                if (executable is null)
                {
                    continue;
                }

                var exists = File.Exists(executable);
                var underInstall = IsSameOrUnder(executable, program.InstallLocation);
                if (exists && !underInstall)
                {
                    continue;
                }

                var score = Math.Max(
                    ScoreName(progId, nameCompact, tokens, publisherTokens, allowPublisherOnly: false),
                    ScoreName(
                        Path.GetFileNameWithoutExtension(executable),
                        nameCompact,
                        tokens,
                        publisherTokens,
                        allowPublisherOnly: false));
                if (!underInstall && score < (int)LeftoverConfidence.Medium)
                {
                    continue;
                }

                if (FindOwningProgram(executable, program, otherPrograms) is not null)
                {
                    continue;
                }

                var guard = RegistryGuard.ValidateForDeletion(location, allowAssociationEntry: true);
                if (!guard.Allowed || !knownPaths.Add(location.DisplayPath))
                {
                    continue;
                }

                result.Items.Add(new LeftoverItem
                {
                    Id = $"assoc|{location.Hive}|{location.View}|{location.Path}",
                    Kind = LeftoverKind.RegistryKey,
                    DisplayPath = location.DisplayPath,
                    Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                    RegistryLocation = location,
                    IsAssociationEntry = true,
                    Reason = exists
                        ? F("FileAssociationHandlerPointsIntoTheRecordedInstall", executable)
                        : F("FileAssociationHandlerPointsToAMissingExecutable", executable)
                });
            }
        }
    }

    /// <summary>
    /// A COM registration under <c>Classes\CLSID</c> is offered when its server is inside the
    /// install folder, or when the server is missing and its file name matches the product. The
    /// CLSID key itself is a direct child and the guard revalidates the GUID.
    /// </summary>
    private void AddComItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0)
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var knownPaths = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clsidRoot = new RegistryLocation(hive, view, $"{_paths.ClassesRegistryPath}\\CLSID");
            foreach (var clsid in BoundedEntries(SafeSubKeys(clsidRoot), 8000, clsidRoot.DisplayPath, result, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(clsid, out _))
                {
                    continue;
                }

                var location = new RegistryLocation(hive, view, $"{clsidRoot.Path}\\{clsid}");
                var inproc = _registry.GetValue(
                    new RegistryLocation(hive, view, $"{location.Path}\\InprocServer32"),
                    string.Empty)?.AsString();
                var local = _registry.GetValue(
                    new RegistryLocation(hive, view, $"{location.Path}\\LocalServer32"),
                    string.Empty)?.AsString();
                var executable = ExecutablePath(inproc, _paths.WindowsDirectory)
                                 ?? ExecutablePath(local, _paths.WindowsDirectory);
                if (executable is null)
                {
                    continue;
                }

                var exists = File.Exists(executable);
                var underInstall = IsSameOrUnder(executable, program.InstallLocation);
                if (exists && !underInstall)
                {
                    continue;
                }

                var score = ScoreName(
                    Path.GetFileNameWithoutExtension(executable),
                    nameCompact,
                    tokens,
                    publisherTokens,
                    allowPublisherOnly: false);
                if (!underInstall && score < (int)LeftoverConfidence.Medium)
                {
                    continue;
                }

                if (FindOwningProgram(executable, program, otherPrograms) is not null)
                {
                    continue;
                }

                var guard = RegistryGuard.ValidateForDeletion(location, allowComEntry: true);
                if (!guard.Allowed || !knownPaths.Add(location.DisplayPath))
                {
                    continue;
                }

                result.Items.Add(new LeftoverItem
                {
                    Id = $"com|{location.Hive}|{location.View}|{location.Path}",
                    Kind = LeftoverKind.RegistryKey,
                    DisplayPath = location.DisplayPath,
                    Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                    RegistryLocation = location,
                    IsComEntry = true,
                    Reason = exists
                        ? F("COMServerRegistrationPointsIntoTheRecordedInstall", executable)
                        : F("COMServerRegistrationPointsToAMissingFile", executable)
                });
            }
        }
    }

    /// <summary>
    /// A PATH segment is offered only when it points inside the install folder or at a directory
    /// that no longer exists and its name matches the product. System, Windows and Program Files
    /// segments are never offered, and the guard revalidates the exact key, value and segment.
    /// </summary>
    private void AddPathItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0)
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var known = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, machineWide) in new[]
                 {
                     (_paths.MachineEnvironmentRoot, true),
                     (_paths.UserEnvironmentRoot, false)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = _registry.GetValue(root, "Path")?.AsString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var seenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawSegment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = rawSegment.Trim();
                if (segment.Length == 0 || !seenSegments.Add(segment))
                {
                    continue;
                }

                var expanded = Environment.ExpandEnvironmentVariables(segment).Trim();
                // Program Files roots and Common Files are rejected by the PATH guard below.
                if (!Path.IsPathRooted(expanded) || IsUnderWindowsDirectory(expanded))
                {
                    continue;
                }

                var exists = Directory.Exists(expanded);
                if (FindOwningProgram(expanded, program, otherPrograms) is not null)
                {
                    continue;
                }

                var underInstall = IsSameOrUnder(expanded, program.InstallLocation);
                if (!underInstall && exists)
                {
                    continue;
                }

                var score = ScoreName(
                    Path.GetFileName(expanded.TrimEnd('\\')),
                    nameCompact,
                    tokens,
                    publisherTokens,
                    allowPublisherOnly: false);
                if (!underInstall && score < (int)LeftoverConfidence.Strong)
                {
                    continue;
                }

                var guard = RegistryGuard.ValidatePathEntryForDeletion(
                    root, "Path", segment, _paths, machineWide);
                if (!guard.Allowed)
                {
                    continue;
                }

                var display = $"PATH ({root.HiveName}) :: {segment}";
                if (!known.Add(display))
                {
                    continue;
                }

                result.Items.Add(new LeftoverItem
                {
                    Id = $"path|{root.Hive}|{root.View}|{(machineWide ? "machine" : "user")}|{segment.ToLowerInvariant()}",
                    Kind = LeftoverKind.PathEntry,
                    DisplayPath = display,
                    Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                    RegistryLocation = root,
                    ValueName = "Path",
                    PathSegment = segment,
                    IsMachinePath = machineWide,
                    Reason = underInstall
                        ? L("PATHPointsIntoTheRecordedInstallLocation")
                        : L("PATHPointsToAMissingDirectoryWhoseName")
                });
            }
        }
    }

    /// <summary>
    /// One firewall rule is one value in the Defender rule store. Only rules whose program is
    /// inside the install folder or gone with a matching name are offered; the rule store key is
    /// never deleted, only the reviewed value, and the backup exports the store first.
    /// </summary>
    private void AddFirewallItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0 || !_registry.KeyExists(_paths.FirewallRulesRoot))
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var known = result.Items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var valueName in BoundedEntries(SafeValueNames(_paths.FirewallRulesRoot), 5000, _paths.FirewallRulesRoot.DisplayPath, result, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = _registry.GetValue(_paths.FirewallRulesRoot, valueName)?.AsString();
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in data.Split('|'))
            {
                var equals = part.IndexOf('=');
                if (equals > 0)
                {
                    fields[part[..equals].Trim()] = part[(equals + 1)..].Trim();
                }
            }

            if (!fields.TryGetValue("App", out var app) || !fields.TryGetValue("Name", out var ruleName))
            {
                continue;
            }

            var executable = ExecutablePath(app, _paths.WindowsDirectory);
            if (executable is null)
            {
                continue;
            }

            var exists = File.Exists(executable);
            if (FindOwningProgram(executable, program, otherPrograms) is not null)
            {
                continue;
            }

            var underInstall = IsSameOrUnder(executable, program.InstallLocation);
            if (exists && !underInstall)
            {
                continue;
            }

            var score = Math.Max(
                ScoreName(ruleName, nameCompact, tokens, publisherTokens, allowPublisherOnly: false),
                ScoreName(
                    Path.GetFileNameWithoutExtension(executable),
                    nameCompact,
                    tokens,
                    publisherTokens,
                    allowPublisherOnly: false));
            if (!underInstall && score < (int)LeftoverConfidence.Strong)
            {
                continue;
            }

            var guard = RegistryGuard.ValidateFirewallRuleValue(
                _paths.FirewallRulesRoot, valueName, _paths.FirewallRulesRoot);
            if (!guard.Allowed)
            {
                continue;
            }

            var display = $"Firewall: {ruleName}";
            var id = $"firewall|{_paths.FirewallRulesRoot.Hive}|{_paths.FirewallRulesRoot.View}|{valueName.ToLowerInvariant()}";
            if (!known.Add(id))
            {
                continue;
            }

            result.Items.Add(new LeftoverItem
            {
                Id = id,
                Kind = LeftoverKind.FirewallRule,
                DisplayPath = display,
                Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                RegistryLocation = _paths.FirewallRulesRoot,
                ValueName = valueName,
                IsFirewallRule = true,
                Reason = exists
                    ? F("FirewallRuleTargetsAProgramInTheRecorded", executable)
                    : F("FirewallRuleTargetsAMissingProgram", executable)
            });
        }
    }

    private static readonly HashSet<string> AssociationContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "AppID", "Applications", "App Paths", "Directory", "Drive", "Folder", "Interface",
        "TypeLib", "Installer", "SystemFileAssociations", "MIME", "PackagedCom", "WOW6432Node",
        "Microsoft", "ActivatableClasses", "Extensions", "Local Settings", "AppX"
    };

    private IReadOnlyList<string> SafeSubKeys(RegistryLocation location)
    {
        try
        {
            return _registry.KeyExists(location) ? _registry.GetSubKeyNames(location) : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> SafeValueNames(RegistryLocation location)
    {
        try
        {
            return _registry.GetValueNames(location);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private bool IsUnderWindowsDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(_paths.WindowsDirectory))
        {
            return false;
        }

        var full = path.TrimEnd(Path.DirectorySeparatorChar);
        var windows = Path.GetFullPath(_paths.WindowsDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(full, windows, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A service or driver is a leftover when its binary is gone, or when the binary still exists
    /// but lives inside this application's install folder. Boot-start drivers are always protected:
    /// a definition is never removed if it could take part in starting Windows. A live service is
    /// never stopped by the scan.
    /// </summary>
    private void AddServiceItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0)
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        var root = _paths.ServicesRoot;
        if (!_registry.KeyExists(root))
        {
            return;
        }

        IReadOnlyList<string> services;
        try
        {
            services = _registry.GetSubKeyNames(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var service in BoundedEntries(services, 5000, root.DisplayPath, result, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = new RegistryLocation(root.Hive, root.View, $"{root.Path}\\{service}");
            var displayName = _registry.GetValue(location, "DisplayName")?.AsString();
            var nameScore = Math.Max(
                ScoreName(service, nameCompact, tokens, publisherTokens, allowPublisherOnly: false),
                ScoreName(displayName ?? string.Empty, nameCompact, tokens, publisherTokens, allowPublisherOnly: false));
            if (nameScore == 0)
            {
                continue;
            }

            var parameters = new RegistryLocation(root.Hive, root.View, $"{root.Path}\\{service}\\Parameters");
            var imagePath = _registry.GetValue(location, "ImagePath")?.AsString();
            var serviceDll = _registry.GetValue(parameters, "ServiceDll")?.AsString();
            var executable = ExecutablePath(serviceDll ?? imagePath, _paths.WindowsDirectory);
            if (executable is null)
            {
                continue;
            }

            var type = _registry.GetValue(location, "Type")?.AsInt32() ?? 0;
            var start = _registry.GetValue(location, "Start")?.AsInt32() ?? 3;
            var isDriver = type is 1 or 2;
            var isBootDriver = isDriver && start is 0 or 1;
            var exists = File.Exists(executable);
            var underInstall = IsSameOrUnder(executable, program.InstallLocation);
            if (exists && !underInstall)
            {
                continue;
            }

            if (FindOwningProgram(executable, program, otherPrograms) is not null)
            {
                continue;
            }

            var guard = RegistryGuard.ValidateForDeletion(location, allowServiceEntry: true);
            if (!guard.Allowed)
            {
                continue;
            }

            var reason = isDriver
                ? $"Driver '{displayName ?? service}' " +
                  (exists
                      ? L("UsesABinaryInsideTheRecordedInstallLocation")
                      : F("PointsToAMissingBinary", executable))
                : exists
                    ? F("ServiceUsesABinaryInsideTheRecordedInstall", displayName ?? service, executable)
                    : F("ServicePointsToAMissingBinary", displayName ?? service, executable);

            var item = new LeftoverItem
            {
                Id = $"service|{location.Hive}|{location.View}|{location.Path}",
                Kind = LeftoverKind.Service,
                DisplayPath = location.DisplayPath,
                Confidence = exists || nameScore >= (int)LeftoverConfidence.High
                    ? LeftoverConfidence.High
                    : LeftoverConfidence.Strong,
                RegistryLocation = location,
                IsDriverEntry = isDriver,
                IsOwnedServiceEntry = exists,
                Reason = reason
            };

            if (isBootDriver)
            {
                item.ProtectedReason =
                    L("BootStartDriverDefinitionsAreNeverRemovedA");
            }

            result.Items.Add(item);
        }
    }

    /// <summary>
    /// Task Scheduler stores each task as an XML file under System32\Tasks. A task is a candidate
    /// when its command was installed by this application, or when its name matches and the command
    /// no longer exists. Tasks in the Microsoft\Windows namespace are never inferred from a name
    /// match alone.
    /// </summary>
    private void AddScheduledTaskItems(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? otherPrograms,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        if (tokens.Count == 0 || !Directory.Exists(_paths.ScheduledTasksDirectory))
        {
            return;
        }

        var nameCompact = NameNormalizer.Compact(program.Name);
        var publisherTokens = NameNormalizer.Tokens(program.Publisher);
        IEnumerable<string> files;
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
            var taskName = Path.GetRelativePath(_paths.ScheduledTasksDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '\\');
            var isWindowsTask = taskName.StartsWith("Microsoft\\", StringComparison.OrdinalIgnoreCase);

            string xml;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1_000_000)
                {
                    continue;
                }

                xml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var action = ReadSingleTaskAction(xml);
            if (action is null)
            {
                // A task can run several programs. One matching action does not establish
                // ownership of the whole task, so never infer deletion from its first command.
                continue;
            }

            var command = TaskElement(action, "Command");
            var arguments = TaskElement(action, "Arguments");
            var executable = ExecutablePath(command, _paths.WindowsDirectory);
            var classId = TaskElement(action, "ClassId");
            var comServer = executable is null && classId is not null ? ComServerPath(classId) : null;
            if (executable is null && comServer is null)
            {
                continue;
            }

            var underInstall = executable is not null
                ? IsSameOrUnder(executable, program.InstallLocation)
                : IsSameOrUnder(comServer!, program.InstallLocation);
            var stillExists = executable is not null && File.Exists(executable);
            var serverExists = comServer is not null && File.Exists(comServer);
            var leaf = Path.GetFileNameWithoutExtension(file);
            var nameScore = Math.Max(
                ScoreName(taskName, nameCompact, tokens, publisherTokens, allowPublisherOnly: false),
                ScoreName(leaf, nameCompact, tokens, publisherTokens, allowPublisherOnly: false));

            if (isWindowsTask && !underInstall)
            {
                continue;
            }

            if (!underInstall && nameScore < (int)LeftoverConfidence.Medium)
            {
                continue;
            }

            if (executable is not null && stillExists && !underInstall)
            {
                continue;
            }

            if (executable is null && serverExists && !underInstall)
            {
                continue;
            }

            var evidence = executable ?? comServer!;
            if (FindOwningProgram(evidence, program, otherPrograms) is not null)
            {
                continue;
            }

            var guard = ScheduledTaskGuard.ValidateForDeletion(
                taskName, _paths.ScheduledTasksDirectory, file);
            if (!guard.Allowed)
            {
                continue;
            }

            var detail = executable is not null
                ? arguments is null ? $" ({command})" : $" ({command} {arguments})"
                : $" (COM handler {classId} → {comServer})";
            result.Items.Add(new LeftoverItem
            {
                Id = $"task|{taskName.ToLowerInvariant()}",
                Kind = LeftoverKind.ScheduledTask,
                DisplayPath = $"Task: {taskName}",
                Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                FilePath = file,
                TaskName = taskName,
                Reason = executable is null
                    ? F("ScheduledTaskUsesACOMHandlerWhoseServer", (underInstall ? "inside the recorded install location" : "missing"), detail)
                    : stillExists
                        ? F("ScheduledTaskRunsAnExecutableInTheRecorded", detail)
                        : F("ScheduledTaskPointsToAMissingExecutable", detail)
            });
        }
    }

    /// <summary>Resolves a COM handler class to its registered server, if any.</summary>
    private string? ComServerPath(string classId)
    {
        if (!Guid.TryParse(classId.Trim().Trim('{', '}'), out var guid))
        {
            return null;
        }

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64)
                 })
        {
            var root = new RegistryLocation(
                hive, view, $"{_paths.ClassesRegistryPath}\\CLSID\\{{{guid:D}}}");
            var inproc = _registry.GetValue(
                new RegistryLocation(hive, view, $"{root.Path}\\InprocServer32"),
                string.Empty)?.AsString();
            var local = _registry.GetValue(
                new RegistryLocation(hive, view, $"{root.Path}\\LocalServer32"),
                string.Empty)?.AsString();
            var server = ExecutablePath(inproc, _paths.WindowsDirectory)
                         ?? ExecutablePath(local, _paths.WindowsDirectory);
            if (server is not null)
            {
                return server;
            }
        }

        return null;
    }

    private static XElement? ReadSingleTaskAction(string xml)
    {
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000
            });
            var document = XDocument.Load(reader);
            var actions = document.Root?.Elements().Where(e => e.Name.LocalName == "Actions").ToList();
            if (actions is not { Count: 1 })
            {
                return null;
            }

            var entries = actions[0].Elements().ToList();
            return entries.Count == 1
                   && entries[0].Name.LocalName is "Exec" or "ComHandler"
                   && entries[0].Descendants().Count(e => e.Name.LocalName is "Command" or "ClassId") == 1
                ? entries[0]
                : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? TaskElement(XElement action, string name)
        => action.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    /// <summary>
    /// Extracts an executable path from a registry command line or task command. Absolute paths are
    /// required; command-line arguments are removed so a quoted path with spaces works.
    /// </summary>
    private static string? ExecutablePath(string? raw, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1)
            {
                return null;
            }

            value = value[1..end];
        }
        else
        {
            var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0)
            {
                value = value[..(exe + 4)];
            }
            else
            {
                var space = value.IndexOf(' ');
                if (space > 0)
                {
                    value = value[..space];
                }
            }
        }

        value = value.Trim().Trim('"');
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(windowsDirectory, value[12..]);
        }
        else if (!Path.IsPathRooted(value)
                 && value.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(windowsDirectory, value);
        }

        value = Environment.ExpandEnvironmentVariables(value);
        try
        {
            return Path.IsPathRooted(value) ? Path.GetFullPath(value) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scores a folder or key name against the product. Only values that map to a defined
    /// confidence level are returned. Publisher-only matches are deliberately low: they are useful
    /// for descending into a vendor folder, never for selecting the vendor folder itself.
    /// Strictly rejects candidates containing foreign distinctive tokens or missing required product tokens.
    /// </summary>
    internal static int ScoreName(
        string candidate,
        string nameCompact,
        IReadOnlyList<string> productTokens,
        IReadOnlyList<string> publisherTokens,
        bool allowPublisherOnly)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        var candidateCompact = NameNormalizer.Compact(candidate);
        if (candidateCompact.Length == 0)
        {
            return 0;
        }

        if (candidateCompact == nameCompact)
        {
            return (int)LeftoverConfidence.High;
        }

        var candidateTokens = NameNormalizer.Tokens(candidate);
        if (candidateTokens.Count == 0)
        {
            return 0;
        }

        // 1. Foreign distinctive tokens check:
        // Any token in candidate not in productTokens, not in publisherTokens,
        // not a stop word, and not numeric/version indicates a different product.
        foreach (var cToken in candidateTokens)
        {
            if (productTokens.Contains(cToken, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (publisherTokens.Contains(cToken, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (NameNormalizer.IsStopWord(cToken))
            {
                continue;
            }

            if (cToken.All(char.IsDigit) || NameNormalizer.LooksLikeVersion(cToken))
            {
                continue;
            }

            if (productTokens.Any(pt => cToken.StartsWith(pt, StringComparison.OrdinalIgnoreCase) && cToken[pt.Length..].All(char.IsDigit)))
            {
                continue;
            }

            if (publisherTokens.Any(pub => cToken.StartsWith(pub, StringComparison.OrdinalIgnoreCase) && cToken[pub.Length..].All(char.IsDigit)))
            {
                continue;
            }

            return 0;
        }

        var productMatches = NameNormalizer.CountTokenMatches(candidate, productTokens);

        // 2. Missing required tokens check:
        // If the uninstalled product has multiple distinctive tokens, candidate must match all of them.
        if (productTokens.Count >= 2 && productMatches < productTokens.Count)
        {
            return 0;
        }

        if (productMatches >= 2)
        {
            return (int)LeftoverConfidence.High;
        }

        if (productMatches == 1)
        {
            if (publisherTokens.Count > 0 && NameNormalizer.CountTokenMatches(candidate, publisherTokens) > 0)
            {
                return (int)LeftoverConfidence.Strong;
            }

            if (productTokens.Count == 1 && candidateTokens.Count == 1 && string.Equals(candidateTokens[0], productTokens[0], StringComparison.OrdinalIgnoreCase))
            {
                return (int)LeftoverConfidence.Strong;
            }

            return (int)LeftoverConfidence.Medium;
        }

        if (allowPublisherOnly
            && publisherTokens.Count > 0
            && NameNormalizer.CountTokenMatches(candidate, publisherTokens) > 0)
        {
            return (int)LeftoverConfidence.Low;
        }

        return 0;
    }

    private static bool LooksLikeVendor(
        string folderName,
        IReadOnlyList<string> productTokens,
        IReadOnlyList<string> publisherTokens)
        => KnownMultiProductVendors.Contains(folderName)
           || NameNormalizer.CountTokenMatches(folderName, publisherTokens) > 0
           || NameNormalizer.CountTokenMatches(folderName, productTokens) > 0;

    /// <summary>
    /// Finds another installed application that owns the candidate path, either because the path
    /// is inside its install location or because the path contains that location. Deleting such a
    /// path could break another application, so it is never offered (traces mark it protected).
    /// </summary>
    /// <summary>Whether a key name also identifies another installed application.</summary>
    private static bool MatchesOtherProgram(
        string name,
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? others)
        => others?.Any(other => !string.Equals(other.Id, program.Id, StringComparison.Ordinal)
            && ScoreName(
                name,
                NameNormalizer.Compact(other.Name),
                NameNormalizer.MatchTokens(other.Name, other.Publisher),
                NameNormalizer.Tokens(other.Publisher),
                allowPublisherOnly: false) >= (int)LeftoverConfidence.Medium) == true;

    private static InstalledProgram? FindOwningProgram(
        string path,
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? others)
    {
        if (others is null || others.Count == 0 || NormalizedPath(path) is not { } candidate)
        {
            return null;
        }

        return OwnershipIndexes.GetValue(others, list => new OwnershipIndex(list)).Find(candidate, program.Id);
    }

    // Deeper discovery asks for the owner of every entry it visits (tens of thousands per scan).
    // The index answers with dictionary lookups along the candidate's ancestors instead of
    // comparing the candidate with every installed application's paths.
    private static readonly ConditionalWeakTable<IReadOnlyList<InstalledProgram>, OwnershipIndex> OwnershipIndexes = new();

    private sealed record OwnershipEvidence(string? InstallRoot, IReadOnlyList<string> ContainedFiles);

    /// <summary>
    /// Ownership paths of every installed application. A candidate is owned when it is inside an
    /// install root, or when it contains an install root or a recorded executable.
    /// </summary>
    private sealed class OwnershipIndex
    {
        private readonly Dictionary<string, List<InstalledProgram>> _roots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<InstalledProgram>> _containers = new(StringComparer.OrdinalIgnoreCase);

        public OwnershipIndex(IReadOnlyList<InstalledProgram> programs)
        {
            foreach (var program in programs)
            {
                var evidence = BuildOwnershipEvidence(program);
                if (evidence.InstallRoot is { } root)
                {
                    Add(_roots, root, program);
                    AddWithAncestors(root, program);
                }

                foreach (var file in evidence.ContainedFiles)
                {
                    AddWithAncestors(file, program);
                }
            }
        }

        public InstalledProgram? Find(string candidate, string excludedId)
        {
            InstalledProgram? best = null;
            var bestOrder = int.MaxValue;
            Consider(_containers, candidate);
            // A drive root only owns itself (same rule as the per-path comparison).
            for (var current = candidate; current is not null; current = Parent(current))
            {
                if (current.Length > 3 || current.Length == candidate.Length)
                {
                    Consider(_roots, current);
                }
            }

            return best;

            void Consider(Dictionary<string, List<InstalledProgram>> map, string key)
            {
                if (!map.TryGetValue(key, out var owners))
                {
                    return;
                }

                foreach (var owner in owners)
                {
                    if (!string.Equals(owner.Id, excludedId, StringComparison.Ordinal)
                        && _order.TryGetValue(owner, out var order) && order < bestOrder)
                    {
                        best = owner;
                        bestOrder = order;
                        break;
                    }
                }
            }
        }

        private readonly Dictionary<InstalledProgram, int> _order = new(ReferenceEqualityComparer.Instance);

        private void Add(Dictionary<string, List<InstalledProgram>> map, string key, InstalledProgram program)
        {
            _order.TryAdd(program, _order.Count);
            if (!map.TryGetValue(key, out var owners))
            {
                map[key] = owners = [];
            }

            if (owners.Count == 0 || !ReferenceEquals(owners[^1], program))
            {
                owners.Add(program);
            }
        }

        private void AddWithAncestors(string path, InstalledProgram program)
        {
            for (var current = path; current is not null; current = Parent(current))
            {
                if (current.Length > 3 || current.Length == path.Length)
                {
                    Add(_containers, current, program);
                }
            }
        }

        private static string? Parent(string path)
        {
            var index = path.LastIndexOf('\\');
            if (index <= 0)
            {
                return null;
            }

            return index == 2 && path[1] == ':' ? (path.Length > 3 ? path[..3] : null) : path[..index];
        }
    }

    private static OwnershipEvidence BuildOwnershipEvidence(InstalledProgram program)
    {
        var files = new List<string>();
        if (NormalizedPath(IconPath(program.DisplayIcon)) is { } icon)
        {
            files.Add(icon);
        }

        // Some uninstall records omit InstallLocation and DisplayIcon. An explicit uninstaller
        // still establishes that a candidate containing that executable is shared.
        foreach (var command in new[] { program.UninstallCommand, program.QuietUninstallCommand })
        {
            if (NormalizedPath(ExecutablePath(command, string.Empty)) is { } executable)
            {
                files.Add(executable);
            }
        }

        return new OwnershipEvidence(NormalizedPath(program.InstallLocation), files);
    }

    private static string? NormalizedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Same semantics as PathGuard.IsUnder, for paths already passed through NormalizedPath.
    private static bool IsSameOrUnderNormalized(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || (path.Length > root.Length && path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase));

    private static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim().Trim('"');
        var comma = value.LastIndexOf(',');
        if (comma > 2 && int.TryParse(value[(comma + 1)..], out _))
        {
            value = value[..comma];
        }

        try
        {
            return Path.IsPathRooted(value) ? Path.GetFullPath(value.Trim().Trim('"')) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void ApplyIgnoredItems(InstalledProgram program, LeftoverScanResult result)
    {
        if (_ignored is null)
        {
            return;
        }

        var ignored = _ignored.GetIgnoredItemIds(program);
        if (ignored.Count == 0)
        {
            return;
        }

        var hidden = result.Items.Where(i => ignored.Contains(i.Id)).ToList();
        foreach (var item in hidden)
        {
            result.Items.Remove(item);
        }

        result.IgnoredCount = hidden.Count;
        if (hidden.Count > 0)
        {
            result.Warnings.Add(
                F("IgnoredLeftoverSHiddenClearTheIgnoredList", hidden.Count));
        }
    }

    private static List<LeftoverItem> Consolidate(List<LeftoverItem> items, List<string> warnings)
    {
        var byPath = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OrderByDescending(i => i.Confidence).ThenByDescending(i => i.IsFromTrace))
        {
            // Display labels are not identities: several firewall rules can have the same name.
            var key = item.Kind == LeftoverKind.FirewallRule ? item.Id : $"{item.Kind}|{item.DisplayPath}";
            if (byPath.TryGetValue(key, out var existing))
            {
                if (item.Confidence > existing.Confidence)
                {
                    byPath[key] = item;
                }

                continue;
            }

            byPath[key] = item;
        }

        var registry = byPath.Values
            .Where(i => i.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue
                or LeftoverKind.Service or LeftoverKind.PathEntry or LeftoverKind.FirewallRule)
            .OrderByDescending(i => i.Confidence)
            .ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tasks = byPath.Values
            .Where(i => i.Kind == LeftoverKind.ScheduledTask)
            .OrderByDescending(i => i.Confidence)
            .ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = byPath.Values
            .Where(i => i.Kind is LeftoverKind.File or LeftoverKind.Directory)
            .OrderBy(i => (i.FilePath ?? i.DisplayPath).Length)
            .ToList();

        var consolidatedFiles = new List<LeftoverItem>();
        foreach (var item in files)
        {
            var path = item.FilePath ?? item.DisplayPath;
            var covered = consolidatedFiles.Any(existing =>
                existing.Kind == LeftoverKind.Directory
                && PathGuard.IsUnder(path, existing.FilePath ?? existing.DisplayPath));
            if (!covered)
            {
                consolidatedFiles.Add(item);
            }
        }

        consolidatedFiles = consolidatedFiles
            .OrderByDescending(i => i.Confidence)
            .ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = registry.Concat(tasks).Concat(consolidatedFiles).ToList();
        if (result.Count == 0)
        {
            warnings.Add(L("NoLeftoverCandidatesWereFound"));
        }

        return result;
    }

    internal static IReadOnlyList<T> BoundedEntries<T>(
        IEnumerable<T> entries,
        int limit,
        string scope,
        LeftoverScanResult result,
        CancellationToken cancellationToken)
    {
        var bounded = new List<T>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounded.Count == limit)
            {
                result.Truncated = true;
                result.Warnings.Add(F("ScanLimitReachedAtSomeEntriesWereNot", limit, scope));
                break;
            }

            bounded.Add(entry);
        }

        return bounded;
    }

    /// <summary>
    /// Measures the remaining file and folder candidates after consolidation, so a child covered
    /// by its parent folder is never measured twice. Folders are measured in parallel.
    /// </summary>
    private static void FillSizes(IEnumerable<LeftoverItem> items, CancellationToken cancellationToken)
    {
        var pending = items
            .Where(i => i.SizeBytes == 0 && i.Kind is LeftoverKind.File or LeftoverKind.Directory && i.FilePath is not null)
            .ToList();
        Parallel.ForEach(pending, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8)
        }, item => item.SizeBytes = SafeSize(item.FilePath!, item.Kind == LeftoverKind.Directory));
    }

    private static long SafeSize(string path, bool isDirectory)
    {
        try
        {
            if (!isDirectory)
            {
                return new FileInfo(path).Length;
            }

            long total = 0;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
            {
                total += new FileInfo(file).Length;
                if (++count >= 50_000)
                {
                    break;
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsSameOrUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd('\\');
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\');
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                   || PathGuard.IsUnder(fullPath, fullRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
