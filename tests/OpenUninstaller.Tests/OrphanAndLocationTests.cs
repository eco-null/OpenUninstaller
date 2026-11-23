using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

/// <summary>
/// Regression tests for the orphaned-leftover scan, the extra per-application folder and registry
/// roots, the ownership index and the helper APIs added with them. Folders, traces, history and
/// Store packages live in the private temp tree and the private HKCU test subtree. The orphan scan
/// and the AppDataLow root read fixed HKCU\Software paths, so the few tests that need a key there
/// create one uniquely named probe key and remove it again.
/// </summary>
public sealed class OrphanAndLocationTests
{
    private static LeftoverScanner CreateScanner(TestEnvironment env)
        => new(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

    private static InstalledProgram Installed(string name, string? publisher = null, string? installLocation = null,
        string? displayIcon = null)
        => TestPrograms.Create(name, publisher, installLocation, displayIcon: displayIcon);

    /// <summary>Orphan items on disk only; the real HKCU\Software keys of the machine are ignored.</summary>
    private static List<LeftoverItem> FileItems(LeftoverScanResult result)
        => result.Items.Where(i => i.Kind is LeftoverKind.File or LeftoverKind.Directory).ToList();

    private static LeftoverItem? Find(LeftoverScanResult result, string path)
        => result.Items.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private static string Probe(string prefix)
    {
        var letters = Guid.NewGuid().ToByteArray().Take(8).Select(b => (char)('a' + b % 26));
        return prefix + new string(letters.ToArray());
    }

    private static void DeleteHkcu(string path)
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string AddFileTo(string directory, string name = "data.bin")
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, "payload");
        return directory;
    }

    // ------------------------------------------------------------------ Orphan folders

    [Fact]
    public async Task Orphan_scan_reports_unclaimed_folders_low_or_strong_when_empty_and_never_preselects_them()
    {
        using var env = new TestEnvironment();
        var filled = AddFileTo(Path.Combine(env.Paths.ProgramData, "Quuxmoor"));
        var empty = Path.Combine(env.Paths.LocalAppData, "Frobnitzel");
        Directory.CreateDirectory(empty);
        var installed = new[] { Installed("Contoso Widget", "Contoso") };

        var result = await CreateScanner(env).ScanOrphansAsync(installed);

        var low = Find(result, filled);
        Assert.NotNull(low);
        Assert.Equal(LeftoverConfidence.Low, low!.Confidence);
        Assert.False(low.IsSelected);
        Assert.Contains("No installed application", low.Reason);

        var strong = Find(result, empty);
        Assert.NotNull(strong);
        Assert.Equal(LeftoverConfidence.Strong, strong!.Confidence);
        Assert.Contains("contains no files", strong.Reason);
        Assert.False(strong.IsSelected);
        Assert.DoesNotContain(FileItems(result), i => i.IsSelected);
    }

    [Fact]
    public async Task Orphan_scan_skips_folders_claimed_by_name_install_folder_publisher_or_install_location()
    {
        using var env = new TestEnvironment();
        var installRoot = Path.Combine(env.Paths.ProgramData, "Holder", "Inner");
        AddFileTo(installRoot);
        var byName = AddFileTo(Path.Combine(env.Paths.ProgramData, "Contoso Widget"));
        var byInstallFolder = AddFileTo(Path.Combine(env.Paths.LocalAppData, "Gizmoland"));
        var byPublisher = AddFileTo(Path.Combine(env.Paths.AppData, "Fabrikam"));
        var containing = Path.Combine(env.Paths.ProgramData, "Holder");
        var control = AddFileTo(Path.Combine(env.Paths.ProgramData, "Quuxmoor"));
        var installed = new[]
        {
            Installed("Contoso Widget", "Contoso Ltd"),
            Installed("Zeta Tool", "Fabrikam", installLocation: Path.Combine(env.Root, "Apps", "Gizmoland")),
            Installed("Inner Tool", "Someone", installLocation: installRoot + "\\")
        };

        var result = await CreateScanner(env).ScanOrphansAsync(installed);

        Assert.Null(Find(result, byName));
        Assert.Null(Find(result, byInstallFolder));
        Assert.Null(Find(result, byPublisher));
        Assert.Null(Find(result, containing));
        Assert.Null(Find(result, installRoot));
        Assert.NotNull(Find(result, control));
    }

    [Fact]
    public async Task Orphan_scan_descends_a_pure_vendor_folder_and_reports_only_unclaimed_products()
    {
        using var env = new TestEnvironment();
        var vendor = Path.Combine(env.Paths.ProgramData, "Fabrikam Industries");
        var claimed = AddFileTo(Path.Combine(vendor, "Gizmo Pro"));
        var unclaimed = AddFileTo(Path.Combine(vendor, "Quuxmoor"));
        var installed = new[] { Installed("Gizmo Pro", "Fabrikam Industries") };

        var result = await CreateScanner(env).ScanOrphansAsync(installed);

        Assert.Null(Find(result, vendor));
        Assert.Null(Find(result, claimed));
        var item = Find(result, unclaimed);
        Assert.NotNull(item);
        Assert.Equal(LeftoverConfidence.Low, item!.Confidence);
        Assert.Contains("Fabrikam Industries", item.Reason);
    }

    [Fact]
    public async Task Orphan_scan_never_reports_excluded_or_opaque_folder_names()
    {
        using var env = new TestEnvironment();
        var excluded = new[]
        {
            "Packages", "VST3", "MicrosoftThing", "WindowsThing", ".dotfolder", Guid.NewGuid().ToString("D"),
            "{" + Guid.NewGuid().ToString("D") + "}", "0123456789abcdef0123"
        };
        foreach (var name in excluded)
        {
            AddFileTo(Path.Combine(env.Paths.ProgramData, name));
        }

        var control = AddFileTo(Path.Combine(env.Paths.ProgramData, "Quuxmoor"));
        var installed = new[] { Installed("Contoso Widget", "Contoso") };

        var result = await CreateScanner(env).ScanOrphansAsync(installed);

        var reported = FileItems(result).Select(i => Path.GetFileName(i.FilePath!)).ToList();
        Assert.Equal(["Quuxmoor"], reported);
        Assert.NotNull(Find(result, control));
    }

    [Fact]
    public async Task Orphan_scan_result_uses_the_orphan_program_id_and_target_is_recognised()
    {
        using var env = new TestEnvironment();

        var result = await CreateScanner(env).ScanOrphansAsync([]);

        Assert.Equal(LeftoverScanner.OrphanScanId, result.ProgramId);
        Assert.True(LeftoverScanner.IsOrphanScanTarget(LeftoverScanner.OrphanScanTarget));
        Assert.False(LeftoverScanner.IsOrphanScanTarget(Installed("Contoso Widget")));
    }

    // ------------------------------------------------------------------ Orphan traces and history

    [Fact]
    public async Task Orphan_scan_uses_traces_of_removed_apps_and_ignores_traces_of_installed_apps()
    {
        using var env = new TestEnvironment();
        var goneFile = env.CreateFile("Fixtures/gone-data.bin");
        var liveFile = env.CreateFile("Fixtures/live-data.bin");
        var store = new TraceStore(env.Paths);
        store.Save(new TraceDocument
        {
            SchemaVersion = 2, Id = "trace-gone", ProgramName = "Quuxmoor Gone", CreatedAt = DateTimeOffset.UtcNow,
            Files = [new TracedFile { Path = goneFile }]
        });
        store.Save(new TraceDocument
        {
            SchemaVersion = 2, Id = "trace-live", ProgramName = "Contoso Widget", CreatedAt = DateTimeOffset.UtcNow,
            Files = [new TracedFile { Path = liveFile }]
        });
        var installed = new[] { Installed("Contoso Widget", "Contoso") };
        Assert.True(TraceStore.MatchScore(store.Get("trace-live")!, installed[0]) > 0);

        var result = await CreateScanner(env).ScanOrphansAsync(installed);

        var item = Find(result, goneFile);
        Assert.NotNull(item);
        Assert.Equal(LeftoverConfidence.Exact, item!.Confidence);
        Assert.True(item.IsFromTrace);
        Assert.True(item.IsSelected);
        Assert.Contains("install trace of Quuxmoor Gone", item.Reason);
        Assert.Null(Find(result, liveFile));
    }

    [Fact]
    public async Task Orphan_scan_lists_strong_history_items_of_removed_apps_only()
    {
        using var env = new TestEnvironment();
        var strongDir = AddFileTo(env.CreateDirectory("Fixtures", "StrongLeft"));
        var protectedDir = AddFileTo(env.CreateDirectory("Fixtures", "ProtectedLeft"));
        var mediumDir = AddFileTo(env.CreateDirectory("Fixtures", "MediumLeft"));
        var installedDir = AddFileTo(env.CreateDirectory("Fixtures", "InstalledLeft"));
        var ownDir = AddFileTo(env.CreateDirectory("Fixtures", "OrphanOwnLeft"));
        var installedProgram = Installed("Contoso Widget", "Contoso");

        LeftoverSnapshotItem Dir(string path, LeftoverConfidence confidence, bool isProtected = false) => new()
        {
            Id = $"file|{path.ToLowerInvariant()}", Kind = LeftoverKind.Directory, DisplayPath = path,
            Confidence = confidence, IsProtected = isProtected
        };

        var history = new[]
        {
            new LeftoverScanSnapshot
            {
                ProgramId = "gone-app", ProgramName = "Quuxmoor Gone", ScannedAt = DateTimeOffset.UtcNow,
                Items = [Dir(strongDir, LeftoverConfidence.Strong), Dir(protectedDir, LeftoverConfidence.Exact, true),
                    Dir(mediumDir, LeftoverConfidence.Medium)]
            },
            new LeftoverScanSnapshot
            {
                // Same id as an installed program, different name: still ignored.
                ProgramId = installedProgram.Id, ProgramName = "Zork Renamed", ScannedAt = DateTimeOffset.UtcNow,
                Items = [Dir(installedDir, LeftoverConfidence.Exact)]
            },
            new LeftoverScanSnapshot
            {
                ProgramId = LeftoverScanner.OrphanScanId, ProgramName = "Orphaned leftovers",
                ScannedAt = DateTimeOffset.UtcNow, Items = [Dir(ownDir, LeftoverConfidence.Exact)]
            }
        };

        var result = await CreateScanner(env).ScanOrphansAsync([installedProgram], history);

        var item = Find(result, strongDir);
        Assert.NotNull(item);
        Assert.Equal(LeftoverConfidence.Strong, item!.Confidence);
        Assert.Contains("Listed as a leftover of Quuxmoor Gone", item.Reason);
        Assert.Null(Find(result, protectedDir));
        Assert.Null(Find(result, mediumDir));
        Assert.Null(Find(result, installedDir));
        Assert.Null(Find(result, ownDir));
    }

    // ------------------------------------------------------------------ Store packages, registry, tasks, shortcuts

    private static void AddPackage(TestEnvironment env, string fullName)
        => env.Registry.CreateSubKey(new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64,
            $"{env.RegistryBase}\\AppxPackages\\{fullName}"));

    [Fact]
    public async Task Orphan_scan_reports_store_data_of_unregistered_packages_only()
    {
        using var env = new TestEnvironment();
        AddPackage(env, "Contoso.Foo_1.0.0.0_x64__8wekyb3d8bbwe");
        var registered = AddFileTo(Path.Combine(env.Paths.LocalAppData, "Packages", "Contoso.Foo_8wekyb3d8bbwe"));
        var orphaned = AddFileTo(Path.Combine(env.Paths.LocalAppData, "Packages", "Quuxmoor.Gone_abcdefghjkmnp"));
        var malformed = AddFileTo(Path.Combine(env.Paths.LocalAppData, "Packages", "Some_Folder"));

        var result = await CreateScanner(env).ScanOrphansAsync([]);

        var item = Find(result, orphaned);
        Assert.NotNull(item);
        Assert.Equal(LeftoverConfidence.Strong, item!.Confidence);
        Assert.Contains("no longer registered", item.Reason);
        Assert.Null(Find(result, registered));
        Assert.Null(Find(result, malformed));
    }

    [Fact]
    public async Task Orphan_scan_skips_store_data_and_warns_when_the_package_list_is_unreadable()
    {
        using var env = new TestEnvironment();
        var folder = AddFileTo(Path.Combine(env.Paths.LocalAppData, "Packages", "Quuxmoor.Gone_abcdefghjkmnp"));

        var result = await CreateScanner(env).ScanOrphansAsync([]);

        Assert.Null(Find(result, folder));
        Assert.Contains(result.Warnings, w => w.Contains("Store package list could not be read"));
    }

    [Fact]
    public async Task Orphan_scan_reports_unclaimed_hkcu_product_keys_but_not_claimed_or_guid_keys()
    {
        using var env = new TestEnvironment();
        var orphan = Probe("Zqvorphan");
        var claimed = Probe("Zqvclaimed");
        var guidKey = "{" + Guid.NewGuid().ToString("D") + "}";
        var paths = new[] { $"Software\\{orphan}", $"Software\\{claimed}", $"Software\\{guidKey}" };
        try
        {
            foreach (var path in paths)
            {
                env.Registry.CreateSubKey(new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, path));
            }

            var result = await CreateScanner(env).ScanOrphansAsync([Installed(claimed)]);

            var item = Assert.Single(result.Items, i => i.RegistryLocation?.Path == paths[0]);
            Assert.Equal(LeftoverConfidence.Low, item.Confidence);
            Assert.True(item.IsProductRootEntry);
            Assert.False(item.IsSelected);
            Assert.DoesNotContain(result.Items, i => i.RegistryLocation?.Path == paths[1]);
            Assert.DoesNotContain(result.Items, i => i.RegistryLocation?.Path == paths[2]);
        }
        finally
        {
            foreach (var path in paths)
            {
                DeleteHkcu(path);
            }
        }
    }

    [Fact]
    public async Task Orphan_scan_reports_scheduled_tasks_whose_program_is_missing()
    {
        using var env = new TestEnvironment();
        string Task(string exe) => $"<Task><Actions><Exec><Command>{exe}</Command></Exec></Actions></Task>";
        var missing = Path.Combine(env.Root, "Gone", "quuxmoor.exe");
        var present = env.CreateFile("Present/quuxmoor.exe");
        env.CreateFile("Tasks/Quuxmoor/Update", Task(missing));
        env.CreateFile("Tasks/Quuxmoor/Alive", Task(present));
        env.CreateFile("Tasks/Microsoft/Windows/Thing", Task(missing));

        var result = await CreateScanner(env).ScanOrphansAsync([]);

        var item = Assert.Single(result.Items, i => i.Kind == LeftoverKind.ScheduledTask);
        Assert.Equal("Quuxmoor\\Update", item.TaskName);
        Assert.Equal(LeftoverConfidence.Strong, item.Confidence);
    }

    [Fact]
    public async Task Orphan_scan_reports_broken_shortcuts_with_a_missing_application_target()
    {
        using var env = new TestEnvironment();
        var shell = Type.GetTypeFromProgID("WScript.Shell");
        if (shell is null)
        {
            return; // No shell scripting host: a real .lnk cannot be created hermetically.
        }

        var quickLaunch = Path.Combine(env.Paths.AppData, "Microsoft", "Internet Explorer", "Quick Launch");
        Directory.CreateDirectory(quickLaunch);
        var present = env.CreateFile("Present/tool.exe");
        var brokenLink = Path.Combine(quickLaunch, "Broken.lnk");
        var liveLink = Path.Combine(env.Paths.UserDesktop, "Live.lnk");
        dynamic wsh = Activator.CreateInstance(shell)!;
        foreach (var (link, target) in new[] { (brokenLink, Path.Combine(env.Root, "Gone", "gone.exe")), (liveLink, present) })
        {
            dynamic shortcut = wsh.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.Save();
        }

        var result = await CreateScanner(env).ScanOrphansAsync([]);

        var item = Find(result, brokenLink);
        Assert.NotNull(item);
        Assert.Equal(LeftoverConfidence.High, item!.Confidence);
        Assert.True(item.IsSelected);
        Assert.Null(Find(result, liveLink));
    }

    // ------------------------------------------------------------------ New per-application locations

    [Fact]
    public async Task Scan_finds_app_folders_in_common_files_and_virtual_store()
    {
        using var env = new TestEnvironment();
        var common = AddFileTo(Path.Combine(env.Paths.ProgramFiles, "Common Files", "Quuxmoor Widget"));
        var virtualStore = AddFileTo(Path.Combine(env.Paths.LocalAppData, "VirtualStore", "Program Files", "Quuxmoor Widget"));
        var program = Installed("Quuxmoor Widget", "Fabrikam");

        var result = await CreateScanner(env).ScanAsync(program, otherPrograms: []);

        Assert.NotNull(Find(result, common));
        Assert.NotNull(Find(result, virtualStore));
    }

    [Fact]
    public async Task Scan_caps_personal_folder_matches_at_medium_and_only_offers_profile_dot_folders()
    {
        using var env = new TestEnvironment();
        var documents = AddFileTo(Path.Combine(env.Paths.UserProfile, "Documents", "Quuxmoor Widget"));
        var myGames = AddFileTo(Path.Combine(env.Paths.UserProfile, "Documents", "My Games", "Quuxmoor Widget"));
        var savedGames = AddFileTo(Path.Combine(env.Paths.UserProfile, "Saved Games", "Quuxmoor Widget"));
        var plain = AddFileTo(Path.Combine(env.Paths.UserProfile, "Quuxmoor Widget"));
        var dot = AddFileTo(Path.Combine(env.Paths.UserProfile, ".quuxmoorwidget"));
        var program = Installed("Quuxmoor Widget", "Fabrikam");

        var result = await CreateScanner(env).ScanAsync(program, otherPrograms: []);

        foreach (var path in new[] { documents, myGames, savedGames })
        {
            var item = Find(result, path);
            Assert.NotNull(item);
            Assert.True(item!.Confidence <= LeftoverConfidence.Medium, $"{path} was {item.Confidence}");
            Assert.False(item.IsSelected);
            Assert.Contains("personal folder", item.Reason);
        }

        Assert.Null(Find(result, plain));
        Assert.NotNull(Find(result, dot));
    }

    [Fact]
    public async Task Scan_finds_windows_error_report_folders_of_the_installed_executable()
    {
        using var env = new TestEnvironment();
        var installLocation = Path.Combine(env.Paths.ProgramFiles, "Quuxmoor");
        var program = Installed("Quuxmoor Widget", "Fabrikam", installLocation,
            displayIcon: $"\"{installLocation}\\quuxmoor.exe\",0");
        var archive = Path.Combine(env.Paths.LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive");
        var queue = Path.Combine(env.Paths.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue");
        var crash = AddFileTo(Path.Combine(archive, "AppCrash_quuxmoor.exe_abc_def"));
        var hang = AddFileTo(Path.Combine(queue, "AppHang_quuxmoor.exe_abc_def"));
        var other = AddFileTo(Path.Combine(archive, "AppCrash_other.exe_abc_def"));

        var result = await CreateScanner(env).ScanAsync(program, otherPrograms: []);

        Assert.Equal(LeftoverConfidence.Strong, Find(result, crash)?.Confidence);
        Assert.Equal(LeftoverConfidence.Strong, Find(result, hang)?.Confidence);
        Assert.Null(Find(result, other));
    }

    [Fact]
    public async Task Scan_finds_quick_launch_shortcuts_by_name()
    {
        using var env = new TestEnvironment();
        var link = env.CreateFile("Roaming/Microsoft/Internet Explorer/Quick Launch/Quuxmoor Widget.lnk", "x");
        var program = Installed("Quuxmoor Widget", "Fabrikam");

        var result = await CreateScanner(env).ScanAsync(program, otherPrograms: []);

        Assert.NotNull(Find(result, link));
    }

    [Fact]
    public async Task Scan_finds_the_app_key_under_hkcu_appdatalow()
    {
        using var env = new TestEnvironment();
        var name = Probe("Zqvlow");
        var path = $"Software\\AppDataLow\\Software\\{name}";
        try
        {
            env.Registry.CreateSubKey(new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, path));

            var result = await CreateScanner(env).ScanAsync(Installed(name, "Fabrikam"), otherPrograms: []);

            Assert.Contains(result.Items, i => i.RegistryLocation is { Hive: RegistryHive.CurrentUser } l
                                               && string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteHkcu(path);
        }
    }

    // ------------------------------------------------------------------ Ownership index

    [Fact]
    public async Task Scan_does_not_offer_candidates_owned_by_another_installed_program()
    {
        using var env = new TestEnvironment();
        var exact = AddFileTo(Path.Combine(env.Paths.ProgramFiles, "Quuxmoor Widget"));
        var containing = AddFileTo(Path.Combine(env.Paths.LocalAppData, "Programs", "Quuxmoor Widget Suite"));
        var program = Installed("Quuxmoor Widget", "Fabrikam");

        var control = await CreateScanner(env).ScanAsync(program, otherPrograms: []);
        Assert.NotNull(Find(control, exact));
        Assert.NotNull(Find(control, containing));

        var others = new[]
        {
            Installed("Other Suite", "Someone", installLocation: exact + "\\"),
            Installed("Another Suite", "Someone", installLocation: Path.Combine(containing, "bin") + "\\")
        };
        var result = await CreateScanner(env).ScanAsync(program, others);

        Assert.Null(Find(result, exact));
        Assert.Null(Find(result, containing));
    }

    // ------------------------------------------------------------------ Infrastructure

    [Fact]
    public void TraceStore_match_score_ranks_name_install_location_and_unrelated_traces()
    {
        TraceDocument Trace(string name, string? location = null) => new()
        {
            Id = "t", ProgramName = name, InstallLocation = location, CreatedAt = DateTimeOffset.UtcNow
        };

        var program = Installed("Widget Pro", "Contoso", installLocation: @"C:\Apps\Widget");

        Assert.Equal(100, TraceStore.MatchScore(Trace("widget-pro"), program));
        Assert.Equal(95, TraceStore.MatchScore(Trace("Different", @"c:\apps\widget\"), program));
        Assert.Equal(85, TraceStore.MatchScore(Trace("Different", @"C:\Apps\Widget\bin"), program));
        Assert.Equal(0, TraceStore.MatchScore(Trace("Quuxmoor Gone", @"D:\Elsewhere"), program));
    }

    [Fact]
    public void LeftoverHistoryStore_list_returns_snapshots_and_skips_corrupt_files()
    {
        using var env = new TestEnvironment();
        var store = new LeftoverHistoryStore(env.Paths);
        Assert.Empty(store.List());

        foreach (var name in new[] { "Alpha App", "Beta App" })
        {
            store.CompareAndSave(new LeftoverScanResult { ProgramId = name, ProgramName = name }, DateTimeOffset.UtcNow);
        }

        File.WriteAllText(Path.Combine(store.DirectoryPath, "corrupt.json"), "{ not json");
        File.WriteAllText(Path.Combine(store.DirectoryPath, "null.json"), "null");

        var snapshots = store.List();

        Assert.Equal(["Alpha App", "Beta App"], snapshots.Select(s => s.ProgramName).Order().ToArray());
    }

    [Fact]
    public void PathGuard_discovery_validation_allows_children_and_rejects_the_same_roots_as_deletion()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);

        var child = Path.Combine(env.Paths.ProgramData, "SomeApp", "data");
        Assert.True(guard.ValidateDiscoveryCandidate(child).Allowed);
        Assert.True(guard.ValidateDiscoveryCandidate(child, recursive: false).Allowed);

        foreach (var path in new[]
                 {
                     env.Root, env.Paths.WindowsDirectory, env.Paths.ProgramFiles, env.Paths.ProgramData,
                     env.Paths.UserProfile, env.Paths.Temp, env.Paths.AppData, env.Paths.LocalAppData,
                     env.Paths.AppDataLocalLow, Path.GetPathRoot(env.Root)!,
                     Path.Combine(env.Root, "Fixtures", "..", "..", "Windows"), "relative\\path"
                 })
        {
            Assert.False(guard.ValidateDiscoveryCandidate(path).Allowed, $"Expected rejection for {path}");
            Assert.Equal(guard.ValidateForDeletion(path).Allowed, guard.ValidateDiscoveryCandidate(path).Allowed);
        }
    }
}
