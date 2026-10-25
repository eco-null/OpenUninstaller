using System.IO;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class PrivacyAndShredTests
{
    [Fact]
    public void Shred_overwrites_and_removes_a_file()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/secret.txt", "top secret payload");
        var shredder = new Shredder(new PathGuard(env.Paths), FileOperationLog.Null);

        var plan = shredder.Plan([file]);
        Assert.Single(plan.Targets);
        Assert.Equal(18, plan.TotalBytes);

        var result = shredder.Shred([file], passes: 3, dryRun: false);

        Assert.True(result.Succeeded, string.Join("; ", result.Failed.Concat(result.Rejected)));
        Assert.Equal(1, result.ShreddedFiles);
        Assert.Equal(18, result.ShreddedBytes);
        Assert.Equal(3, result.Passes);
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public void Shred_dry_run_never_writes_or_deletes()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/keep.txt", "unchanged");
        var shredder = new Shredder(new PathGuard(env.Paths), FileOperationLog.Null);

        var result = shredder.Shred([file], passes: 7, dryRun: true);

        Assert.True(result.Succeeded);
        Assert.True(result.DryRun);
        Assert.Equal(1, result.ShreddedFiles);
        Assert.Equal("unchanged", File.ReadAllText(file));
    }

    [Fact]
    public void Shred_refuses_protected_roots_and_missing_paths()
    {
        using var env = new TestEnvironment();
        var shredder = new Shredder(new PathGuard(env.Paths), FileOperationLog.Null);

        var result = shredder.Shred(
            [env.Paths.UserProfile, Path.Combine(env.Root, "does-not-exist.bin"), env.Paths.DataDirectory],
            passes: 3,
            dryRun: false);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Rejected.Count);
        Assert.Equal(0, result.ShreddedFiles);
        Assert.True(Directory.Exists(env.Paths.UserProfile));
    }

    [Fact]
    public void Shred_removes_a_tree_and_read_only_files()
    {
        using var env = new TestEnvironment();
        var root = env.CreateDirectory("Fixtures", "tree");
        var nested = env.CreateFile("Fixtures/tree/nested/a.bin", "aaaa");
        var locked = env.CreateFile("Fixtures/tree/b.bin", "bbbb");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        var shredder = new Shredder(new PathGuard(env.Paths), FileOperationLog.Null);

        var result = shredder.Shred([root], passes: 2, dryRun: false);

        Assert.True(result.Succeeded, string.Join("; ", result.Failed));
        Assert.Equal(2, result.ShreddedFiles);
        Assert.Equal(2, result.DeletedDirectories);
        Assert.False(File.Exists(nested));
        Assert.False(File.Exists(locked));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Privacy_scan_reports_history_categories_and_clean_respects_allow_list()
    {
        using var env = new TestEnvironment();
        var explorer = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{env.RegistryBase}\\Explorer");
        var runMru = new RegistryLocation(explorer.Hive, explorer.View, $"{explorer.Path}\\RunMRU");
        env.Registry.CreateSubKey(runMru);
        env.Registry.SetValue(runMru, "MRUList", RegistryValueKind.String, "ba");
        env.Registry.SetValue(runMru, "a", RegistryValueKind.String, "notepad");
        env.Registry.SetValue(runMru, "b", RegistryValueKind.String, "calc");

        var recentDocs = new RegistryLocation(explorer.Hive, explorer.View, $"{explorer.Path}\\RecentDocs");
        env.Registry.CreateSubKey(recentDocs);
        env.Registry.SetValue(recentDocs, "MRUListEx", RegistryValueKind.Binary, new byte[] { 0, 0, 0, 0 });
        var recentSubKey = new RegistryLocation(recentDocs.Hive, recentDocs.View, $"{recentDocs.Path}\\0");
        env.Registry.CreateSubKey(recentSubKey);
        env.Registry.SetValue(recentSubKey, "0", RegistryValueKind.String, "file.txt");

        var recentItem = env.CreateFile("Roaming/Microsoft/Windows/Recent/secret.lnk", "shortcut");
        var unrelated = env.CreateFile("Roaming/Unrelated/keep.txt", "keep");
        var thumbnail = env.CreateFile(
            "LocalAppData/Microsoft/Windows/Explorer/thumbcache_32.db",
            "thumb");
        var icon = env.CreateFile(
            "LocalAppData/Microsoft/Windows/Explorer/iconcache_16.db",
            "icon");
        var snapshot = env.CreateFile("ProgramData/OpenUninstaller/ScanHistory/app.json", "{}");
        var log = env.CreateFile("ProgramData/OpenUninstaller/Logs/op-test.jsonl", "{}");
        var ignored = env.CreateFile("ProgramData/OpenUninstaller/ignored-leftovers.json", "{}");

        var service = new PrivacyService(env.Paths, env.Registry, FileOperationLog.Null, explorer);
        var scan = service.Scan();

        Assert.Equal(3, Assert.Single(scan.Categories.Where(c => c.Id == "run-history")).ItemCount);
        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "recent-docs")).ItemCount >= 2);
        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "recent-items")).ItemCount >= 1);
        Assert.Equal(2, Assert.Single(scan.Categories.Where(c => c.Id == "thumbnail-cache")).ItemCount);
        Assert.True(Assert.Single(scan.Categories.Where(c => c.Id == "app-history")).ItemCount >= 3);

        var runClean = service.Clean(["run-history"], dryRun: false);
        Assert.True(runClean.Succeeded);
        Assert.Equal(3, runClean.ItemsDeleted);
        Assert.Empty(env.Registry.GetValueNames(runMru));
        Assert.True(env.Registry.KeyExists(runMru));

        var docsClean = service.Clean(["recent-docs"], dryRun: false);
        Assert.True(docsClean.Succeeded);
        Assert.Empty(env.Registry.GetValueNames(recentDocs));
        Assert.False(env.Registry.KeyExists(recentSubKey));

        var filesClean = service.Clean(["recent-items", "thumbnail-cache", "app-history"], dryRun: false);
        Assert.True(filesClean.Succeeded);
        Assert.False(File.Exists(recentItem));
        Assert.False(File.Exists(thumbnail));
        Assert.False(File.Exists(icon));
        Assert.False(File.Exists(snapshot));
        Assert.False(File.Exists(log));
        Assert.False(File.Exists(ignored));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Privacy_dry_run_deletes_nothing()
    {
        using var env = new TestEnvironment();
        var explorer = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{env.RegistryBase}\\Explorer");
        var runMru = new RegistryLocation(explorer.Hive, explorer.View, $"{explorer.Path}\\RunMRU");
        env.Registry.CreateSubKey(runMru);
        env.Registry.SetValue(runMru, "a", RegistryValueKind.String, "notepad");
        var file = env.CreateFile("Roaming/Microsoft/Windows/Recent/x.lnk", "x");
        var service = new PrivacyService(env.Paths, env.Registry, FileOperationLog.Null, explorer);

        var result = service.Clean(["run-history", "recent-items"], dryRun: true);

        Assert.True(result.Succeeded);
        Assert.True(result.DryRun);
        Assert.True(result.ItemsDeleted >= 2);
        Assert.NotNull(env.Registry.GetValue(runMru, "a"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Maintenance_expires_old_logs_and_scan_history()
    {
        using var env = new TestEnvironment();
        var oldLog = env.CreateFile("ProgramData/OpenUninstaller/Logs/op-old.jsonl", "{}");
        var freshLog = env.CreateFile("ProgramData/OpenUninstaller/Logs/op-new.jsonl", "{}");
        var oldSnapshot = env.CreateFile("ProgramData/OpenUninstaller/ScanHistory/old.json", "{}");
        var freshSnapshot = env.CreateFile("ProgramData/OpenUninstaller/ScanHistory/new.json", "{}");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(oldSnapshot, DateTime.UtcNow.AddDays(-200));

        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var guard = new PathGuard(env.Paths);
        var backups = new BackupService(
            env.Paths, env.Registry, new NoopRunner(), clock, FileOperationLog.Null, guard);
        var junk = new JunkService(env.Paths, guard, FileOperationLog.Null);
        var maintenance = new MaintenanceService(
            env.Paths, backups, junk, clock, FileOperationLog.Null);

        var result = await maintenance.RunOnceAsync(new MaintenanceSettings
        {
            KeepBackups = 20,
            LogRetentionDays = 30,
            ScanHistoryRetentionDays = 90
        });

        Assert.True(result.Succeeded, string.Join("; ", result.Warnings));
        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldSnapshot));
        Assert.True(File.Exists(freshLog));
        Assert.True(File.Exists(freshSnapshot));
        Assert.Contains(result.Steps, step => step.StartsWith("Backups:", StringComparison.Ordinal));
    }

    [Fact]
    public void Maintenance_settings_clamp_values()
    {
        var settings = new MaintenanceSettings
        {
            IntervalMinutes = 1,
            KeepBackups = 0,
            LogRetentionDays = -5,
            ScanHistoryRetentionDays = 999_999,
            JunkCategories = [" User-Temp ", "user-temp", ""]
        };

        settings.Normalize();

        Assert.Equal(15, settings.IntervalMinutes);
        Assert.Equal(1, settings.KeepBackups);
        Assert.Equal(1, settings.LogRetentionDays);
        Assert.Equal(3650, settings.ScanHistoryRetentionDays);
        Assert.Equal(new[] { "user-temp" }, settings.JunkCategories);
    }

    [Fact]
    public void Background_service_query_reports_a_missing_service_without_elevation()
    {
        using var env = new TestEnvironment();
        var manager = new BackgroundServiceManager(
            env.Paths,
            Path.Combine(env.Root, "OpenUninstaller.Service.exe"),
            "OUTest" + Guid.NewGuid().ToString("N")[..12]);

        var status = manager.Query();

        Assert.False(status.Installed);
        Assert.False(status.Running);
        Assert.False(status.ExecutableExists);
        Assert.DoesNotContain("exception", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            bool shellExecute,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult { Started = true, ExitCode = 0 });
    }
}
