using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class DeepCleanupTests
{
    private static LeftoverScanner CreateScanner(TestEnvironment env)
        => new(
            env.Registry,
            new TraceStore(env.Paths),
            env.Paths,
            new PathGuard(env.Paths),
            new IgnoredLeftoverStore(env.Paths));

    [Fact]
    public void RegistryGuard_scopes_association_and_com_entries_to_direct_children()
    {
        var association = new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\Widget.Document");
        Assert.False(RegistryGuard.ValidateForDeletion(association).Allowed);
        Assert.True(RegistryGuard.ValidateForDeletion(association, allowAssociationEntry: true).Allowed);

        var container = new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\CLSID");
        Assert.False(RegistryGuard.ValidateForDeletion(container, allowAssociationEntry: true).Allowed);

        var nested = new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Classes\Widget.Document\shell\open\command");
        Assert.False(RegistryGuard.ValidateForDeletion(nested, allowAssociationEntry: true).Allowed);

        var com = new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Classes\CLSID\{11111111-1111-1111-1111-111111111111}");
        Assert.False(RegistryGuard.ValidateForDeletion(com).Allowed);
        Assert.True(RegistryGuard.ValidateForDeletion(com, allowComEntry: true).Allowed);

        var notAGuid = new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\CLSID\Notes");
        Assert.False(RegistryGuard.ValidateForDeletion(notAGuid, allowComEntry: true).Allowed);
    }

    [Fact]
    public void Path_guard_pins_the_key_and_rejects_system_segments()
    {
        using var env = new TestEnvironment();
        var fixture = env.CreateDirectory("Fixtures", "Widget Tools");

        var userRoot = env.Paths.UserEnvironmentRoot;
        var allowed = RegistryGuard.ValidatePathEntryForDeletion(
            userRoot, "Path", fixture, env.Paths, machineWide: false);
        Assert.True(allowed.Allowed, allowed.Reason);

        var wrongKey = new RegistryLocation(
            RegistryHive.CurrentUser, RegistryView.Registry64, env.RegistryBase + "\\Elsewhere");
        Assert.False(RegistryGuard.ValidatePathEntryForDeletion(
            wrongKey, "Path", fixture, env.Paths, machineWide: false).Allowed);

        foreach (var segment in new[]
                 {
                     env.Paths.WindowsDirectory,
                     Path.Combine(env.Paths.ProgramFiles, "Widget"),
                     @"" + env.Root + "\\Widget;evil",
                     @"..\Widget"
                 })
        {
            Assert.False(RegistryGuard.ValidatePathEntryForDeletion(
                userRoot, "Path", segment, env.Paths, machineWide: false).Allowed, segment);
        }

        Assert.False(RegistryGuard.ValidatePathEntryForDeletion(
            env.Paths.MachineEnvironmentRoot, "PATHX", fixture, env.Paths, machineWide: true).Allowed);
    }

    [Fact]
    public void Firewall_guard_accepts_only_the_rule_store_root()
    {
        using var env = new TestEnvironment();
        Assert.True(RegistryGuard.ValidateFirewallRuleValue(
            env.Paths.FirewallRulesRoot, "Widget Rule", env.Paths.FirewallRulesRoot).Allowed);
        Assert.False(RegistryGuard.ValidateFirewallRuleValue(
            env.Paths.FirewallRulesRoot, " ", env.Paths.FirewallRulesRoot).Allowed);
        Assert.False(RegistryGuard.ValidateFirewallRuleValue(
            env.Paths.UserEnvironmentRoot, "Widget Rule", env.Paths.FirewallRulesRoot).Allowed);
    }

    [Fact]
    public async Task Deep_scan_finds_associations_com_drivers_path_firewall_and_com_tasks()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var exe = env.CreateFile("Fixtures/Widget/widget.exe");
        var dll = env.CreateFile("Fixtures/Widget/widget.dll");

        env.AddRegistryKey(
            "Classes\\Widget.Document\\shell\\open\\command",
            (string.Empty, $"\"{exe}\" \"%1\"", RegistryValueKind.String));
        env.AddRegistryKey(
            "Classes\\CLSID\\{11111111-1111-1111-1111-111111111111}\\InprocServer32",
            (string.Empty, $"\"{dll}\"", RegistryValueKind.String));

        env.AddRegistryKey(
            "Services\\WidgetDriver",
            ("DisplayName", "Widget Driver", RegistryValueKind.String),
            ("ImagePath", Path.Combine(install, "gone.sys"), RegistryValueKind.ExpandString),
            ("Type", 1, RegistryValueKind.DWord),
            ("Start", 3, RegistryValueKind.DWord));
        env.AddRegistryKey(
            "Services\\WidgetBootDriver",
            ("DisplayName", "Widget Boot Driver", RegistryValueKind.String),
            ("ImagePath", Path.Combine(install, "boot.sys"), RegistryValueKind.ExpandString),
            ("Type", 1, RegistryValueKind.DWord),
            ("Start", 0, RegistryValueKind.DWord));
        env.AddRegistryKey(
            "Services\\WidgetHelper",
            ("DisplayName", "Widget Helper", RegistryValueKind.String),
            ("ImagePath", $"\"{exe}\" /service", RegistryValueKind.ExpandString),
            ("Type", 16, RegistryValueKind.DWord),
            ("Start", 3, RegistryValueKind.DWord));

        env.AddRegistryKey(
            "MachineEnvironment",
            ("Path", $"C:\\Windows;{install}", RegistryValueKind.ExpandString));

        var ruleName = "Widget Firewall Rule";
        env.AddRegistryKey(
            "FirewallRules",
            (ruleName,
                $"v2.30|Action=Allow|Active=TRUE|Dir=In|App={exe}|Name={ruleName}",
                RegistryValueKind.String));

        var comTask = env.CreateFile(
            "Tasks/Widget/WidgetComUpdate",
            """
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <ComHandler>
                    <ClassId>{11111111-1111-1111-1111-111111111111}</ClassId>
                  </ComHandler>
                </Exec>
              </Actions>
            </Task>
            """);

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget", "Widget Corp", install);
        var scan = await scanner.ScanAsync(program, otherPrograms: []);

        Assert.Contains(scan.Items, i => i.IsAssociationEntry && !i.IsProtected);
        Assert.Contains(scan.Items, i => i.IsComEntry && !i.IsProtected);
        Assert.Contains(scan.Items, i => i.IsDriverEntry && !i.IsProtected && i.Kind == LeftoverKind.Service);
        Assert.Contains(scan.Items, i =>
            i.IsDriverEntry && i.IsProtected && i.DisplayPath.Contains("WidgetBootDriver", StringComparison.Ordinal));
        Assert.Contains(scan.Items, i => i.IsOwnedServiceEntry && !i.IsProtected);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.PathEntry
                                          && i.PathSegment?.Contains("Fixtures", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.FirewallRule && i.ValueName == ruleName);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.ScheduledTask && i.FilePath == comTask);
        Assert.DoesNotContain(scan.Items, i => i.Kind == LeftoverKind.PathEntry
                                               && i.PathSegment?.Contains("C:\\Windows", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Path_and_firewall_items_delete_through_backup_and_verify_each_change()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Fixtures", "Widget");
        var exe = env.CreateFile("Fixtures/Widget/widget.exe");
        var environment = env.AddRegistryKey(
            "MachineEnvironment",
            ("Path", $"{install};C:\\Tools", RegistryValueKind.ExpandString));
        var firewall = env.AddRegistryKey(
            "FirewallRules",
            ("Widget Rule",
                $"v2.30|Action=Allow|Active=TRUE|Dir=In|App={exe}|Name=Widget Rule",
                RegistryValueKind.String));

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget", "Widget Corp", install);
        var scan = await scanner.ScanAsync(program, otherPrograms: []);
        var items = scan.Items
            .Where(i => i.Kind is LeftoverKind.PathEntry or LeftoverKind.FirewallRule)
            .ToList();
        Assert.Equal(2, items.Count);

        var pathGuard = new PathGuard(env.Paths);
        var backup = new BackupService(
            env.Paths,
            env.Registry,
            new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow),
            FileOperationLog.Null,
            pathGuard);
        var deletion = new LeftoverDeletionService(
            env.Registry,
            pathGuard,
            backup,
            FileOperationLog.Null,
            new ProcessRunner(),
            env.Paths);

        var dryRun = await deletion.DeleteAsync("Widget", items, dryRun: true);
        Assert.Equal(2, dryRun.DeletedCount);
        Assert.Equal($"{install};C:\\Tools", env.Registry.GetValue(environment, "Path")?.AsString());
        Assert.NotNull(env.Registry.GetValue(firewall, "Widget Rule"));

        var result = await deletion.DeleteAsync("Widget", items, dryRun: false);

        Assert.True(result.Succeeded, string.Join("; ", result.Items.Select(i => i.Detail)));
        Assert.Equal(2, result.DeletedCount);
        Assert.NotNull(result.BackupDirectory);
        Assert.True(Directory.Exists(result.BackupDirectory));
        Assert.Equal("C:\\Tools", env.Registry.GetValue(environment, "Path")?.AsString());
        Assert.Null(env.Registry.GetValue(firewall, "Widget Rule"));
    }

    [Fact]
    public void Shortcut_resolver_returns_null_instead_of_guessing()
    {
        using var env = new TestEnvironment();
        var notAShortcut = env.CreateFile("Fixtures/Widget.txt", "not a link");
        var brokenLink = env.CreateFile("Fixtures/Broken.lnk", "not a real shell link");

        Assert.Null(ShortcutResolver.TryResolve(notAShortcut));
        Assert.Null(ShortcutResolver.TryResolve(brokenLink));
        Assert.Null(ShortcutResolver.TryResolve(Path.Combine(env.Root, "missing.lnk")));
    }
}
