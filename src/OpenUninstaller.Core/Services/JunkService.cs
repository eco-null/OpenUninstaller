using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Allow-listed junk scanning and cleaning. Only children of known roots are touched; recent files
/// are skipped so an active application's temp data is not pulled out from under it. Every root is
/// either a well-known cache location or a folder that Windows recreates automatically.
/// </summary>
public sealed class JunkService
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);

    private static readonly string[] ChromiumCacheSubdirectories =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "Service Worker\\CacheStorage",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Media Cache",
        "GrShaderCache",
        "ShaderCache"
    ];

    private static readonly string[] BrowserProcessNames =
    [
        "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "chromium"
    ];

    private readonly AppPaths _paths;
    private readonly PathGuard _guard;
    private readonly IOperationLog _log;

    public JunkService(AppPaths paths, PathGuard guard, IOperationLog log)
    {
        _paths = paths;
        _guard = guard;
        _log = log;
    }

    public JunkScanResult Scan(CancellationToken cancellationToken = default)
        => Scan(progress: null, cancellationToken);

    /// <summary>
    /// Scans every category. Each finished category is reported as soon as it is measured so a
    /// caller can render results while the remaining categories are still being inspected.
    /// </summary>
    public JunkScanResult Scan(IProgress<JunkCategory>? progress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new JunkScanResult();
        var runningBrowsers = RunningBrowserNames();
        foreach (var definition in BuildDefinitions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = definition.Category;
            if (definition.IsRecycleBin)
            {
                var (bytes, items) = RecycleBin.Query();
                category.Bytes = bytes;
                category.ItemCount = items;
                result.Categories.Add(category);
                progress?.Report(category);
                continue;
            }

            if (category.Id == "browser-cache" && runningBrowsers.Count > 0)
            {
                category.Warnings.Add(
                    $"{string.Join(", ", runningBrowsers)} is running. Close browsers before cleaning; locked files are skipped.");
            }

            var cutoff = DateTime.UtcNow - RecentWindow;
            foreach (var root in definition.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var (entries, enumerationError) = EnumerateEntries(definition, root);
                if (enumerationError is not null)
                {
                    AddWarning(category, enumerationError);
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var guard = _guard.ValidateJunkItem(entry, root);
                    if (!guard.Allowed)
                    {
                        continue;
                    }

                    if (IsRecent(entry, cutoff))
                    {
                        continue;
                    }

                    category.Bytes += SafeSize(entry);
                    category.ItemCount++;
                }
            }

            result.Categories.Add(category);
            progress?.Report(category);
        }

        return new JunkScanResult
        {
            Categories = result.Categories,
            Duration = stopwatch.Elapsed
        };
    }

    public JunkCleanResult Clean(IEnumerable<string> categoryIds, bool dryRun, CancellationToken cancellationToken = default)
    {
        var requested = categoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        var result = new JunkCleanResult { DryRun = dryRun };
        var definitions = BuildDefinitions()
            .Where(d => requested.Count == 0 || requested.Contains(d.Category.Id))
            .ToList();

        if (definitions.Count == 0)
        {
            result.Messages.Add("No matching junk categories.");
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        var cutoff = DateTime.UtcNow - RecentWindow;
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = definition.Category;
            if (!category.CanClean)
            {
                result.Messages.Add($"{category.Title}: report-only in this version.");
                continue;
            }

            if (definition.IsRecycleBin)
            {
                var (bytes, items) = RecycleBin.Query();
                if (dryRun)
                {
                    result.FreedBytes += bytes;
                    result.DeletedItems += items;
                    result.Messages.Add($"{category.Title}: would empty {items} item(s), {InstalledProgram.FormatBytes(bytes)}.");
                    continue;
                }

                if (RecycleBin.Empty(out var recycleError))
                {
                    result.FreedBytes += bytes;
                    result.DeletedItems += items;
                    result.Messages.Add($"{category.Title}: emptied {items} item(s).");
                    _log.Write("junk", "recycle-bin", "recycle-bin", "emptied", $"{items} items");
                }
                else
                {
                    result.FailedItems++;
                    result.Messages.Add($"{category.Title}: {recycleError}");
                }

                continue;
            }

            foreach (var root in definition.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var (entries, enumerationError) = EnumerateEntries(definition, root);
                if (enumerationError is not null)
                {
                    result.Messages.Add($"{category.Title}: {enumerationError}");
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var guard = _guard.ValidateJunkItem(entry, root);
                    if (!guard.Allowed)
                    {
                        result.SkippedItems++;
                        continue;
                    }

                    if (IsRecent(entry, cutoff))
                    {
                        result.SkippedItems++;
                        continue;
                    }

                    var size = SafeSize(entry);
                    if (dryRun)
                    {
                        result.FreedBytes += size;
                        result.DeletedItems++;
                        continue;
                    }

                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else if (File.Exists(entry))
                        {
                            File.Delete(entry);
                        }

                        result.FreedBytes += size;
                        result.DeletedItems++;
                        _log.Write("junk", "delete", entry, "deleted", $"{size} bytes");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.FailedItems++;
                        result.Messages.Add($"{entry}: {ex.Message}");
                        _log.Write("junk", "delete", entry, "failed", ex.Message);
                    }
                }
            }
        }

        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private static (IReadOnlyList<string> Entries, string? Error) EnumerateEntries(
        Definition definition,
        string root)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> entries = definition.Patterns.Count > 0
                ? definition.Patterns.SelectMany(pattern => Directory.EnumerateFiles(root, pattern, options))
                : Directory.EnumerateFileSystemEntries(root, "*", options);

            return (entries.Take(50_000).ToList(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], ex.Message);
        }
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
                Description = "Contents of the user temp folders older than 10 minutes."
            },
            [_paths.Temp, Local("Temp")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "windows-temp",
                Title = "Windows temporary files",
                Description = "%WINDIR%\\Temp contents older than 10 minutes.",
                RequiresElevation = true
            },
            [Path.Combine(_paths.WindowsDirectory, "Temp")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "browser-cache",
                Title = "Browser caches",
                Description = "Chrome, Edge, Brave, Vivaldi, Opera and Firefox caches for every profile. Close browsers before cleaning.",
            },
            BrowserCacheRoots(),
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "thumbnails",
                Title = "Thumbnail and icon caches",
                Description = "Windows Explorer cache databases; recreated automatically."
            },
            [Local("Microsoft", "Windows", "Explorer")],
            ["thumbcache_*.db", "iconcache_*.db"]);

        yield return new Definition(
            new JunkCategory
            {
                Id = "crash-reports",
                Title = "Crash dumps and error reports",
                Description = "User crash dumps and Windows Error Reporting queues."
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
                Description = "Kernel minidumps and live-kernel reports written by Windows after a crash.",
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
                Description = "Direct3D, NVIDIA, AMD and Intel shader caches; rebuilt on demand."
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
                Description = "WinINet/WinHTTP cache used by Windows components and applications."
            },
            [Local("Microsoft", "Windows", "INetCache")],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "delivery-optimization",
                Title = "Delivery Optimization cache",
                Description = "Windows Update peer-to-peer download cache; Windows recreates it.",
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
                Local("Yarn", "Cache")
            ],
            []);

        yield return new Definition(
            new JunkCategory
            {
                Id = "windows-logs",
                Title = "Windows servicing logs",
                Description = "CBS, DISM and Windows Update logs. Reported only: they are needed for troubleshooting.",
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
                Description = "Boot and application prefetch files. Reported only: removing them temporarily slows startup.",
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
                Description = "Reported only. Removing it can interrupt pending updates.",
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
                Description = "Reported only. Windows.old enables rollback; remove it from Storage settings.",
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
                Description = "Emptied through the Windows Shell; cannot be undone."
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
    private List<string> BrowserCacheRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Local(params string[] parts) => Path.Combine([_paths.LocalAppData, .. parts]);
        string Roaming(params string[] parts) => Path.Combine([_paths.AppData, .. parts]);

        var chromiumBases = new[]
        {
            Local("Google", "Chrome", "User Data"),
            Local("Google", "Chrome Beta", "User Data"),
            Local("Google", "Chrome SxS", "User Data"),
            Local("Microsoft", "Edge", "User Data"),
            Local("Microsoft", "Edge Beta", "User Data"),
            Local("Microsoft", "Edge Dev", "User Data"),
            Local("BraveSoftware", "Brave-Browser", "User Data"),
            Local("Vivaldi", "User Data"),
            Local("Chromium", "User Data")
        };

        foreach (var baseDirectory in chromiumBases)
        {
            foreach (var profile in EnumerateChromiumProfiles(baseDirectory))
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

        // Opera keeps its profile directly in a product folder, not in a "User Data" directory.
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
            foreach (var profile in EnumerateDirectories(firefoxBase, 100))
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

    private static IEnumerable<string> EnumerateChromiumProfiles(string userDataDirectory)
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

        profiles.AddRange(EnumerateDirectories(userDataDirectory, 100)
            .Where(path => Path.GetFileName(path).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)));
        return profiles.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateDirectories(string root, int limit)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(limit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    running.Add(name);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process inspection is informational only.
            }
        }

        return running;
    }

    private static void AddWarning(JunkCategory category, string warning)
    {
        if (!category.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
        {
            category.Warnings.Add(warning);
        }
    }

    private static bool IsRecent(string path, DateTime cutoff)
    {
        try
        {
            var lastWrite = Directory.Exists(path)
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
            return lastWrite > cutoff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long SafeSize(string path)
    {
        try
        {
            if (File.Exists(path))
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
                if (++count >= 100_000)
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

    private sealed class Definition
    {
        public Definition(JunkCategory category, IReadOnlyList<string> roots, IReadOnlyList<string> patterns)
        {
            Category = category;
            Roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Patterns = patterns;
        }

        public JunkCategory Category { get; }
        public IReadOnlyList<string> Roots { get; }
        public IReadOnlyList<string> Patterns { get; }
        public bool IsRecycleBin { get; init; }
    }
}
