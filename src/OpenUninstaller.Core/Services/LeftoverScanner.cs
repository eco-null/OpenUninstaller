using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

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
public sealed class LeftoverScanner
{
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

    public LeftoverScanner(
        IRegistryStore registry,
        TraceStore traces,
        AppPaths paths,
        PathGuard pathGuard,
        IgnoredLeftoverStore? ignored = null)
    {
        _registry = registry;
        _traces = traces;
        _paths = paths;
        _pathGuard = pathGuard;
        _ignored = ignored;
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
                    result.Warnings.Add(guard.Reason ?? "The selected target is protected.");
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
                        Reason = "Explicitly selected cleanup target.",
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
                progress?.Report(new OperationProgress(OperationStages.Scanning, $"Using trace {trace.Id}"));
                AddTraceItems(trace, program, otherPrograms, result, cancellationToken);
            }
            else
            {
                result.Warnings.Add("No install trace was found. Results are heuristic suggestions.");
            }

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Scanning registry and common folders"));
            AddHeuristicItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Scanning application registrations"));
            AddAppRegistrationItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Scanning file associations and COM registrations"));
            AddAssociationItems(program, otherPrograms, result, cancellationToken);
            AddComItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Scanning services and drivers"));
            AddServiceItems(program, otherPrograms, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Scanning PATH entries and firewall rules"));
            AddPathItems(program, result, cancellationToken);
            AddFirewallItems(program, result, cancellationToken);

            progress?.Report(new OperationProgress(OperationStages.Scanning, "Scanning scheduled tasks"));
            AddScheduledTaskItems(program, otherPrograms, result, cancellationToken);

            var consolidated = Consolidate(result.Items, result.Warnings);
            result.Items.Clear();
            result.Items.AddRange(consolidated);
            ApplyIgnoredItems(program, result);
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
        CancellationToken cancellationToken)
    {
        if (trace.SchemaVersion < 2)
        {
            result.Warnings.Add(
                "This trace uses legacy schema 1: registry key candidates are protected because the trace cannot distinguish new keys from values added to existing keys. Re-run trace-install for value-level evidence.");
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
                Reason = "Created during the traced install and still present after uninstall."
            };

            var guard = RegistryGuard.ValidateForDeletion(location);
            if (!guard.Allowed)
            {
                item.ProtectedReason = guard.Reason;
            }
            else if (trace.SchemaVersion < 2)
            {
                item.ProtectedReason =
                    "Legacy trace schema 1 cannot prove this key was created by the installer. Re-run trace-install to use value-level evidence.";
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
                        Reason = "This value was added during the traced install to a key that already existed."
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
                SizeBytes = SafeSize(file.Path, isDirectory),
                Reason = "Created during the traced install and still present after uninstall."
            };

            var guard = _pathGuard.ValidateForDeletion(file.Path, recursive: isDirectory);
            if (!guard.Allowed)
            {
                item.ProtectedReason = guard.Reason;
            }
            else if (FindOwningProgram(file.Path, program, otherPrograms) is { } owner)
            {
                item.ProtectedReason = $"Another installed application ({owner.Name}) uses this path.";
            }

            result.Items.Add(item);
        }

