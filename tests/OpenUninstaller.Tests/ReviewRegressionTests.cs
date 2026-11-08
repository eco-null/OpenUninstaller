using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData(@"C:relative\folder")]
    [InlineData(@"\rooted\without\drive")]
    public void PathGuard_rejects_paths_that_depend_on_the_current_directory(string path)
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);

        Assert.False(guard.ValidateForDeletion(path).Allowed);
    }

    [Fact]
    public void PathGuard_protects_roots_reported_with_short_names()
    {
        // Path.GetTempPath() returns C:\Users\RUNNER~1\... for long user names.
        using var env = new TestEnvironment();
        var temp = Path.GetFullPath(env.CreateDirectory("Long Temporary Folder"));
        var shortTemp = ShortPath(temp);
        if (shortTemp is null || string.Equals(shortTemp, temp, StringComparison.OrdinalIgnoreCase))
        {
            return; // 8.3 names are disabled on this volume; there is no alias to test.
        }

        var guard = new PathGuard(new AppPaths { Temp = shortTemp });

        Assert.True(guard.ValidateForDeletion(Path.Combine(temp, "stale.tmp")).Allowed);
        Assert.False(guard.ValidateForDeletion(temp).Allowed, $"{temp} (root {shortTemp}) was allowed.");
    }

    [Fact]
    public void Resolver_keeps_vendor_arguments_verbatim()
    {
        var program = TestPrograms.Create(
            "Legacy",
            uninstallCommand: "\"C:\\Program Files\\Legacy\\uninst.exe\" \"C:\\Program Files\\Legacy\\setup.dll\",Uninstall /D=\"C:\\Program Files\\Legacy\"");

        var command = UninstallCommandResolver.Resolve(program, preferSilent: false);

        Assert.Equal("C:\\Program Files\\Legacy\\uninst.exe", command.Executable);
        Assert.Equal("\"C:\\Program Files\\Legacy\\setup.dll\",Uninstall /D=\"C:\\Program Files\\Legacy\"", command.Arguments);
    }

    [Fact]
    public void Resolver_finds_bare_system_executables_in_the_system_directory()
    {
        var program = TestPrograms.Create(
            "ClickOnce App",
            uninstallCommand: "rundll32.exe dfshim.dll,ShArpMaintain App.application, Culture=neutral");

        var command = UninstallCommandResolver.Resolve(program, preferSilent: false);

        Assert.True(command.IsUsable, command.Error);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), command.Executable, ignoreCase: true);
        Assert.Equal("dfshim.dll,ShArpMaintain App.application, Culture=neutral", command.Arguments);
    }

    [Fact]
    public async Task Deletion_removes_folders_that_contain_read_only_files()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateDirectory("Fixtures/ReadOnlyApp");
        var file = env.CreateFile("Fixtures/ReadOnlyApp/data/locked.bin", "locked");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var guard = new PathGuard(env.Paths);
        var backup = new BackupService(env.Paths, env.Registry, new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, guard);
        var deletion = new LeftoverDeletionService(env.Registry, guard, backup,
            FileOperationLog.Null, new ProcessRunner(), env.Paths);
        var item = new LeftoverItem
        {
            Id = "folder", Kind = LeftoverKind.Directory, FilePath = folder, DisplayPath = folder,
            Confidence = LeftoverConfidence.Exact, Reason = "Selected folder"
        };

        var result = await deletion.DeleteAsync("ReadOnlyApp", [item], dryRun: false);

        var outcome = Assert.Single(result.Items);
        Assert.Equal(DeletionStatus.Deleted, outcome.Status);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Registry_store_reports_denied_machine_writes_as_unauthorized_access()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return; // Elevated runs may write HKLM; the denial cannot be reproduced.
            }
        }

        var store = new WindowsRegistryStore();
        var location = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE\\Microsoft");

        Assert.Throws<UnauthorizedAccessException>(
            () => store.DeleteValue(location, "OpenUninstaller-test-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Maintenance_service_binaries_are_installed_under_program_files()
    {
        using var env = new TestEnvironment();
        var manager = new BackgroundServiceManager(env.Paths, Path.Combine(env.Paths.AppBaseDirectory, "service", "OpenUninstaller.Service.exe"));

        Assert.True(PathGuard.IsUnder(manager.InstalledDirectory, env.Paths.ProgramFiles));
        Assert.False(PathGuard.IsUnder(manager.InstalledDirectory, env.Paths.AppBaseDirectory));
    }

    [Fact]
    public void Backup_delete_does_not_follow_a_backups_folder_replaced_by_a_junction()
    {
        // The LocalSystem service prunes a tree the user owns; swapping Backups for a junction
        // must not turn pruning into a delete somewhere else.
        using var env = new TestEnvironment();
        var victim = env.CreateFile("Elsewhere/b1/manifest.json", "{}");
        if (Directory.Exists(env.Paths.BackupsDirectory))
        {
            Directory.Delete(env.Paths.BackupsDirectory, recursive: true);
        }

        Directory.CreateDirectory(env.Paths.DataDirectory);
        CreateJunction(env.Paths.BackupsDirectory, Path.Combine(env.Root, "Elsewhere"));
        var backups = new BackupService(env.Paths, env.Registry, new ProcessRunner(),
            new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, new PathGuard(env.Paths));

        Assert.False(backups.Delete("b1"));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task Maintenance_does_not_expire_files_through_a_redirected_log_folder()
    {
        using var env = new TestEnvironment();
        var victim = env.CreateFile("Elsewhere/important.jsonl", "{}");
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-400));
        Directory.CreateDirectory(env.Paths.DataDirectory);
        if (Directory.Exists(env.Paths.LogsDirectory))
        {
            Directory.Delete(env.Paths.LogsDirectory, recursive: true);
        }

        CreateJunction(env.Paths.LogsDirectory, Path.Combine(env.Root, "Elsewhere"));
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var guard = new PathGuard(env.Paths);
        var backups = new BackupService(env.Paths, env.Registry, new ProcessRunner(), clock, FileOperationLog.Null, guard);
        var maintenance = new MaintenanceService(env.Paths, backups,
            new JunkService(env.Paths, guard, FileOperationLog.Null), clock, FileOperationLog.Null);

        await maintenance.RunOnceAsync(new MaintenanceSettings { LogRetentionDays = 1 });

        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void Verified_tree_delete_removes_read_only_files_and_inner_links_without_following_them()
    {
        using var env = new TestEnvironment();
        var victim = env.CreateFile("Outside/keep.txt", "keep");
        var tree = env.CreateDirectory("Tree");
        var locked = env.CreateFile("Tree/nested/locked.bin", "x");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        CreateJunction(Path.Combine(tree, "nested", "link"), Path.Combine(env.Root, "Outside"));

        VerifiedFileSystem.DeleteTree(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void Verified_delete_removes_one_name_of_a_hard_linked_file_and_keeps_the_other()
    {
        using var env = new TestEnvironment();
        var original = env.CreateFile("Shared/original.txt", "data");
        var link = Path.Combine(env.CreateDirectory("Leftover"), "link.txt");
        RunCmd("mklink", "/H", link, original);

        VerifiedFileSystem.DeleteTree(Path.GetDirectoryName(link)!);

        Assert.False(File.Exists(link));
        Assert.Equal("data", File.ReadAllText(original));
    }

    [Fact]
    public void Pinned_directory_cannot_be_renamed_or_have_its_parent_renamed()
    {
        using var env = new TestEnvironment();
        var parent = env.CreateDirectory("Pin/Parent");
        var child = env.CreateDirectory("Pin/Parent/Child");

        using (VerifiedFileSystem.PinDirectory(child))
        {
            Assert.ThrowsAny<Exception>(() => Directory.Move(child, child + "-moved"));
            Assert.ThrowsAny<Exception>(() => Directory.Move(parent, parent + "-moved"));
        }

        Directory.Move(parent, parent + "-moved");
    }

    [Theory]
    [InlineData("        BINARY_PATH_NAME   : \"C:\\Users\\me\\AppData\\Local\\Programs\\OpenUninstaller\\service\\OpenUninstaller.Service.exe\"",
        "C:\\Users\\me\\AppData\\Local\\Programs\\OpenUninstaller\\service\\OpenUninstaller.Service.exe")]
    [InlineData("        BINARY_PATH_NAME   : C:\\Tools\\svc.exe", "C:\\Tools\\svc.exe")]
    [InlineData("        START_TYPE         : 2   AUTO_START", null)]
    public void Service_binary_path_is_read_from_sc_qc_output(string line, string? expected)
    {
        var output = "[SC] QueryServiceConfig SUCCESS\r\n\r\nSERVICE_NAME: OpenUninstallerMaintenance\r\n" + line + "\r\n";

        Assert.Equal(expected, BackgroundServiceManager.ParseBinaryPath(output));
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        RunCmd("mklink", "/J", link, target);
    }

    private static void RunCmd(params string[] arguments)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c" }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static string? ShortPath(string path)
    {
        var length = GetShortPathNameW(path, null, 0);
        if (length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        var written = GetShortPathNameW(path, buffer, length);
        return written == 0 || written >= length ? null : new string(buffer, 0, (int)written);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, char[]? shortPath, uint bufferLength);
}
