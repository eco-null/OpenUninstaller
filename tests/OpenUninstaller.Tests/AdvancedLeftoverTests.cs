using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

/// <summary>
/// Covers the evidence that is not a plain folder name: registry values added to shared keys,
/// App Paths / Classes\Applications registrations, orphaned services, and scheduled tasks.
/// Every fixture is isolated in the test environment; no real application is uninstalled.
/// </summary>
public sealed class AdvancedLeftoverTests
{
    [Fact]
    public async Task Trace_values_in_existing_keys_are_value_items_not_key_items()
    {
        using var env = new TestEnvironment();
        var installLocation = env.CreateDirectory("Program Files", "Widget");
        env.AddRegistryKey(
            "Shared\\Flags",
            ("KeepMe", "keep", RegistryValueKind.String),
            ("WidgetFlag", "1", RegistryValueKind.String));

        var keyPath = $"{env.RegistryBase}\\Shared\\Flags";
        var trace = new TraceDocument
        {
            SchemaVersion = 2,
            Id = "trace-widget-values",
            ProgramName = "Widget",
            Publisher = "Contoso",
            InstallLocation = installLocation,
            CreatedAt = DateTimeOffset.UtcNow,
            RegistryValues =
            [
                new TracedRegistryKey
                {
                    Hive = RegistryHive.CurrentUser,
                    View = RegistryView.Registry64,
                    Path = keyPath,
                    ValueNames = ["WidgetFlag"]
                }
            ]
        };
        new TraceStore(env.Paths).Save(trace);

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget", publisher: "Contoso", installLocation: installLocation);
        var result = await scanner.ScanAsync(program, otherPrograms: []);

        var matching = result.Items
            .Where(i => string.Equals(i.RegistryLocation?.Path, keyPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var value = Assert.Single(matching);
        Assert.Equal(LeftoverKind.RegistryValue, value.Kind);
        Assert.Equal("WidgetFlag", value.ValueName);
        Assert.Equal(LeftoverConfidence.Exact, value.Confidence);
        Assert.Equal("Value", value.KindLabel);
        Assert.DoesNotContain(result.Items, i =>
            i.Kind == LeftoverKind.RegistryKey
            && string.Equals(i.RegistryLocation?.Path, keyPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Legacy_schema_1_registry_keys_are_protected_instead_of_offered_for_deletion()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey("Legacy\\Flags", ("WidgetFlag", "1", RegistryValueKind.String));
        var keyPath = $"{env.RegistryBase}\\Legacy\\Flags";
        var trace = new TraceDocument
        {
            Id = "trace-widget-legacy",
            ProgramName = "Widget",
            CreatedAt = DateTimeOffset.UtcNow,
            RegistryKeys =
            [
                new TracedRegistryKey
                {
                    Hive = RegistryHive.CurrentUser,
                    View = RegistryView.Registry64,
                    Path = keyPath,
                    ValueNames = ["WidgetFlag"]
                }
            ]
        };
        new TraceStore(env.Paths).Save(trace);

        var scanner = CreateScanner(env);
        var result = await scanner.ScanAsync(TestPrograms.Create("Widget"), otherPrograms: []);

        var item = Assert.Single(result.Items.Where(i => i.Kind == LeftoverKind.RegistryKey
            && string.Equals(i.RegistryLocation?.Path, keyPath, StringComparison.OrdinalIgnoreCase)));
        Assert.True(item.IsProtected);
        Assert.False(item.IsSelected);
        Assert.Contains("schema 1", item.ProtectedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Warnings, w => w.Contains("schema 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task App_paths_entry_pointing_at_a_missing_executable_is_found()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "App Paths\\widget.exe",
            (string.Empty, @"C:\definitely-missing\widget.exe", RegistryValueKind.String));

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create(
            "Widget",
            publisher: "Contoso",
            installLocation: env.CreateDirectory("Program Files", "Widget"));
        var result = await scanner.ScanAsync(program, otherPrograms: []);

        var matching = result.Items.Where(i =>
            i.RegistryLocation?.Path.EndsWith("App Paths\\widget.exe", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.NotEmpty(matching);
        var item = matching[0];
        Assert.Equal(LeftoverKind.RegistryKey, item.Kind);
        Assert.Equal(LeftoverConfidence.Strong, item.Confidence);
        Assert.Contains("missing executable", item.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(item.IsProtected);
    }

    [Fact]
    public async Task Classes_applications_entry_for_a_missing_command_is_found()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "Classes\\Applications\\widget.exe\\shell\\open\\command",
            (string.Empty, "\"C:\\definitely-missing\\widget.exe\" --open", RegistryValueKind.String));

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget", installLocation: env.CreateDirectory("Program Files", "Widget"));
        var result = await scanner.ScanAsync(program, otherPrograms: []);

        Assert.Contains(result.Items, i =>
            i.RegistryLocation?.Path.EndsWith(
                "Classes\\Applications\\widget.exe", StringComparison.OrdinalIgnoreCase) == true
            && i.Confidence == LeftoverConfidence.Strong);
    }

    [Fact]
    public async Task Orphaned_service_with_matching_name_is_found()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "Services\\WidgetHelper",
            ("DisplayName", "Widget Helper", RegistryValueKind.String),
            ("ImagePath", "\"C:\\definitely-missing\\widgetsvc.exe\" -k", RegistryValueKind.String));

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget", publisher: "Contoso");
        var result = await scanner.ScanAsync(program, otherPrograms: []);

        var item = Assert.Single(result.Items.Where(i => i.Kind == LeftoverKind.Service));
        Assert.EndsWith("Services\\WidgetHelper", item.RegistryLocation?.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LeftoverConfidence.Strong, item.Confidence);
        Assert.Contains("missing binary", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scheduled_task_with_missing_command_is_found()
    {
        using var env = new TestEnvironment();
        var taskFile = env.CreateFile(
            "Tasks/Widget/Update",
            """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>C:\definitely-missing\widget.exe</Command>
                  <Arguments>--update</Arguments>
                </Exec>
              </Actions>
            </Task>
            """);

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget", publisher: "Contoso");
        var result = await scanner.ScanAsync(program, otherPrograms: []);

        var item = Assert.Single(result.Items.Where(i => i.Kind == LeftoverKind.ScheduledTask));
        Assert.Equal("Widget\\Update", item.TaskName);
        Assert.Equal(taskFile, item.FilePath);
        Assert.Equal(LeftoverConfidence.Strong, item.Confidence);
        Assert.Contains("missing executable", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task App_paths_entry_can_be_backed_up_and_deleted_with_the_scoped_guard()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "App Paths\\widget.exe",
            (string.Empty, @"C:\definitely-missing\widget.exe", RegistryValueKind.String));

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget");
        var scan = await scanner.ScanAsync(program, otherPrograms: []);
        var item = scan.Items.First(i =>
            i.RegistryLocation?.Path.EndsWith("App Paths\\widget.exe", StringComparison.OrdinalIgnoreCase) == true);

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

        var result = await deletion.DeleteAsync("Widget", [item], dryRun: false);

        Assert.True(result.Succeeded, string.Join("; ", result.Items.Select(i => i.Detail)));
        Assert.Equal(1, result.DeletedCount);
        Assert.False(env.Registry.KeyExists(item.RegistryLocation!.Value));
    }

    [Fact]
    public async Task Deleting_a_traced_value_keeps_sibling_values_and_creates_a_backup()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "Shared\\Flags",
            ("KeepMe", "keep", RegistryValueKind.String),
            ("WidgetFlag", "1", RegistryValueKind.String));

        var keyPath = $"{env.RegistryBase}\\Shared\\Flags";
        var trace = new TraceDocument
        {
            SchemaVersion = 2,
            Id = "trace-widget-delete-value",
            ProgramName = "Widget",
            CreatedAt = DateTimeOffset.UtcNow,
            RegistryValues =
            [
                new TracedRegistryKey
                {
                    Hive = RegistryHive.CurrentUser,
                    View = RegistryView.Registry64,
                    Path = keyPath,
                    ValueNames = ["WidgetFlag"]
                }
            ]
        };
        new TraceStore(env.Paths).Save(trace);

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget");
        var scan = await scanner.ScanAsync(program, otherPrograms: []);
        var item = Assert.Single(scan.Items.Where(i => i.Kind == LeftoverKind.RegistryValue));

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

        var result = await deletion.DeleteAsync("Widget", [item], dryRun: false);
        var location = item.RegistryLocation!.Value;

        Assert.True(result.Succeeded, string.Join("; ", result.Items.Select(i => i.Detail)));
        Assert.Equal(1, result.DeletedCount);
        Assert.Null(env.Registry.GetValue(location, "WidgetFlag"));
        Assert.NotNull(env.Registry.GetValue(location, "KeepMe"));
        Assert.NotNull(result.BackupId);
    }

    [Fact]
    public async Task Scheduled_task_deletion_uses_schtasks_and_creates_a_backup()
    {
        using var env = new TestEnvironment();
        var taskFile = env.CreateFile(
            "Tasks/Widget/Update",
            "<Task><Actions><Exec><Command>C:\\definitely-missing\\widget.exe</Command></Exec></Actions></Task>");

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create("Widget");
        var scan = await scanner.ScanAsync(program, otherPrograms: []);
        var item = Assert.Single(scan.Items.Where(i => i.Kind == LeftoverKind.ScheduledTask));

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
            new TaskDeletingRunner(env.Paths.ScheduledTasksDirectory),
            env.Paths);

        var result = await deletion.DeleteAsync("Widget", [item], dryRun: false);

        Assert.True(result.Succeeded, string.Join("; ", result.Items.Select(i => i.Detail)));
        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(taskFile));
        Assert.NotNull(result.BackupId);
    }

    private static LeftoverScanner CreateScanner(TestEnvironment env)
        => new(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

    /// <summary>Simulates schtasks /Delete by removing the task's XML file.</summary>
    private sealed class TaskDeletingRunner(string tasksRoot) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            bool shellExecute,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            if (arguments.Contains("/Delete", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(arguments, "/TN \"([^\"]+)\"");
                if (match.Success)
                {
                    var path = ScheduledTaskGuard.XmlPath(
                        tasksRoot, match.Groups[1].Value);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            return Task.FromResult(new ProcessResult { Started = true, ExitCode = 0 });
        }
    }
}