        if (trace.RegistryTruncated || trace.FilesTruncated)
        {
            result.Truncated = true;
            result.Warnings.Add("The trace was truncated; some locations were not indexed.");
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
            result.Warnings.Add("The application name has no distinctive tokens; heuristic matching was skipped.");
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

        void AddRegistry(RegistryLocation location, int score, string reason)
        {
            if (knownPaths.Contains(location.DisplayPath))
            {
                return;
            }

            if (!_registry.KeyExists(location))
            {
                return;
            }

            var guard = RegistryGuard.ValidateForDeletion(location);
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
                result.Warnings.Add($"Vendor folder '{path}' was preserved to protect other products.");
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
                SizeBytes = SafeSize(path, isDirectory),
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
                        $"The recorded install location is used by {owner.Name}; it was not offered for removal.");
                }
                else
                {
                    AddFile(installLocation, (int)LeftoverConfidence.High,
                        "The recorded install location still exists after uninstall.");
                }
            }
        }

        // ------------------------------------------------------------------ Vendor Registry
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var software = new RegistryLocation(hive, view, "SOFTWARE");
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

                    var vendorLocation = new RegistryLocation(hive, view, $"SOFTWARE\\{vendor}");
                    IReadOnlyList<string> products;
                    try
                    {
                        products = _registry.GetSubKeyNames(vendorLocation);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        continue;
                    }

                    foreach (var product in products.Take(250))
                    {
                        var combined = $"{vendor} {product}";
                        var score = Score(combined);
                        if (score == 0)
                        {
                            continue;
                        }

                        var location = new RegistryLocation(hive, view, $"SOFTWARE\\{vendor}\\{product}");
                        AddRegistry(
                            location,
                            score,
                            $"Registry key '{vendor}\\{product}' matches the application.");
                    }
                }
            }
        }

        // ----------------------------------------------------------------- Application Folders
        var fileRoots = new List<string>
        {
            _paths.ProgramData,
            _paths.LocalAppData,
            _paths.AppDataLocalLow,
            _paths.AppData,
            _paths.Public,
            Path.Combine(_paths.LocalAppData, "Programs"),
            _paths.ProgramFiles,
            _paths.ProgramFilesX86
        };

        foreach (var root in fileRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).Take(4000).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(child.TrimEnd('\\'));
                if (FileNameExclusions.Contains(folderName))
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
                        AddFile(child, score, "Folder name matches the application name.");
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
                    grandchildren = Directory.EnumerateDirectories(child, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }).Take(500).ToList();
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
                            grandScore,
                            $"Folder '{folderName}\\{grandName}' matches the application name.");
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Shortcuts
        var shortcutRoots = new[]
        {
            _paths.UserStartMenuPrograms,
            _paths.CommonStartMenuPrograms,
            _paths.UserDesktop,
            _paths.PublicDesktop
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
                entries = Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).Take(5000).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
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
                            $"Shortcut target is inside the recorded install location ({target}).");
                    }

                    continue;
                }

                var shortcutScore = Score(name, allowPublisherOnly: false);

                if (targetMissing && shortcutScore >= (int)LeftoverConfidence.Medium)
                {
                    AddFile(
                        entry,
                        (int)LeftoverConfidence.Strong,
                        $"Shortcut target is missing ({target}) and the name matches the application.");
                    continue;
                }

                if (shortcutScore >= (int)LeftoverConfidence.Strong)
                {
                    if (target is not null && File.Exists(target) && FindOwningProgram(target, program, otherPrograms) is not null)
                    {
                        continue;
                    }

                    AddFile(entry, shortcutScore, "Shortcut name matches the application name.");
                }
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

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
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

                    foreach (var child in children.Take(500))
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
                                ? $"Registration points into the recorded install location ({executable})."
                                : $"Registration points to a missing executable ({executable})."
                        });
                    }
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

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classes = new RegistryLocation(hive, view, _paths.ClassesRegistryPath);
            foreach (var progId in SafeSubKeys(classes).Take(4000))
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
                        ? $"File-association handler points into the recorded install location ({executable})."
                        : $"File-association handler points to a missing executable ({executable})."
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
            foreach (var clsid in SafeSubKeys(clsidRoot).Take(8000))
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
                        ? $"COM server registration points into the recorded install location ({executable})."
                        : $"COM server registration points to a missing file ({executable})."
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
                if (!Path.IsPathRooted(expanded) || IsUnderSystemPath(expanded))
                {
                    continue;
                }

                var exists = Directory.Exists(expanded);
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
                        ? "PATH points into the recorded install location."
                        : "PATH points to a missing directory whose name matches the application."
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
        var known = result.Items.Select(i => i.DisplayPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var valueName in SafeValueNames(_paths.FirewallRulesRoot).Take(5000))
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
            if (!known.Add(display))
            {
                continue;
            }

            result.Items.Add(new LeftoverItem
            {
                Id = $"firewall|{_paths.FirewallRulesRoot.Hive}|{_paths.FirewallRulesRoot.View}|{valueName.ToLowerInvariant()}",
                Kind = LeftoverKind.FirewallRule,
                DisplayPath = display,
                Confidence = underInstall ? LeftoverConfidence.High : LeftoverConfidence.Strong,
                RegistryLocation = _paths.FirewallRulesRoot,
                ValueName = valueName,
                IsFirewallRule = true,
                Reason = exists
                    ? $"Firewall rule targets a program in the recorded install location ({executable})."
                    : $"Firewall rule targets a missing program ({executable})."
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

    private bool IsUnderSystemPath(string path)
    {
        var full = path.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in new[]
                 {
                     _paths.WindowsDirectory,
                     _paths.ProgramFiles,
                     _paths.ProgramFilesX86
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, normalized, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        foreach (var service in services.Take(5000))
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
                      ? "uses a binary inside the recorded install location. The driver is not stopped."
                      : $"points to a missing binary ({executable}).")
                : exists
                    ? $"Service '{displayName ?? service}' uses a binary inside the recorded install location ({executable}). The service is not stopped."
                    : $"Service '{displayName ?? service}' points to a missing binary ({executable}).";

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
                    "Boot-start driver definitions are never removed; a bad change here can stop Windows from starting.";
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
            files = Directory.EnumerateFiles(_paths.ScheduledTasksDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(5000).ToList();
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

            var command = XmlElement(xml, "Command");
            var arguments = XmlElement(xml, "Arguments");
            var executable = ExecutablePath(command, _paths.WindowsDirectory);
            var classId = XmlElement(xml, "ClassId");
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
                    ? $"Scheduled task uses a COM handler whose server is {(underInstall ? "inside the recorded install location" : "missing")}{detail}."
                    : stillExists
                        ? $"Scheduled task runs an executable in the recorded install location{detail}."
                        : $"Scheduled task points to a missing executable{detail}."
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

    private static string? XmlElement(string xml, string name)
    {
        var match = Regex.Match(
            xml,
            $"<{name}>(?<value>.*?)</{name}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
    }

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
    private static InstalledProgram? FindOwningProgram(
        string path,
        InstalledProgram program,
        IReadOnlyList<InstalledProgram>? others)
    {
        if (others is null)
        {
            return null;
        }

        foreach (var other in others)
        {
            if (string.Equals(other.Id, program.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(other.InstallLocation))
            {
                var location = other.InstallLocation.TrimEnd('\\');
                if (IsSameOrUnder(path, location) || IsSameOrUnder(location, path))
                {
                    return other;
                }
            }

            var icon = IconPath(other.DisplayIcon);
            if (icon is not null && IsSameOrUnder(icon, path))
            {
                return other;
            }
        }

        return null;
    }

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
                $"{hidden.Count} ignored leftover(s) hidden. Clear the ignored list from the review window to show them again.");
        }
    }

    private static List<LeftoverItem> Consolidate(List<LeftoverItem> items, List<string> warnings)
    {
        var byPath = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OrderByDescending(i => i.Confidence).ThenByDescending(i => i.IsFromTrace))
        {
            var key = $"{item.Kind}|{item.DisplayPath}";
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
            warnings.Add("No leftover candidates were found.");
        }

        return result;
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
