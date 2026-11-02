using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class LeftoverNativeParityTests
{
    private static LeftoverScanner Scanner(TestEnvironment env, params RegistryLocation[] installerRoots)
        => new(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths),
            installerUserDataRoots: installerRoots);

    [Fact]
    public async Task Deeper_name_candidates_are_visible_but_protected_and_never_selected()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateDirectory("LocalAppData", "Neutral", "Storage", "Widget");
        var file = env.CreateFile("Roaming/Neutral/Storage/Widget.log", "application log");
        var unrelated = env.CreateFile("Roaming/Neutral/Storage/WidgetOther.log", "keep");
        var personal = env.CreateFile("User/Documents/Neutral/Storage/Widget.log", "personal");

        var result = await Scanner(env).ScanAsync(TestPrograms.Create("Widget"), []);

        foreach (var path in new[] { folder, file })
        {
            var candidate = Assert.Single(result.Items, i => i.FilePath == path);
            Assert.Equal(LeftoverConfidence.Low, candidate.Confidence);
            Assert.True(candidate.IsProtected);
            candidate.IsSelected = true;
            Assert.False(candidate.IsSelected);
        }
        Assert.DoesNotContain(result.Items, i => i.FilePath == unrelated || i.FilePath == personal);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(personal));
    }

    [Fact]
    public async Task Deeper_discovery_preserves_other_application_ownership_and_system_containers()
    {
        using var env = new TestEnvironment();
        var owned = env.CreateFile("LocalAppData/Neutral/Other/Widget.log", "other app");
        var otherRoot = Path.GetDirectoryName(owned)!;
        var system = env.CreateFile("LocalAppData/Microsoft/Neutral/Widget.log", "shared");
        var installed = env.CreateFile("Program Files/Neutral/Deep/Widget.log", "not a scan root");

        var result = await Scanner(env).ScanAsync(TestPrograms.Create("Widget"),
            [TestPrograms.Create("Other Product", installLocation: otherRoot)]);

        Assert.DoesNotContain(result.Items, i => i.FilePath == owned || i.FilePath == system || i.FilePath == installed);
    }

    [Fact]
    public async Task Deeper_discovery_announces_the_depth_boundary_instead_of_claiming_completion()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("LocalAppData/Neutral/Layer/Level/Store/Widget.log", "deep");

        var result = await Scanner(env).ScanAsync(TestPrograms.Create("Widget"), []);

        Assert.True(result.Truncated);
        Assert.Contains(result.Warnings, w => w.Contains("Discovery depth (4)", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, i => i.FilePath == file);
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("Widget Suite", "Widget Suite backup", null, true)]
    [InlineData("Widget", "MyWidget", null, false)]
    [InlineData("Widget", "WidgetOther", null, false)]
    [InlineData("Widget", "Widget2", null, true)]
    [InlineData("Widget Suite", "Widget Suite2 backup", null, true)]
    [InlineData("Widget", "éWidget", null, false)]
    [InlineData("Widget", "WidgetЖ", null, false)]
    [InlineData("Widget Suite", "éWidget Suite backup", null, false)]
    [InlineData("Widget Suite", "Widget SuiteЖ backup", null, false)]
    [InlineData("Widget Suite", "MyWidget Suite backup Widget Suite2 backup", null, true)]
    [InlineData("Player", "Player backup", null, false)]
    [InlineData("Contoso", "Contoso backup", "Contoso", false)]
    public async Task Native_name_boundaries_expand_only_protected_distinctive_discovery(
        string product, string leaf, string? publisher, bool expected)
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile($"LocalAppData/Neutral/Storage/{leaf}.log", "candidate");

        var result = await Scanner(env).ScanAsync(TestPrograms.Create(product, publisher), []);

        var matches = result.Items.Where(item => item.FilePath == file).ToList();
        if (!expected) Assert.Empty(matches);
        else
        {
            var item = Assert.Single(matches);
            Assert.True(item.IsProtected);
            Assert.Equal(LeftoverConfidence.Low, item.Confidence);
            item.IsSelected = true;
            Assert.False(item.IsSelected);
        }
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Installer_identity_seeds_are_report_only_for_exact_name_or_exact_location(bool byName)
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var root = env.AddRegistryKey("InstallerUserData");
        var properties = env.AddRegistryKey(
            @"InstallerUserData\S-1-5-18\Products\0123456789ABCDEF0123456789ABCDEF\InstallProperties",
            ("DisplayName", byName ? "WIDGET" : "Different display label", RegistryValueKind.String),
            ("InstallLocation", byName ? "" : install, RegistryValueKind.String));

        var result = await Scanner(env, root).ScanAsync(TestPrograms.Create("Widget", installLocation: install), []);
        var candidate = Assert.Single(result.Items, i => i.RegistryLocation == properties);

        Assert.True(candidate.IsProtected);
        Assert.Equal(LeftoverConfidence.Low, candidate.Confidence);
        Assert.Contains("identity seed", candidate.Reason, StringComparison.Ordinal);
        candidate.IsSelected = true;
        Assert.False(candidate.IsSelected);

        var guard = new PathGuard(env.Paths);
        var backup = new BackupService(env.Paths, env.Registry, new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, guard);
        var deletion = new LeftoverDeletionService(env.Registry, guard, backup,
            FileOperationLog.Null, new ProcessRunner(), env.Paths);
        var deletionResult = await deletion.DeleteAsync("Widget", [candidate], dryRun: false);
        Assert.Equal(DeletionStatus.Protected, Assert.Single(deletionResult.Items).Status);
        Assert.True(env.Registry.KeyExists(properties));
        Assert.False(Directory.Exists(env.Paths.BackupsDirectory));
    }

    [Fact]
    public async Task Installer_seed_scan_rejects_partial_names_foreign_owners_and_malformed_product_ids()
    {
        using var env = new TestEnvironment();
        var root = env.AddRegistryKey("InstallerUserData");
        var otherRoot = env.CreateDirectory("Fixtures", "Other");
        env.AddRegistryKey(@"InstallerUserData\S-1-5-18\Products\0123456789ABCDEF0123456789ABCDEF\InstallProperties",
            ("DisplayName", "Widget Other", RegistryValueKind.String));
        env.AddRegistryKey(@"InstallerUserData\S-1-5-18\Products\1123456789ABCDEF0123456789ABCDEF\InstallProperties",
            ("DisplayName", "Widget", RegistryValueKind.String), ("InstallLocation", otherRoot, RegistryValueKind.String));
        env.AddRegistryKey(@"InstallerUserData\S-1-5-18\Products\not-a-product\InstallProperties",
            ("DisplayName", "Widget", RegistryValueKind.String));

        var result = await Scanner(env, root).ScanAsync(TestPrograms.Create("Widget"),
            [TestPrograms.Create("Other Product", installLocation: otherRoot)]);

        Assert.DoesNotContain(result.Items, i => i.RegistryLocation?.Path.Contains("InstallerUserData", StringComparison.Ordinal) == true);
    }
}
