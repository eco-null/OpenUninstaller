using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class JunkSafetyTests
{
    private static JunkService Service(TestEnvironment env) => new(env.Paths, new PathGuard(env.Paths), FileOperationLog.Null);
    private static DateTime Old => DateTime.UtcNow.AddDays(-3);

    [Fact]
    public void Default_scan_counts_overlapping_categories_once_and_reports_exact_file_metadata()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/old.tmp", "12345", Old);
        var scan = Service(env).Scan();
        var file = Assert.Single(scan.Files.Where(file => file.Path == path));
        Assert.Equal(5, file.Bytes);
        Assert.Equal("user-temp", file.CategoryId);
        Assert.NotEmpty(file.Identity);
        Assert.Equal(5, scan.Categories.Where(category => category.Id != "recycle-bin").Sum(category => category.Bytes));
    }

    [Fact]
    public void Old_files_in_fresh_cache_directories_are_found_without_deleting_directories()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/cache/nested/cache.bin", "old", Old);
        var service = Service(env);
        var file = Assert.Single(service.Scan().Files.Where(file => file.Path == path));
        Assert.Equal(1, service.CleanFiles([file], false).DeletedItems);
        Assert.False(File.Exists(path));
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(Directory.Exists(env.Paths.Temp));
    }

    [Fact]
    public void Reviewed_clean_skips_modified_and_replaced_files_and_does_not_add_new_files()
    {
        using var env = new TestEnvironment();
        var modified = env.CreateFile("Temp/modified.tmp", "old", Old);
        var replaced = env.CreateFile("Temp/replaced.tmp", "old", Old);
        var service = Service(env);
        var files = service.Scan().Files.Where(file => file.CategoryId == "user-temp").ToList();
        File.WriteAllText(modified, "new data");
        File.Delete(replaced);
        File.WriteAllText(replaced, "old");
        File.SetLastWriteTimeUtc(replaced, Old);
        var added = env.CreateFile("Temp/added.tmp", "new", Old);
        var clean = service.CleanFiles(files, false);
        Assert.Equal(0, clean.DeletedItems);
        Assert.Equal(2, clean.SkippedItems);
        Assert.All(new[] { modified, replaced, added }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Clean_skips_files_locked_after_review_and_dry_run_does_not_delete()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/locked.tmp", "data", Old);
        var service = Service(env);
        var file = Assert.Single(service.Scan().Files.Where(file => file.Path == path));
        Assert.Equal(1, service.CleanFiles([file], true).DeletedItems);
        Assert.True(File.Exists(path));
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var clean = service.CleanFiles([file], false);
        Assert.Equal(0, clean.DeletedItems);
        Assert.Equal(1, clean.SkippedItems);
    }

    [Fact]
    public void Pattern_search_reports_personal_files_but_cannot_clean_them_and_deduplicates_roots()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("User/Documents/project/data.old", "keep", Old);
        File.SetLastAccessTimeUtc(path, Old);
        var service = Service(env);
        var scan = service.Scan(new JunkScanOptions
        {
            IncludeKnownLocations = false, SearchRoots = [env.Paths.UserProfile, Path.GetDirectoryName(path)!],
            Patterns = ["*.old", "*.*"]
        });
        var file = Assert.Single(scan.Files);
        Assert.False(file.CanClean);
        Assert.Equal(path, file.Path);
        Assert.Equal(0, service.CleanFiles([file], false).DeletedItems);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Pattern_search_honors_access_age_and_directory_masks()
    {
        using var env = new TestEnvironment();
        var accessed = env.CreateFile("Fixtures/accessed.tmp", "accessed", Old);
        File.SetLastAccessTimeUtc(accessed, DateTime.UtcNow);
        var excluded = env.CreateFile("Fixtures/resources/themes/theme.old", "keep", Old);
        File.SetLastAccessTimeUtc(excluded, Old);
        var included = env.CreateFile("Fixtures/old.tmp", "match", Old);
        File.SetLastAccessTimeUtc(included, Old);
        var service = Service(env);
        var options = new JunkScanOptions { IncludeKnownLocations = false, SearchRoots = [Path.Combine(env.Root, "Fixtures")] };
        Assert.Equal(included, Assert.Single(service.Scan(options).Files).Path);
        var noAccessFilter = service.Scan(options with { UseLastAccessTime = false });
        Assert.Equal(2, noAccessFilter.Files.Count);
        Assert.DoesNotContain(noAccessFilter.Files, file => file.Path == excluded);
        Assert.Equal(3, service.Scan(options with { UseLastAccessTime = false, UseDefaultExclusions = false }).Files.Count);
    }

    [Fact]
    public void Explicit_path_and_wildcard_exclusions_prune_selected_trees()
    {
        using var env = new TestEnvironment();
        env.CreateFile("Fixtures/skip/a.tmp", "a", Old);
        env.CreateFile("Fixtures/cache/b.tmp", "b", Old);
        var included = env.CreateFile("Fixtures/keep/c.tmp", "c", Old);
        var scan = Service(env).Scan(new JunkScanOptions
        {
            IncludeKnownLocations = false, UseLastAccessTime = false, SearchRoots = [Path.Combine(env.Root, "Fixtures")],
            ExcludedPaths = [Path.Combine(env.Root, "Fixtures", "skip"), "*/cache/"]
        });
        Assert.Equal(included, Assert.Single(scan.Files).Path);
    }

    [Theory]
    [InlineData("LocalAppData/Microsoft/Windows/Notifications")]
    [InlineData("Windows/System32/config/systemprofile/AppData/Local/Microsoft/Windows/Notifications")]
    [InlineData("Program Files/WindowsApps")]
    [InlineData("Windows/System32/drivers")]
    public void Pattern_search_honors_built_in_exclusions(string relativeDirectory)
    {
        using var env = new TestEnvironment();
        var protectedFile = env.CreateFile(relativeDirectory + "/state.tmp", "keep", Old);
        var neighbor = env.CreateFile(relativeDirectory + "-neighbor/old.tmp", "candidate", Old);
        var service = Service(env);
        var options = new JunkScanOptions
        {
            IncludeKnownLocations = false, UseLastAccessTime = false,
            SearchRoots = [Path.GetDirectoryName(protectedFile)!, Path.GetDirectoryName(neighbor)!]
        };

        Assert.Equal(neighbor, Assert.Single(service.Scan(options).Files).Path);
        var unfiltered = service.Scan(options with { UseDefaultExclusions = false });
        Assert.Equal(2, unfiltered.Files.Count);
        Assert.All(unfiltered.Files, file => Assert.False(file.CanClean));
        Assert.Equal(0, service.CleanFiles(unfiltered.Files, false).DeletedItems);
        Assert.True(File.Exists(protectedFile));
    }

    [Fact]
    public void Invalid_root_and_locked_files_are_explicit_incomplete_results()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/locked.tmp", "data", Old);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = Service(env);
        var category = Assert.Single(service.Scan().Categories.Where(category => category.Id == "user-temp"));
        Assert.False(category.IsComplete);
        Assert.NotEmpty(category.Warnings);
        var repeatedRoot = service.Scan(new JunkScanOptions
        {
            IncludeKnownLocations = false, UseLastAccessTime = false,
            SearchRoots = [env.Paths.Temp, env.Paths.Temp + Path.DirectorySeparatorChar]
        });
        Assert.Equal(path, Assert.Single(repeatedRoot.Files).Path);
        Assert.False(Assert.Single(repeatedRoot.Files).CanClean);
        Assert.Equal(0, Assert.Single(repeatedRoot.Categories).SkippedItems);
        var invalid = service.Scan(new JunkScanOptions { IncludeKnownLocations = false, SearchRoots = ["relative"] });
        Assert.False(invalid.IsComplete);
        Assert.Contains(Assert.Single(invalid.Categories).Warnings, warning => warning.Contains("absolute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Expanded_known_locations_keep_packaged_and_application_state()
    {
        using var env = new TestEnvironment();
        var paths = new[]
        {
            env.CreateFile("Windows/SystemTemp/temporary.bin", "system", Old),
            env.CreateFile("ProgramData/Temp/temporary.bin", "shared", Old),
            env.CreateFile("LocalAppData/Packages/Test.App/TempState/temporary.bin", "app", Old),
            env.CreateFile("Roaming/discord/GPUCache/gpu.bin", "render", Old),
            env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Cache/cached.bin", "browser", Old)
        };
        var preserved = env.CreateFile("LocalAppData/Packages/Test.App/LocalState/data.bin", "keep", Old);
        var preferences = env.CreateFile("Roaming/discord/Local Storage/preferences.bin", "keep", Old);
        var offline = env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Service Worker/CacheStorage/offline.bin", "keep", Old);
        var service = Service(env);
        var scan = service.Scan();
        Assert.All(paths, path => Assert.Contains(scan.Files, file => file.Path == path));
        var clean = service.CleanFiles(scan.Files.Where(file => file.CanClean), false);
        Assert.Equal(5, clean.DeletedItems);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(preserved));
        Assert.True(File.Exists(preferences));
        Assert.True(File.Exists(offline));
    }

    [Fact]
    public void Pattern_remnants_category_can_still_be_cleaned_independently()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/old.tmp", "data", Old);
        Assert.Equal(1, Service(env).Clean(["pattern-remnants"], false).DeletedItems);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Cancellation_is_observed_before_traversal_and_deletion()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/old.tmp", "data", Old);
        var service = Service(env);
        var files = service.Scan().Files;
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Scan(source.Token));
        Assert.Throws<OperationCanceledException>(() => service.CleanFiles(files, false, source.Token));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("system")]
    [InlineData("local")]
    [InlineData("programs")]
    public void Misconfigured_temp_cannot_turn_profile_or_machine_roots_into_cleanable_data(string kind)
    {
        using var env = new TestEnvironment();
        var unsafeRoot = kind switch
        {
            "profile" => env.Paths.UserProfile, "system" => env.Paths.SystemDrive,
            "local" => env.Paths.LocalAppData, _ => env.Paths.ProgramFiles
        };
        var path = Path.Combine(unsafeRoot, "ImportantProject", "data.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "keep");
        File.SetLastWriteTimeUtc(path, Old);
        // Model a bad TEMP environment variable while retaining every other fixture root.
        typeof(AppPaths).GetProperty(nameof(AppPaths.Temp))!.SetValue(env.Paths, unsafeRoot);
        var service = Service(env);
        var category = Assert.Single(service.Scan().Categories.Where(category => category.Id == "user-temp"));
        Assert.DoesNotContain(category.Files, file => file.Path == path);
        Assert.False(category.IsComplete);
        Assert.Contains(category.Warnings, warning => warning.Contains("protected", StringComparison.OrdinalIgnoreCase));
        var report = service.Scan(new JunkScanOptions
        {
            IncludeKnownLocations = false, SearchRoots = [unsafeRoot], Patterns = ["*.bin"],
            UseLastAccessTime = false, UseDefaultExclusions = false
        });
        var forged = Assert.Single(report.Files.Where(file => file.Path == path)) with
        { CanClean = true, CategoryId = "user-temp" };
        Assert.Equal(0, service.CleanFiles([forged], false).DeletedItems);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "important.old");
        File.WriteAllText(temporary, "keep");
        File.SetLastWriteTimeUtc(temporary, Old);
        Assert.Equal(0, service.Clean(["pattern-remnants"], false).DeletedItems);
        Assert.True(File.Exists(temporary));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Explicit_missing_search_root_is_reported_as_incomplete()
    {
        using var env = new TestEnvironment();
        var scan = Service(env).Scan(new JunkScanOptions
        { IncludeKnownLocations = false, SearchRoots = [Path.Combine(env.Root, "Missing")] });
        Assert.False(scan.IsComplete);
        Assert.Contains(Assert.Single(scan.Categories).Warnings, warning => warning.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Access_filter_does_not_accidentally_apply_a_second_write_time_filter()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Fixtures/recent-write.tmp", "old-access", DateTime.UtcNow);
        File.SetLastAccessTimeUtc(path, Old);
        var service = Service(env);
        var options = new JunkScanOptions { IncludeKnownLocations = false, SearchRoots = [Path.GetDirectoryName(path)!] };
        Assert.Equal(path, Assert.Single(service.Scan(options).Files).Path);
        Assert.Empty(service.Scan(options with { UseLastAccessTime = false }).Files);
    }

    [Fact]
    public void Browser_discovery_does_not_silently_stop_after_one_hundred_profiles()
    {
        using var env = new TestEnvironment();
        for (var index = 0; index < 102; index++)
            env.CreateFile($"LocalAppData/Google/Chrome/User Data/Profile {index}/Cache/data.bin", "cache", Old);
        var browser = Assert.Single(Service(env).Scan().Categories.Where(category => category.Id == "browser-cache"));
        Assert.Equal(102, browser.ItemCount);
        Assert.Equal(510, browser.Bytes);
    }
}
