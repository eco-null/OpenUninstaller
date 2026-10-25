using System.Diagnostics;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// File-level browser history and cookie cleanup. Only the known profile databases are touched —
/// never passwords, bookmarks, autofill or extensions. A browser that is running is skipped, because
/// deleting its open database corrupts the profile. There is no backup by design: a copy would keep
/// the browsing history the user asked to remove.
/// </summary>
public sealed class BrowserDataService
{
    private static readonly string[] BrowserProcesses = ["chrome", "msedge", "brave", "vivaldi", "firefox"];

    private static readonly HashSet<string> AllowedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "History", "History-journal",
        "Cookies", "Cookies-journal",
        "places.sqlite", "places.sqlite-wal", "places.sqlite-shm",
        "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm"
    };

    private static readonly string[] ChromiumBrowsers =
    [
        Path.Combine("Google", "Chrome"),
        Path.Combine("Microsoft", "Edge"),
        Path.Combine("BraveSoftware", "Brave-Browser"),
        "Vivaldi"
    ];

    private readonly AppPaths _paths;
    private readonly PathGuard _guard;
    private readonly Func<string, bool> _isRunning;

    public BrowserDataService(
        AppPaths paths,
        PathGuard guard,
        Func<string, bool>? isBrowserRunning = null)
    {
        _paths = paths;
        _guard = guard;
        _isRunning = isBrowserRunning ?? IsProcessRunning;
    }

    public BrowserScanResult Scan()
    {
        var result = new BrowserScanResult();

        foreach (var vendor in ChromiumBrowsers)
        {
            var userData = Path.Combine(_paths.LocalAppData, vendor, "User Data");
            AddChromiumProfiles(BrowserNameFor(vendor), ProcessNameFor(vendor), userData, result);
        }

        var firefoxRoot = Path.Combine(_paths.AppData, "Mozilla", "Firefox", "Profiles");
        AddFirefoxProfiles(firefoxRoot, result);
        return result;
    }

    public BrowserCleanResult Clean(IEnumerable<BrowserProfile> profiles, bool dryRun)
    {
        var result = new BrowserCleanResult { DryRun = dryRun };
        foreach (var profile in profiles)
        {
            if (!dryRun && _isRunning(profile.ProcessName))
            {
                result.Skipped += profile.Files.Count;
                result.Messages.Add(
                    $"{profile.Browser} is running. Close it and rescan; its {profile.ProfileName} profile was left untouched.");
                continue;
            }

            foreach (var file in OrderForDeletion(profile.Files))
            {
                var guard = _guard.ValidateForDeletion(file.Path, recursive: false);
                if (!guard.Allowed)
                {
                    result.Messages.Add($"{file.Path}: {guard.Reason}");
                    continue;
                }

                if (!File.Exists(file.Path))
                {
                    continue;
                }

                if (dryRun)
                {
                    result.DeletedFiles++;
                    result.Bytes += file.Bytes;
                    continue;
                }

                try
                {
                    File.Delete(file.Path);
                    if (File.Exists(file.Path))
                    {
                        result.Messages.Add($"{file.Path}: still exists after deletion.");
                        continue;
                    }

                    result.DeletedFiles++;
                    result.Bytes += file.Bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Messages.Add($"{file.Path}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private void AddChromiumProfiles(
        string browser,
        string process,
        string userData,
        BrowserScanResult result)
    {
        if (!Directory.Exists(userData))
        {
            return;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(userData, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(200).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"{browser}: the profile folder could not be read ({ex.Message}).");
            return;
        }

        foreach (var directory in directories)
        {
            var profileName = Path.GetFileName(directory);
            if (profileName.Equals("System Profile", StringComparison.OrdinalIgnoreCase)
                || profileName.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var files = new List<BrowserDataFile>();
            AddIfExists(files, BrowserDataKind.History, Path.Combine(directory, "History"));
            AddIfExists(files, BrowserDataKind.History, Path.Combine(directory, "History-journal"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "Cookies"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "Cookies-journal"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "Network", "Cookies"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "Network", "Cookies-journal"));
            if (files.Count == 0)
            {
                continue;
            }

            var profile = new BrowserProfile
            {
                Browser = browser,
                ProcessName = process,
                ProfileName = profileName,
                ProfilePath = directory
            };
            profile.Files.AddRange(files);
            result.Profiles.Add(profile);
        }
    }

    private static void AddFirefoxProfiles(string root, BrowserScanResult result)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(100).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"Firefox: the profile folder could not be read ({ex.Message}).");
            return;
        }

        foreach (var directory in directories)
        {
            var files = new List<BrowserDataFile>();
            AddIfExists(files, BrowserDataKind.History, Path.Combine(directory, "places.sqlite"));
            AddIfExists(files, BrowserDataKind.History, Path.Combine(directory, "places.sqlite-wal"));
            AddIfExists(files, BrowserDataKind.History, Path.Combine(directory, "places.sqlite-shm"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "cookies.sqlite"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "cookies.sqlite-wal"));
            AddIfExists(files, BrowserDataKind.Cookies, Path.Combine(directory, "cookies.sqlite-shm"));
            if (files.Count == 0)
            {
                continue;
            }

            var profile = new BrowserProfile
            {
                Browser = "Firefox",
                ProcessName = "firefox",
                ProfileName = Path.GetFileName(directory),
                ProfilePath = directory
            };
            profile.Files.AddRange(files);
            result.Profiles.Add(profile);
        }
    }

    private static void AddIfExists(List<BrowserDataFile> files, BrowserDataKind kind, string path)
    {
        try
        {
            if (!File.Exists(path) || !AllowedFileNames.Contains(Path.GetFileName(path)))
            {
                return;
            }

            files.Add(new BrowserDataFile(kind, path, new FileInfo(path).Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<BrowserDataFile> OrderForDeletion(IEnumerable<BrowserDataFile> files)
        => files.OrderBy(f => f.Path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                               || f.Path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                               || f.Path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1);

    private static string BrowserNameFor(string vendor) => vendor switch
    {
        var v when v.Contains("Chrome", StringComparison.OrdinalIgnoreCase) => "Chrome",
        var v when v.Contains("Edge", StringComparison.OrdinalIgnoreCase) => "Edge",
        var v when v.Contains("Brave", StringComparison.OrdinalIgnoreCase) => "Brave",
        var v when v.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) => "Vivaldi",
        _ => "Chromium"
    };

    private static string ProcessNameFor(string vendor) => vendor switch
    {
        var v when v.Contains("Chrome", StringComparison.OrdinalIgnoreCase) => "chrome",
        var v when v.Contains("Edge", StringComparison.OrdinalIgnoreCase) => "msedge",
        var v when v.Contains("Brave", StringComparison.OrdinalIgnoreCase) => "brave",
        var v when v.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase) => "vivaldi",
        _ => BrowserProcesses[0]
    };

    private static bool IsProcessRunning(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }
}
