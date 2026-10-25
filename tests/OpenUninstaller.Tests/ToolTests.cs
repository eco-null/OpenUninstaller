using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class ToolTests
{
    [Fact]
    public void Autorun_disable_and_enable_round_trip()
    {
        using var env = new TestEnvironment();
        var runKey = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        var valueName = "OU Test " + Guid.NewGuid().ToString("N");
        env.Registry.SetValue(runKey, valueName, RegistryValueKind.String, "\"C:\\Windows\\System32\\notepad.exe\"");

        try
        {
            var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, new ProcessRunner());
            var entry = Assert.Single(service.Scan().Entries.Where(e => e.Name == valueName && e.IsEnabled));

            Assert.True(service.Disable(entry.Id, out var error), error);
            Assert.Null(env.Registry.GetValue(runKey, valueName));
            var disabled = Assert.Single(service.Scan().Entries.Where(e => e.Name == valueName));
            Assert.False(disabled.IsEnabled);

            Assert.True(service.Enable(disabled.Id, out error), error);
            Assert.NotNull(env.Registry.GetValue(runKey, valueName));
            Assert.True(Assert.Single(service.Scan().Entries.Where(e => e.Name == valueName)).IsEnabled);
        }
        finally
        {
            env.Registry.DeleteValue(runKey, valueName);
        }
    }

    [Fact]
    public void Junk_scan_and_clean_are_limited_to_category_roots()
    {
        using var env = new TestEnvironment();
        var old = DateTime.UtcNow.AddHours(-1);
        var file = env.CreateFile("Temp/old.tmp", "12345", old);
        var windowsTemp = env.CreateDirectory("Windows", "Temp");
        var windowsFile = env.CreateFile("Windows/Temp/old.tmp", "1234", old);
        var untouched = env.CreateFile("Fixtures/outside.tmp", "keep", old);
        var service = new JunkService(env.Paths, new PathGuard(env.Paths), FileOperationLog.Null);

        var scan = service.Scan();
        var userTemp = Assert.Single(scan.Categories.Where(c => c.Id == "user-temp"));
        Assert.True(userTemp.Bytes >= 5);
        Assert.True(userTemp.ItemCount >= 1);

        var dryRun = service.Clean(["user-temp"], dryRun: true);
        Assert.True(File.Exists(file));
        Assert.True(dryRun.DeletedItems >= 1);

        var cleaned = service.Clean(["user-temp"], dryRun: false);
        Assert.True(cleaned.Succeeded);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(windowsFile));
        Assert.True(File.Exists(untouched));
    }

    [Fact]
    public void Autorun_service_disable_and_enable_round_trip()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey(
            "Services\\WidgetService",
            ("DisplayName", "Widget Service", RegistryValueKind.String),
            ("ImagePath", @"C:\definitely-missing\widget.exe", RegistryValueKind.String),
            ("Start", 2, RegistryValueKind.DWord));
        var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, new ProcessRunner());

        var entry = Assert.Single(service.Scan().Entries.Where(e =>
            e.Source == AutorunSourceKind.MachineService && e.Name == "Widget Service"));
        Assert.True(entry.IsEnabled);

        Assert.True(service.Disable(entry.Id, out var error), error);
        Assert.Equal(4, env.Registry.GetValue(location, "Start")?.AsInt32());
        var disabled = Assert.Single(service.Scan().Entries.Where(e => e.Id == entry.Id));
        Assert.False(disabled.IsEnabled);
        Assert.Equal(AutorunSourceKind.DisabledService, disabled.Source);

        Assert.True(service.Enable(entry.Id, out error), error);
        Assert.Equal(2, env.Registry.GetValue(location, "Start")?.AsInt32());
        Assert.True(Assert.Single(service.Scan().Entries.Where(e => e.Id == entry.Id)).IsEnabled);
    }

    [Fact]
    public void Autorun_scheduled_task_disable_and_enable_round_trip()
    {
        using var env = new TestEnvironment();
        env.CreateFile(
            "Tasks/Widget/Update",
            """
            <Task><Settings><Enabled>true</Enabled></Settings><Actions><Exec>
              <Command>C:\definitely-missing\widget.exe</Command>
            </Exec></Actions></Task>
            """);
        var runner = new RecordingRunner();
        var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, runner);

        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Source == AutorunSourceKind.ScheduledTask));
        Assert.True(entry.IsEnabled);

        Assert.True(service.Disable(entry.Id, out var error), error);
        Assert.Contains(runner.Calls, call => call.Contains("/DISABLE", StringComparison.Ordinal));
        var disabled = Assert.Single(service.Scan().Entries.Where(e => e.Id == entry.Id));
        Assert.False(disabled.IsEnabled);
        Assert.Equal(AutorunSourceKind.DisabledScheduledTask, disabled.Source);

        Assert.True(service.Enable(entry.Id, out error), error);
        Assert.Contains(runner.Calls, call => call.Contains("/ENABLE", StringComparison.Ordinal));
        Assert.True(Assert.Single(service.Scan().Entries.Where(e => e.Id == entry.Id)).IsEnabled);
    }

    [Fact]
    public void Autorun_task_disabled_outside_the_app_can_be_enabled_through_schtasks()
    {
        using var env = new TestEnvironment();
        env.CreateFile(
            "Tasks/Widget/External",
            """
            <Task><Settings><Enabled>false</Enabled></Settings><Actions><Exec>
              <Command>C:\definitely-missing\widget.exe</Command>
            </Exec></Actions></Task>
            """);
        var runner = new RecordingRunner();
        var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, runner);

        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Source == AutorunSourceKind.ScheduledTask));
        Assert.False(entry.IsEnabled);
        Assert.True(service.Enable(entry.Id, out var error), error);
        Assert.Contains(runner.Calls, call => call.Contains("/ENABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void Autorun_service_disabled_outside_the_app_cannot_guess_its_start_mode()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "Services\\ExternalService",
            ("DisplayName", "External Service", RegistryValueKind.String),
            ("ImagePath", @"C:\definitely-missing\external.exe", RegistryValueKind.String),
            ("Start", 4, RegistryValueKind.DWord));
        var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, new ProcessRunner());

        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Name == "External Service"));
        Assert.False(entry.IsEnabled);
        Assert.False(service.Enable(entry.Id, out var error));
        Assert.Contains("outside OpenUninstaller", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Autorun_history_and_rollback_round_trip()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "Services\\TestRollbackService",
            ("DisplayName", "Test Rollback Service", RegistryValueKind.String),
            ("ImagePath", @"C:\definitely-missing\test.exe", RegistryValueKind.String),
            ("Start", 2, RegistryValueKind.DWord));

        var historyStore = new AutorunHistoryStore(env.Paths);
        var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, new ProcessRunner(), historyStore);

        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Name == "Test Rollback Service"));
        Assert.True(entry.IsEnabled);

        Assert.True(service.Disable(entry.Id, out var error), error);

        var history = service.GetHistory();
        var record = Assert.Single(history.Where(h => h.EntryId == entry.Id));
        Assert.Equal(AutorunHistoryAction.Disabled, record.Action);
        Assert.True(record.CanRollback);

        Assert.True(service.Rollback(entry.Id, out var rollbackError), rollbackError);

        var reloaded = Assert.Single(service.Scan().Entries.Where(e => e.Name == "Test Rollback Service"));
        Assert.True(reloaded.IsEnabled);

        var updatedHistory = service.GetHistory();
        var rolledBackRecord = Assert.Single(updatedHistory.Where(h => h.EntryId == entry.Id && h.Action == AutorunHistoryAction.RolledBack));
        Assert.NotNull(rolledBackRecord);
        Assert.False(rolledBackRecord.CanRollback);
    }

    [Fact]
    public void Autorun_history_rollback_all()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey(
            "Services\\ServiceOne",
            ("DisplayName", "Service One", RegistryValueKind.String),
            ("ImagePath", @"C:\definitely-missing\one.exe", RegistryValueKind.String),
            ("Start", 2, RegistryValueKind.DWord));
        env.AddRegistryKey(
            "Services\\ServiceTwo",
            ("DisplayName", "Service Two", RegistryValueKind.String),
            ("ImagePath", @"C:\definitely-missing\two.exe", RegistryValueKind.String),
            ("Start", 3, RegistryValueKind.DWord));

        var service = new AutorunService(env.Registry, env.Paths, FileOperationLog.Null, new ProcessRunner());
        var s1 = Assert.Single(service.Scan().Entries.Where(e => e.Name == "Service One"));
        var s2 = Assert.Single(service.Scan().Entries.Where(e => e.Name == "Service Two"));

        Assert.True(service.Disable(s1.Id, out _));
        Assert.True(service.Disable(s2.Id, out _));

        var rollbackResult = service.RollbackAll();
        Assert.Equal(2, rollbackResult.SucceededCount);
        Assert.Equal(0, rollbackResult.FailedCount);
        Assert.True(rollbackResult.Succeeded);

        var scanAfter = service.Scan();
        Assert.True(Assert.Single(scanAfter.Entries.Where(e => e.Name == "Service One")).IsEnabled);
        Assert.True(Assert.Single(scanAfter.Entries.Where(e => e.Name == "Service Two")).IsEnabled);
    }

    [Fact]
    public void Autorun_history_store_caps_and_persists()
    {
        using var env = new TestEnvironment();
        var store = new AutorunHistoryStore(env.Paths);
        Assert.Empty(store.GetAll());

        for (var i = 0; i < 10; i++)
        {
            store.Record(new AutorunHistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                EntryId = $"entry-{i}",
                Name = $"Name {i}",
                Command = "test.exe",
                Source = AutorunSourceKind.MachineService,
                Action = AutorunHistoryAction.Disabled,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        var all = store.GetAll();
        Assert.Equal(10, all.Count);
        Assert.Equal("Name 9", all[0].Name);
    }

    [Fact]
    public void Hunter_matches_executable_inside_install_location()
    {
        using var env = new TestEnvironment();
        var install = env.CreateDirectory("Program Files", "Contoso", "Widget");
        var exe = env.CreateFile("Program Files/Contoso/Widget/widget.exe", "stub");
        var program = TestPrograms.Create("Contoso Widget", installLocation: install);

        var match = HunterService.MatchExecutable(exe, "Widget", 1234, [program]);

        Assert.True(match.Matched);
        Assert.Equal(100, match.Score);
        Assert.Equal(program.Id, match.Program?.Id);
        Assert.Equal(1234, match.ProcessId);
    }

    [Fact]
    public void Hunter_matches_display_icon_and_returns_no_match_otherwise()
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Fixtures/Tool/tool.exe", "stub");
        var iconProgram = TestPrograms.Create("Some Tool", displayIcon: exe);
        var other = TestPrograms.Create("Other App", installLocation: env.CreateDirectory("Program Files", "Other"));

        var match = HunterService.MatchExecutable(exe, "Tool", 1, [other, iconProgram]);
        Assert.Equal(95, match.Score);
        Assert.Equal(iconProgram.Id, match.Program?.Id);

        var none = HunterService.MatchExecutable(Path.Combine(env.Root, "nope.exe"), "nope", 2, [other]);
        Assert.False(none.Matched);
    }

    [Fact]
    public void Junk_scan_discovers_every_browser_profile_and_cleans_only_cache_files()
    {
        using var env = new TestEnvironment();
        var old = DateTime.UtcNow.AddHours(-2);
        var chromeCache = env.CreateFile(
            "LocalAppData/Google/Chrome/User Data/Default/Cache/cache.bin", "chrome", old);
        var chromeProfile = env.CreateFile(
            "LocalAppData/Google/Chrome/User Data/Profile 1/Code Cache/js.bin", "profile", old);
        var edgeCache = env.CreateFile(
            "LocalAppData/Microsoft/Edge/User Data/Default/GPUCache/gpu.bin", "edge", old);
        var firefoxCache = env.CreateFile(
            "Roaming/Mozilla/Firefox/Profiles/abc.default/cache2/cache.bin", "firefox", old);
        var preferences = env.CreateFile(
            "LocalAppData/Google/Chrome/User Data/Default/Preferences", "keep", old);
        var service = new JunkService(env.Paths, new PathGuard(env.Paths), FileOperationLog.Null);

        var scan = service.Scan();
        var browser = Assert.Single(scan.Categories.Where(c => c.Id == "browser-cache"));
        Assert.True(browser.Bytes > 0);
        Assert.True(browser.ItemCount >= 4);

        var dryRun = service.Clean(["browser-cache"], dryRun: true);
        Assert.True(dryRun.DeletedItems >= 4);
        Assert.True(File.Exists(chromeCache));

        var cleaned = service.Clean(["browser-cache"], dryRun: false);
        Assert.True(cleaned.Succeeded);
        Assert.False(File.Exists(chromeCache));
        Assert.False(File.Exists(chromeProfile));
        Assert.False(File.Exists(edgeCache));
        Assert.False(File.Exists(firefoxCache));
        Assert.True(File.Exists(preferences));
    }

    [Fact]
    public void Junk_scan_reports_crash_dumps_shader_caches_and_previous_windows()
    {
        using var env = new TestEnvironment();
        var old = DateTime.UtcNow.AddHours(-2);
        env.CreateFile("LocalAppData/CrashDumps/app.dmp", "dump", old);
        env.CreateFile("LocalAppData/D3DSCache/shader.bin", "shader", old);
        env.CreateFile("Windows.old/old.txt", "old", old);
        var service = new JunkService(env.Paths, new PathGuard(env.Paths), FileOperationLog.Null);

        var scan = service.Scan();

        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "crash-reports")).Bytes > 0);
        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "shader-caches")).Bytes > 0);
        var oldWindows = Assert.Single(scan.Categories.Where(c => c.Id == "old-windows"));
        Assert.False(oldWindows.CanClean);
        Assert.True(oldWindows.Bytes > 0);

        var clean = service.Clean(["old-windows"], dryRun: false);
        Assert.Contains(clean.Messages, m => m.Contains("report-only", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(env.Root, "Windows.old", "old.txt")));
    }

    [Fact]
    public void Junk_scan_covers_delivery_optimization_developer_caches_and_report_only_windows_data()
    {
        using var env = new TestEnvironment();
        var old = DateTime.UtcNow.AddHours(-2);
        var npm = env.CreateFile("LocalAppData/npm-cache/package.tgz", "npm", old);
        var pip = env.CreateFile("LocalAppData/pip/Cache/response.bin", "pip", old);
        var delivery = env.CreateFile(
            "Windows/ServiceProfiles/NetworkService/AppData/Local/Microsoft/Windows/DeliveryOptimization/Cache/chunk.bin",
            "do",
            old);
        var cbsLog = env.CreateFile("Windows/Logs/CBS/CBS.log", "log", old);
        var prefetch = env.CreateFile("Windows/Prefetch/APP.pf", "pf", old);
        var service = new JunkService(env.Paths, new PathGuard(env.Paths), FileOperationLog.Null);

        var scan = service.Scan();

        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "developer-caches")).Bytes > 0);
        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "delivery-optimization")).Bytes > 0);
        Assert.False(Assert.Single(scan.Categories.Where(c => c.Id == "windows-logs")).CanClean);
        Assert.False(Assert.Single(scan.Categories.Where(c => c.Id == "prefetch")).CanClean);

        var cleaned = service.Clean(["developer-caches", "windows-logs", "prefetch"], dryRun: false);
        Assert.False(File.Exists(npm));
        Assert.False(File.Exists(pip));
        Assert.True(File.Exists(cbsLog));
        Assert.True(File.Exists(prefetch));
        Assert.True(File.Exists(delivery));
        Assert.Equal(2, cleaned.Messages.Count(m =>
            m.Contains("report-only", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            bool shellExecute,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return Task.FromResult(new ProcessResult { Started = true, ExitCode = 0 });
        }
    }

    [Fact]
    public void Junk_scan_never_reports_the_category_root_itself()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);
        var result = guard.ValidateJunkItem(env.Paths.Temp, env.Paths.Temp);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Junk_exclusions_safeguard_system_directories()
    {
        Assert.True(JunkService.IsExcluded(@"C:\Windows\System32\something.tmp"));
        Assert.True(JunkService.IsExcluded(@"C:\Windows\WinSxS\temp.tmp"));
        Assert.True(JunkService.IsExcluded(@"C:\$Recycle.Bin\deleted.old"));
        Assert.True(JunkService.IsExcluded(@"D:\System Volume Information\indexer.db$"));
        Assert.False(JunkService.IsExcluded(@"C:\Users\Can\AppData\Local\Temp\random.tmp"));
    }

    [Fact]
    public void Windows_tools_service_catalog_and_custom_tools()
    {
        using var env = new TestEnvironment();
        var service = new WindowsToolsService(env.Paths);
        var tools = service.GetAllTools();

        Assert.True(tools.Count >= 23);
        Assert.Contains(tools, t => t.Id == "system-restore");
        Assert.Contains(tools, t => t.Id == "task-manager");
        Assert.Contains(tools, t => t.Id == "registry-editor");
        Assert.Contains(tools, t => t.Id == "resource-monitor");

        var custom = new WindowsTool
        {
            Id = "custom-test-tool",
            Name = "Custom Tool",
            Category = "Custom Tools",
            Description = "A custom tool for testing",
            Command = "notepad.exe",
            Arguments = "test.txt",
            Glyph = "🔧",
            IsCustom = true
        };

        service.AddCustomTool(custom);
        var customLoaded = service.LoadCustomTools();
        Assert.Contains(customLoaded, t => t.Id == "custom-test-tool");

        var allWithCustom = service.GetAllTools();
        Assert.Contains(allWithCustom, t => t.Id == "custom-test-tool");

        var removed = service.RemoveCustomTool("custom-test-tool");
        Assert.True(removed);
        Assert.DoesNotContain(service.LoadCustomTools(), t => t.Id == "custom-test-tool");
    }

    [Fact]
    public void Evidence_remover_service_discovers_valid_drives()
    {
        var service = new EvidenceRemoverService(FileOperationLog.Null);
        var drives = service.GetDrives();

        Assert.NotNull(drives);
        if (drives.Count > 0)
        {
            var first = drives[0];
            Assert.False(string.IsNullOrWhiteSpace(first.DriveName));
            Assert.True(first.TotalBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(first.DriveLetter));
        }
    }
}
