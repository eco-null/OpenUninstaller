using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class CleanupSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guards_reject_paths_through_ancestor_junctions(bool missingTarget)
    {
        using var env = new TestEnvironment();
        var protectedFile = env.CreateFile("Windows/Protected/data.txt", "protected");
        var fixtures = env.CreateDirectory("Fixtures");
        var link = Path.Combine(fixtures, "alias");
        await CreateJunctionAsync(link, env.Paths.WindowsDirectory);
        try
        {
            var path = Path.Combine(link, "Protected", missingTarget ? "missing.txt" : "data.txt");
            var guard = new PathGuard(env.Paths);

            Assert.False(guard.ValidateForDeletion(path, recursive: false).Allowed);
            Assert.False(guard.ValidateForDeletion(path, recursive: true).Allowed);
            Assert.False(guard.ValidateJunkItem(path, fixtures).Allowed);
            Assert.Equal("protected", File.ReadAllText(protectedFile));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_cleanup_does_not_expand_to_parent_siblings_or_name_matches(bool directory)
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Shared/PortableTool.exe", "executable");
        var sibling = env.CreateFile("Fixtures/Shared/unrelated.txt", "keep");
        env.CreateFile("Roaming/PortableTool/settings.txt", "unrelated name match");
        var target = directory ? env.CreateDirectory("Fixtures", "SelectedFolder") : file;
        var program = HunterService.CreateCleanupTarget(target);
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

        var scan = await scanner.ScanAsync(program);

        Assert.Null(program.InstallLocation);
        var item = Assert.Single(scan.Items);
        Assert.Equal(target, item.FilePath);
        Assert.Equal(directory ? LeftoverKind.Directory : LeftoverKind.File, item.Kind);
        Assert.True(item.IsSelected);
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public async Task Explicit_protected_target_is_not_offered_for_cleanup()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Windows/Protected.exe", "protected");
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

        var scan = await scanner.ScanAsync(HunterService.CreateCleanupTarget(file));

        Assert.Empty(scan.Items);
        Assert.NotEmpty(scan.Warnings);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Explicit_file_cleanup_backs_up_only_selected_file_and_can_restore_it()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Shared/PortableTool.exe", "executable");
        var sibling = env.CreateFile("Fixtures/Shared/unrelated.txt", "keep");
        var guard = new PathGuard(env.Paths);
        var backup = new BackupService(env.Paths, env.Registry, new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, guard);
        var deletion = new LeftoverDeletionService(env.Registry, guard, backup,
            FileOperationLog.Null, new ProcessRunner(), env.Paths);
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, guard);
        var scan = await scanner.ScanAsync(HunterService.CreateCleanupTarget(file));

        var result = await deletion.DeleteAsync("PortableTool", scan.Items, dryRun: false);

        Assert.Equal(DeletionStatus.Deleted, Assert.Single(result.Items).Status);
        Assert.NotNull(result.BackupId);
        Assert.False(File.Exists(file));
        Assert.Equal("keep", File.ReadAllText(sibling));
        Assert.Equal(file, Assert.Single(backup.Get(result.BackupId!)!.Manifest.Files).OriginalPath);
        var restore = await backup.RestoreAsync(result.BackupId!, overwrite: false);
        Assert.True(restore.Succeeded, string.Join("; ", restore.Entries.Select(e => e.Detail).Concat(restore.Warnings)));
        Assert.Equal("executable", File.ReadAllText(file));
    }

    [Fact]
    public async Task Executable_cleanup_keeps_file_when_backup_cannot_be_created()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/PortableTool.exe", "executable");
        Directory.CreateDirectory(env.Paths.DataDirectory);
        File.WriteAllText(env.Paths.BackupsDirectory, "block backup directory creation");
        var guard = new PathGuard(env.Paths);
        var backup = new BackupService(env.Paths, env.Registry, new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, guard);
        var deletion = new LeftoverDeletionService(env.Registry, guard, backup,
            FileOperationLog.Null, new ProcessRunner(), env.Paths);
        var item = new LeftoverItem
        {
            Id = "executable", Kind = LeftoverKind.File, FilePath = file, DisplayPath = file,
            Confidence = LeftoverConfidence.Exact, Reason = "Selected executable"
        };

        await Assert.ThrowsAsync<IOException>(() => deletion.DeleteAsync("PortableTool", [item], dryRun: false));
        Assert.Equal("executable", File.ReadAllText(file));
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
