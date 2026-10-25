using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class BackupRestoreTests
{
    [Fact]
    public async Task Backup_and_restore_round_trip_registry_and_files()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("BackupTarget\\App", ("Marker", "original", RegistryValueKind.String));
        var folder = env.CreateDirectory("Fixtures", "Backup App");
        var file = env.CreateFile("Fixtures/Backup App/data.txt", "payload");

        var guard = new PathGuard(env.Paths);
        var service = new BackupService(
            env.Paths,
            env.Registry,
            new ProcessRunner(),
            new FixedClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            FileOperationLog.Null,
            guard);

        var items = new List<LeftoverItem>
        {
            new()
            {
                Id = "registry-1",
                Kind = LeftoverKind.RegistryKey,
                DisplayPath = location.DisplayPath,
                Confidence = LeftoverConfidence.Exact,
                Reason = "test",
                RegistryLocation = location,
                IsSelected = true
            },
            new()
            {
                Id = "file-1",
                Kind = LeftoverKind.Directory,
                DisplayPath = folder,
                Confidence = LeftoverConfidence.Exact,
                Reason = "test",
                FilePath = folder,
                IsSelected = true
            }
        };

        var backup = await service.CreateAsync("Backup App", items);

        Assert.True(backup.Succeeded, string.Join(Environment.NewLine, backup.Warnings));
        Assert.Equal(2, backup.Manifest.Registry.Count + backup.Manifest.Files.Count);
        Assert.True(File.Exists(Path.Combine(backup.Directory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(backup.Directory, "files.zip")));
        Assert.Single(service.List());

        env.Registry.DeleteKey(location, recursive: true);
        Directory.Delete(folder, recursive: true);
        Assert.False(env.Registry.KeyExists(location));
        Assert.False(Directory.Exists(folder));

        var restore = await service.RestoreAsync(backup.BackupId, overwrite: true);

        Assert.True(restore.Succeeded, string.Join(Environment.NewLine, restore.Entries.Select(e => $"{e.Status}: {e.Detail}").Concat(restore.Warnings)));
        Assert.True(env.Registry.KeyExists(location));
        Assert.True(File.Exists(file));
        Assert.Equal("payload", File.ReadAllText(file));
    }

    [Fact]
    public async Task Restore_skips_existing_files_without_overwrite()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Overwrite/data.txt", "first");
        var guard = new PathGuard(env.Paths);
        var service = new BackupService(
            env.Paths,
            env.Registry,
            new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow),
            FileOperationLog.Null,
            guard);
        var item = new LeftoverItem
        {
            Id = "file-1",
            Kind = LeftoverKind.File,
            DisplayPath = file,
            Confidence = LeftoverConfidence.Exact,
            Reason = "test",
            FilePath = file,
            IsSelected = true
        };

        var backup = await service.CreateAsync("Overwrite App", [item]);
        File.WriteAllText(file, "second");

        var restore = await service.RestoreAsync(backup.BackupId, overwrite: false);

        Assert.False(restore.Succeeded);
        Assert.Contains(restore.Entries, entry => entry.Status == RestoreEntryStatus.Skipped);
        Assert.Equal("second", File.ReadAllText(file));
    }

    [Fact]
    public async Task Prune_keeps_the_newest_backups()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);
        var service = new BackupService(
            env.Paths,
            env.Registry,
            new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow),
            FileOperationLog.Null,
            guard);

        for (var i = 0; i < 3; i++)
        {
            var file = env.CreateFile($"Fixtures/Prune/{i}.txt", i.ToString());
            var item = new LeftoverItem
            {
                Id = $"file-{i}",
                Kind = LeftoverKind.File,
                DisplayPath = file,
                Confidence = LeftoverConfidence.Exact,
                Reason = "test",
                FilePath = file,
                IsSelected = true
            };
            var result = await service.CreateAsync("Prune App", [item]);
            Assert.True(result.Succeeded);
        }

        var removed = service.Prune(2);

        Assert.Equal(1, removed);
        Assert.Equal(2, service.List().Count);
    }
}
