using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;
using System.Runtime.InteropServices;

namespace OpenUninstaller.Tests;

public sealed class JunkParityTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static JunkService Service(TestEnvironment env) => new(env.Paths,
        new PathGuard(env.Paths), FileOperationLog.Null, new FixedClock(Now));

    [Fact]
    public void Main_pattern_access_cutoff_is_strict_and_age_can_be_disabled()
    {
        using var env = new TestEnvironment();
        var before = env.CreateFile("Fixtures/before.tmp", "before", Now.AddDays(-3));
        var equal = env.CreateFile("Fixtures/equal.tmp", "equal", Now.AddDays(-3));
        var after = env.CreateFile("Fixtures/after.tmp", "after", Now.AddDays(-3));
        File.SetLastAccessTimeUtc(before, Now.AddDays(-1).AddTicks(-1));
        File.SetLastAccessTimeUtc(equal, Now.AddDays(-1));
        File.SetLastAccessTimeUtc(after, Now.AddDays(-1).AddTicks(1));
        var options = new JunkScanOptions { IncludeKnownLocations = false, SearchRoots = [Path.GetDirectoryName(before)!] };
        var service = Service(env);

        Assert.Equal(before, Assert.Single(service.Scan(options).Files).Path);
        Assert.Equal(3, service.Scan(options with { UseAgeFilter = false }).Files.Count);
        Assert.Equal(3, service.Scan(options with { UseLastAccessTime = false }).Files.Count);
    }

    [Fact]
    public void Include_folders_bypass_main_masks_age_and_exclusions_but_never_authorize_cleanup()
    {
        using var env = new TestEnvironment();
        var recent = env.CreateFile("Fixtures/resources/themes/recent.json", "state", Now);
        var nested = env.CreateFile("Fixtures/resources/themes/sub/nested.dat", "nested", Now);
        var service = Service(env);
        var options = new JunkScanOptions
        {
            IncludeKnownLocations = false,
            SearchRoots = [Path.Combine(env.Root, "Fixtures")], Patterns = ["*.tmp"],
            ExcludedPaths = [Path.GetDirectoryName(recent)!],
            IncludeRoots = [Path.GetDirectoryName(recent)!, Path.GetDirectoryName(nested)!]
        };

        var scan = service.Scan(options);
        Assert.Empty(scan.Categories.Single(category => category.Id == "pattern-search").Files);
        var included = scan.Categories.Single(category => category.Id == "included-folders");
        Assert.Equal(2, included.Files.Count);
        Assert.Equal(11, included.Bytes);
        Assert.All(included.Files, file => Assert.False(file.CanClean));
        Assert.False(included.CanClean);
        Assert.Equal(0, service.CleanFiles(included.Files, false).DeletedItems);
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Include_root_validation_missing_paths_locks_and_cancellation_remain_visible()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Fixtures/locked.json", "state", Now);
        var service = Service(env);
        var options = new JunkScanOptions { IncludeKnownLocations = false, IncludeRoots = [Path.GetDirectoryName(path)!] };
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var locked = service.Scan(options);
        Assert.Equal(path, Assert.Single(locked.Files).Path);
        Assert.False(Assert.Single(locked.Files).CanClean);
        Assert.True(locked.IsComplete);
        Assert.False(service.Scan(options with { IncludeRoots = ["relative"] }).IsComplete);
        Assert.False(service.Scan(options with { IncludeRoots = [Path.Combine(env.Root, "missing")] }).IsComplete);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Scan(options, cancellationToken: cts.Token));
    }

    [Fact]
    public void Locked_file_metadata_is_reported_but_known_cache_cleaning_keeps_exclusive_checks()
    {
        using var env = new TestEnvironment();
        var path = env.CreateFile("Temp/locked.tmp", "private data", Now.AddDays(-3));
        var service = Service(env);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = service.Scan(new JunkScanOptions
        {
            IncludeKnownLocations = false, SearchRoots = [env.Paths.Temp], UseAgeFilter = false
        });
        var found = Assert.Single(report.Files);
        Assert.Equal(path, found.Path);
        Assert.False(found.CanClean);
        Assert.Equal(12, found.Bytes);
        Assert.True(report.IsComplete);
        Assert.Equal(0, service.CleanFiles([found], false).DeletedItems);
        var known = service.Scan();
        Assert.DoesNotContain(known.Files, file => file.Path == path);
        Assert.False(known.Categories.Single(category => category.Id == "user-temp").IsComplete);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Hard_link_metadata_can_be_reported_without_granting_deletion_or_following_reparse_points()
    {
        using var env = new TestEnvironment();
        var target = env.CreateFile("Temp/original.tmp", "linked data", Now.AddDays(-3));
        var alias = Path.Combine(env.Paths.Temp, "alias.tmp");
        Assert.True(CreateHardLinkW(alias, target, IntPtr.Zero), $"CreateHardLinkW error {Marshal.GetLastWin32Error()}");
        var service = Service(env);
        var report = service.Scan(new JunkScanOptions
        {
            IncludeKnownLocations = false, SearchRoots = [env.Paths.Temp], UseAgeFilter = false
        });
        Assert.Equal(2, report.Files.Count);
        Assert.Single(report.Files.Select(file => file.Identity).Distinct());
        Assert.All(report.Files, file => Assert.False(file.CanClean));
        Assert.Equal(0, service.CleanFiles(report.Files, false).DeletedItems);
        Assert.DoesNotContain(service.Scan().Files, file => file.Path == target || file.Path == alias);
        using var handle = new JunkFileHandle(alias, env.Paths.Temp, "pattern-search", false, false);
        Assert.Throws<InvalidOperationException>(() => handle.Delete());
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(alias));
    }

    [Theory]
    [InlineData("*", "", true)]
    [InlineData("*.*", "README", false)]
    [InlineData("*.TMP", "file.tmp", true)]
    [InlineData("*.??~", "file.ab~", true)]
    [InlineData("*.??~", "file.a~", false)]
    [InlineData("a*b?c*d", "aonebXcTwod", true)]
    [InlineData("a*b?c*d", "aonebXcTwo", false)]
    [InlineData("??", "😀", true)]
    [InlineData("Ä.tmp", "ä.tmp", false)]
    [InlineData("I.tmp", "ı.tmp", false)]
    [InlineData("a**b", "ab", true)]
    public void Native_wildcard_grammar_and_ascii_fold_are_preserved(string mask, string value, bool matches)
        => Assert.Equal(matches, JunkService.MatchesMask(mask, value));

    [Fact]
    public void Environment_exclusions_expand_injected_paths_and_wildcard_the_drive_only_when_prefixed()
    {
        using var env = new TestEnvironment();
        var service = Service(env);
        var mask = service.ExpandMask("%windir%/System32/drivers/");
        Assert.StartsWith("?:/", mask);
        var otherDrive = 'Z' + env.Paths.WindowsDirectory[1..].Replace('\\', '/') + "/System32/drivers/";
        Assert.True(JunkService.MatchesMask(mask, otherDrive));
        var explicitPath = env.Paths.WindowsDirectory.Replace('\\', '/') + "/System32/drivers/";
        Assert.Equal(explicitPath, service.ExpandMask(explicitPath));
        Assert.EndsWith(env.Paths.LocalAppData[1..].Replace('\\', '/') + "/Microsoft/", service.ExpandMask("%localappdata%/Microsoft/"));
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}
