using OpenUninstaller.Cli;

namespace OpenUninstaller.Tests;

public sealed class JunkCliOptionsTests
{
    [Fact]
    public void Explicit_scope_preserves_paths_patterns_and_access_age_filter()
    {
        var parsed = ArgumentSet.Parse(["scan", "--files", "--no-known", "--roots", @"C:\scan; E:\cache",
            "--patterns", "*.tmp;*.??~", "--exclude", @"C:\scan\keep;*.~enc", "--minimum-age-hours", "48"]);
        var options = Program.ParseJunkScanOptions(parsed);
        Assert.Equal("scan", parsed.FirstPositional);
        Assert.False(options.IncludeKnownLocations);
        Assert.Equal([@"C:\scan", @"E:\cache"], options.SearchRoots);
        Assert.Equal(["*.tmp", "*.??~"], options.Patterns);
        Assert.Equal([@"C:\scan\keep", "*.~enc"], options.ExcludedPaths);
        Assert.Equal(TimeSpan.FromHours(48), options.MinimumAge);
        Assert.True(options.UseLastAccessTime);
    }

    [Fact]
    public void Empty_explicit_scope_and_invalid_age_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseJunkScanOptions(ArgumentSet.Parse(["--no-known"])));
        Assert.Throws<ArgumentException>(() => Program.ParseJunkScanOptions(ArgumentSet.Parse(["--minimum-age-hours", "-1"])));
        Assert.Throws<ArgumentException>(() => Program.ParseJunkScanOptions(ArgumentSet.Parse(["--minimum-age-hours", "8761"])));
    }

    [Fact]
    public void Access_age_override_is_explicit_and_does_not_consume_subcommand()
    {
        var parsed = ArgumentSet.Parse(["--write-age-only", "--no-default-exclusions", "scan"]);
        Assert.Equal("scan", parsed.FirstPositional);
        Assert.False(Program.ParseJunkScanOptions(parsed).UseLastAccessTime);
        Assert.False(Program.ParseJunkScanOptions(parsed).UseDefaultExclusions);
    }

    [Fact]
    public void Explicit_include_roots_and_disabled_age_are_independent_of_pattern_roots()
    {
        var args = ArgumentSet.Parse(["--no-age-filter", "scan", "--no-known", "--include-roots", @"C:\keep;E:\cache"]);
        Assert.Equal("scan", args.FirstPositional);
        var options = Program.ParseJunkScanOptions(args);
        Assert.Empty(options.SearchRoots);
        Assert.Equal([@"C:\keep", @"E:\cache"], options.IncludeRoots);
        Assert.False(options.UseAgeFilter);
        Assert.True(options.UseLastAccessTime);
    }
}
