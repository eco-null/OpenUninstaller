using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class LeftoverReliabilityTests
{
    private static LeftoverScanner Scanner(TestEnvironment env)
        => new(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Protected_candidates_are_enforced_by_deletion_service_before_backup(bool dryRun)
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Shared/keep.txt", "shared app data");
        var guard = new PathGuard(env.Paths);
        var backup = new BackupService(env.Paths, env.Registry, new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, guard);
        var deletion = new LeftoverDeletionService(env.Registry, guard, backup,
            FileOperationLog.Null, new ProcessRunner(), env.Paths);
        var item = new LeftoverItem
        {
            Id = "protected-file", Kind = LeftoverKind.File, FilePath = file, DisplayPath = file,
            Confidence = LeftoverConfidence.Exact, Reason = "Trace evidence",
            ProtectedReason = "Another installed application owns this path."
        };

        var result = await deletion.DeleteAsync("Widget", [item], dryRun);

        Assert.Equal(DeletionStatus.Protected, Assert.Single(result.Items).Status);
        Assert.Equal(item.ProtectedReason, result.Items[0].Detail);
        Assert.Equal("shared app data", File.ReadAllText(file));
        Assert.False(Directory.Exists(env.Paths.BackupsDirectory));
    }

    [Fact]
    public async Task Shared_install_path_excludes_other_apps_path_and_firewall_entries()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var otherPath = env.CreateDirectory("Fixtures", "Widget", "SharedProduct");
        var executable = env.CreateFile("Fixtures/Widget/SharedProduct/widget.exe");
        env.AddRegistryKey("UserEnvironment", ("Path", otherPath, RegistryValueKind.String));
        env.AddRegistryKey("FirewallRules", ("Shared rule",
            $"v2.30|App={executable}|Name=Widget", RegistryValueKind.String));

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget", installLocation: install),
            [TestPrograms.Create("Other Product", installLocation: otherPath)]);

        Assert.DoesNotContain(scan.Items, i => i.Kind is LeftoverKind.PathEntry or LeftoverKind.FirewallRule);
        Assert.DoesNotContain(scan.Items, i => i.FilePath == install);
    }

    [Fact]
    public async Task Same_name_firewall_rules_retain_their_distinct_value_identities()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var executable = Path.Combine(install, "widget.exe");
        env.AddRegistryKey("FirewallRules",
            ("Widget TCP", $"v2.30|App={executable}|Name=Widget|Protocol=6", RegistryValueKind.String),
            ("Widget UDP", $"v2.30|App={executable}|Name=Widget|Protocol=17", RegistryValueKind.String));

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget", installLocation: install), []);
        var rules = scan.Items.Where(i => i.Kind == LeftoverKind.FirewallRule).ToList();

        Assert.Equal(2, rules.Count);
        Assert.Equal(2, rules.Select(i => i.Id).Distinct().Count());
        Assert.Contains(rules, i => i.ValueName == "Widget TCP");
        Assert.Contains(rules, i => i.ValueName == "Widget UDP");
    }

    [Fact]
    public async Task Uninstall_command_protects_other_app_with_missing_install_location()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var otherUninstaller = env.CreateFile("Fixtures/Widget/OtherProduct/remove.exe");

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget", installLocation: install),
            [TestPrograms.Create("Other Product", uninstallCommand: $"\"{otherUninstaller}\" /quiet")]);

        Assert.DoesNotContain(scan.Items, i => i.FilePath == install);
        Assert.Contains(scan.Warnings, w => w.Contains("Other Product", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scan_finds_product_temp_folders_and_exact_executable_crash_dumps()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var executable = env.CreateFile("Fixtures/Widget/widget.exe");
        var tempFolder = Path.Combine(env.Paths.Temp, "Widget");
        Directory.CreateDirectory(tempFolder);
        File.WriteAllText(Path.Combine(tempFolder, "old.tmp"), "temporary");
        var dumps = Path.Combine(env.Paths.LocalAppData, "CrashDumps");
        Directory.CreateDirectory(dumps);
        var own = Path.Combine(dumps, "widget.exe.1234.dmp");
        var unrelated = Path.Combine(dumps, "widgethelper.exe.1234.dmp");
        File.WriteAllText(own, "dump");
        File.WriteAllText(unrelated, "keep");

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget", installLocation: install,
            displayIcon: $"\"{executable}\",0"), []);

        Assert.Contains(scan.Items, i => i.FilePath == tempFolder);
        var dump = Assert.Single(scan.Items, i => i.FilePath == own);
        Assert.Equal(LeftoverConfidence.Strong, dump.Confidence);
        Assert.False(dump.IsSelected);
        Assert.DoesNotContain(scan.Items, i => i.FilePath == unrelated || i.FilePath == dumps);
    }

    [Fact]
    public async Task Task_with_owned_first_action_and_unrelated_second_action_is_preserved()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var own = Path.Combine(install, "widget.exe");
        var unrelated = env.CreateFile("Fixtures/Other/helper.exe");
        var task = env.CreateFile("Tasks/WidgetMaintenance", $"""
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><Exec><Command>{own}</Command></Exec><Exec><Command>{unrelated}</Command></Exec></Actions>
            </Task>
            """);

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget", installLocation: install), []);

        Assert.DoesNotContain(scan.Items, i => i.Kind == LeftoverKind.ScheduledTask && i.FilePath == task);
        Assert.True(File.Exists(task));
    }

    [Fact]
    public async Task Shortcut_scan_preserves_named_documents_and_folders_and_does_not_preselect_name_only_links()
    {
        using var env = new TestEnvironment();
        var document = Path.Combine(env.Paths.UserDesktop, "Widget.txt");
        var folder = Path.Combine(env.Paths.UserDesktop, "Widget");
        var shortcut = Path.Combine(env.Paths.UserDesktop, "Widget.url");
        File.WriteAllText(document, "personal notes");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "personal notes");
        File.WriteAllText(shortcut, "[InternetShortcut]\nURL=https://example.invalid/widget");

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget"), []);

        Assert.DoesNotContain(scan.Items, i => i.FilePath == document || i.FilePath == folder);
        var link = Assert.Single(scan.Items, i => i.FilePath == shortcut);
        Assert.Equal(LeftoverConfidence.Strong, link.Confidence);
        Assert.False(link.IsSelected);
    }

    [Fact]
    public async Task Task_parser_uses_action_xml_not_commented_command_and_supports_namespace_prefixes()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var own = Path.Combine(install, "widget.exe");
        var unrelated = env.CreateFile("Fixtures/Other/helper.exe");
        var misleading = env.CreateFile("Tasks/WidgetComment", $"""
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <!-- <Command>{own}</Command> -->
              <Actions><Exec><Command>{unrelated}</Command></Exec></Actions>
            </Task>
            """);
        var namespaced = env.CreateFile("Tasks/WidgetPrefixed", $"""
            <t:Task xmlns:t="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <t:Actions><t:Exec><t:Command>{own}</t:Command></t:Exec></t:Actions>
            </t:Task>
            """);

        var scan = await Scanner(env).ScanAsync(TestPrograms.Create("Widget", installLocation: install), []);

        Assert.DoesNotContain(scan.Items, i => i.Kind == LeftoverKind.ScheduledTask && i.FilePath == misleading);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.ScheduledTask && i.FilePath == namespaced);
    }

    [Fact]
    public void Scan_caps_are_reported_and_exact_limit_does_not_claim_truncation()
    {
        var exact = Result();
        Assert.Equal(2, LeftoverScanner.BoundedEntries(["one", "two"], 2, "fixture", exact, default).Count);
        Assert.False(exact.Truncated);
        var truncated = Result();
        Assert.Equal(2, LeftoverScanner.BoundedEntries(["one", "two", "three"], 2, "fixture", truncated, default).Count);
        Assert.True(truncated.Truncated);
        Assert.Contains("fixture", Assert.Single(truncated.Warnings));
    }

    [Fact]
    public void Bounded_enumeration_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LeftoverScanner.BoundedEntries(
            ["one"], 2, "fixture", Result(), cancellation.Token));
    }

    private static LeftoverScanResult Result() => new() { ProgramId = "fixture", ProgramName = "Widget" };
}
