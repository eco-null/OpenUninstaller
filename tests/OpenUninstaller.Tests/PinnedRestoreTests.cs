using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class PinnedRestoreTests
{
    private static BackupService Service(TestEnvironment env) => new(env.Paths, env.Registry, new ProcessRunner(),
        new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, new PathGuard(env.Paths));
    private static LeftoverItem Item(string path) => new()
    {
        Id = "item", Kind = LeftoverKind.File, DisplayPath = path, FilePath = path,
        Confidence = LeftoverConfidence.Exact, Reason = "test"
    };

    [Fact]
    public async Task Missing_parents_are_created_and_overwrite_works()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Missing/Parent/data.txt", "backup");
        var expectedTime = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(file, expectedTime);
        var service = Service(env);
        var backup = await service.CreateAsync("missing", [Item(file)]);
        Directory.Delete(Path.Combine(env.Root, "Fixtures", "Missing"), true);
        var restored = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(restored.Succeeded, string.Join("; ", restored.Entries.Select(e => e.Detail)));
        Assert.Equal("backup", File.ReadAllText(file));
        Assert.InRange((File.GetLastWriteTimeUtc(file) - expectedTime).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(3));
        File.WriteAllText(file, "existing");
        Assert.False((await service.RestoreAsync(backup.BackupId, false)).Succeeded);
        Assert.Equal("existing", File.ReadAllText(file));
        Assert.True((await service.RestoreAsync(backup.BackupId, true)).Succeeded);
        Assert.Equal("backup", File.ReadAllText(file));
    }

    [Fact]
    public async Task No_overwrite_directory_does_not_merge_a_directory_created_after_guard()
    {
        using var env = new TestEnvironment();
        var dir = env.CreateDirectory("Fixtures", "NoOverwrite");
        var file = env.CreateFile("Fixtures/NoOverwrite/data.txt", "backup");
        var item = new LeftoverItem { Id = "item", Kind = LeftoverKind.Directory, DisplayPath = dir, FilePath = dir, Confidence = LeftoverConfidence.Exact, Reason = "test" };
        var service = Service(env);
        var backup = await service.CreateAsync("directory", [item]);
        Directory.Delete(dir, true);
        var attempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-pin") return;
            attempted = true;
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, "new");
        };
        var result = await service.RestoreAsync(backup.BackupId, false);
        Assert.True(attempted);
        Assert.False(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(file));
    }

    [Fact]
    public async Task Independent_restores_can_run_in_parallel_under_a_shared_temp_ancestor()
    {
        const int count = 8;
        var environments = new List<TestEnvironment>();
        using var entered = new CountdownEvent(count);
        using var release = new ManualResetEventSlim();
        try
        {
            var work = new List<(BackupService Service, string Id, string File)>();
            for (var i = 0; i < count; i++)
            {
                var env = new TestEnvironment();
                environments.Add(env);
                var file = env.CreateFile("Fixtures/Parallel/data.txt", "backup");
                var service = Service(env);
                var backup = await service.CreateAsync("parallel", [Item(file)]);
                File.Delete(file);
                service.RestoreTestHook = (stage, _) =>
                {
                    if (stage != "before-pin") return;
                    entered.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(30))) throw new IOException("Parallel gate timed out.");
                };
                work.Add((service, backup.BackupId, file));
            }
            var tasks = work.Select(item => Task.Factory.StartNew(
                () => item.Service.RestoreAsync(item.Id, true), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
            try { Assert.True(entered.Wait(TimeSpan.FromSeconds(20)), "Not all restores reached the gate."); }
            finally { release.Set(); }
            var results = await Task.WhenAll(tasks);
            Assert.All(results, result => Assert.True(result.Succeeded,
                string.Join("; ", result.Entries.Select(e => e.Detail).Concat(result.Warnings))));
            Assert.All(work, item => Assert.Equal("backup", File.ReadAllText(item.File)));
        }
        finally
        {
            release.Set();
            foreach (var env in environments) env.Dispose();
        }
    }

    [Fact]
    public async Task Unrelated_ancestor_writer_does_not_block_restore()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Ancestor/App/data.txt", "backup");
        var ancestor = Path.Combine(env.Root, "Fixtures");
        var service = Service(env);
        var backup = await service.CreateAsync("ancestor", [Item(file)]);
        File.Delete(file);
        using var writer = CreateFileW(ancestor, 0x102, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(writer.IsInvalid);
        var outside = env.CreateDirectory("Windows", "Outside");
        var attempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-file-open") return;
            attempted = true;
            Assert.False(SetJunction(writer, outside));
        };
        var restore = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(restore.Succeeded, string.Join("; ", restore.Entries.Select(e => e.Detail).Concat(restore.Warnings)));
        Assert.True(attempted);
        Assert.Equal("backup", File.ReadAllText(file));
    }

    [Fact]
    public async Task Ancestor_swapped_after_guard_is_refused()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Swap/data.txt", "backup");
        var parent = Path.GetDirectoryName(file)!;
        var moved = parent + "-moved";
        var outside = env.CreateDirectory("Windows", "Outside");
        var marker = Path.Combine(outside, "data.txt");
        File.WriteAllText(marker, "outside");
        var service = Service(env);
        var backup = await service.CreateAsync("swap", [Item(file)]);
        var attempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-pin") return;
            attempted = true;
            Directory.Move(parent, moved);
            MakeJunction(parent, outside);
        };
        try
        {
            var result = await service.RestoreAsync(backup.BackupId, true);
            Assert.True(attempted);
            Assert.False(result.Succeeded);
            Assert.Equal("outside", File.ReadAllText(marker));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent); Directory.Move(moved, parent); }
    }

    [Fact]
    public async Task Open_file_and_ancestor_cannot_be_renamed_before_write()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Pin/data.txt", "backup");
        var parent = Path.GetDirectoryName(file)!;
        var service = Service(env);
        var backup = await service.CreateAsync("pin", [Item(file)]);
        File.WriteAllText(file, "changed");
        var parentAttempted = false;
        var fileAttempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage == "before-file-open")
            {
                parentAttempted = true;
                Assert.ThrowsAny<IOException>(() => Directory.Move(parent, parent + "-moved"));
            }
            if (stage == "after-file-open")
            {
                fileAttempted = true;
                Assert.ThrowsAny<IOException>(() => File.Move(file, file + ".moved"));
            }
        };
        Assert.True((await service.RestoreAsync(backup.BackupId, true)).Succeeded);
        Assert.True(parentAttempted && fileAttempted);
        Assert.Equal("backup", File.ReadAllText(file));
    }

    [Fact]
    public async Task Leaf_replaced_with_hardlink_before_open_is_not_written()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Hardlink/data.txt", "backup");
        var outside = env.CreateFile("Windows/Outside/data.txt", "outside");
        var moved = file + ".old";
        var service = Service(env);
        var backup = await service.CreateAsync("hardlink", [Item(file)]);
        var attempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-file-open") return;
            attempted = true;
            File.Move(file, moved);
            Assert.True(CreateHardLinkW(file, outside, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);
        };
        try
        {
            var result = await service.RestoreAsync(backup.BackupId, true);
            Assert.True(attempted, string.Join("; ", result.Entries.Select(e => e.Detail)));
            Assert.False(result.Succeeded);
            Assert.Equal("outside", File.ReadAllText(outside));
        }
        finally { if (File.Exists(moved)) { File.Delete(file); File.Move(moved, file); } }
    }

    [Fact]
    public async Task Preexisting_attribute_only_writer_blocks_restore_before_junction_conversion()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Metadata/data.txt", "backup");
        var parent = Path.GetDirectoryName(file)!;
        var outside = env.CreateDirectory("Windows", "Outside");
        var marker = Path.Combine(outside, "data.txt");
        File.WriteAllText(marker, "outside");
        var service = Service(env);
        var backup = await service.CreateAsync("metadata", [Item(file)]);
        File.Delete(file);
        SafeFileHandle? attacker = null;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-pin") return;
            attacker = CreateFileW(parent, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.False(attacker.IsInvalid);
        };
        try
        {
            var restore = await service.RestoreAsync(backup.BackupId, true);
            Assert.NotNull(attacker);
            Assert.False(restore.Succeeded);
            // Prove this really is a usable reparse writer, rather than an inert competing handle.
            Assert.True(SetJunction(attacker!, outside), new Win32Exception(Marshal.GetLastWin32Error()).Message);
            Assert.Equal("outside", File.ReadAllText(marker));
        }
        finally
        {
            attacker?.Dispose();
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) Directory.Delete(parent);
        }
    }

    [Fact]
    public async Task Attribute_only_reparse_attempt_after_parent_lock_is_blocked()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Guard/data.txt", "backup");
        var parent = Path.GetDirectoryName(file)!;
        var outside = env.CreateDirectory("Windows", "Outside");
        var marker = Path.Combine(outside, "data.txt");
        File.WriteAllText(marker, "outside");
        var service = Service(env);
        var backup = await service.CreateAsync("guard", [Item(file)]);
        File.Delete(file);
        var attempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-file-open") return;
            attempted = true;
            using var attacker = CreateFileW(parent, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (!attacker.IsInvalid) Assert.False(SetJunction(attacker, outside));
        };
        var result = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(attempted);
        Assert.True(result.Succeeded, string.Join("; ", result.Entries.Select(e => e.Detail)));
        Assert.Equal("outside", File.ReadAllText(marker));
        Assert.Equal("backup", File.ReadAllText(file));
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_created_parents_and_releases_backup_lock()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Created/Parent/data.txt", "backup");
        var service = Service(env);
        var backup = await service.CreateAsync("rollback", [Item(file)]);
        var created = Path.Combine(env.Root, "Fixtures", "Created");
        Directory.Delete(created, true);
        var attempted = false;
        service.RestoreTestHook = (stage, _) =>
        {
            if (stage != "before-commit") return;
            attempted = true;
            throw new IOException("injected failure");
        };
        var result = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(attempted);
        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(created));
        Assert.True(service.Delete(backup.BackupId));
    }

    [Fact]
    public async Task Cancellation_before_commit_rolls_back_and_releases_handles_and_backup_lock()
    {
        using var env = new TestEnvironment();
        var file = env.CreateFile("Fixtures/Cancel/data.txt", "backup");
        var service = Service(env);
        var backup = await service.CreateAsync("cancel", [Item(file)]);
        File.WriteAllText(file, "original");
        using var cancel = new CancellationTokenSource();
        var attempted = false;
        service.RestoreTestHook = (stage, _) => { if (stage == "before-commit") { attempted = true; cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RestoreAsync(backup.BackupId, true, cancel.Token));
        Assert.True(attempted);
        Assert.Equal("original", File.ReadAllText(file));
        File.Move(file, file + ".moved");
        Assert.True(service.Delete(backup.BackupId));
    }

    private static void MakeJunction(string path, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(path); info.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
    private static bool SetJunction(SafeFileHandle handle, string target)
    {
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = System.Text.Encoding.Unicode.GetBytes(target);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(0xA0000003u);
        writer.Write((ushort)(8 + substitute.Length + 2 + print.Length + 2)); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)substitute.Length);
        writer.Write((ushort)(substitute.Length + 2)); writer.Write((ushort)print.Length);
        writer.Write(substitute); writer.Write((ushort)0); writer.Write(print); writer.Write((ushort)0);
        var bytes = memory.ToArray();
        return DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
