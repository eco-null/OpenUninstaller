using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class GuardTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData(@"\\server\share\folder")]
    public void PathGuard_rejects_invalid_paths(string path)
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);

        Assert.False(guard.ValidateForDeletion(path).Allowed);
    }

    [Fact]
    public void PathGuard_rejects_system_and_profile_roots()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);

        foreach (var path in new[]
                 {
                     env.Root,
                     env.Paths.WindowsDirectory,
                     env.Paths.ProgramFiles,
                     env.Paths.ProgramData,
                     env.Paths.UserProfile,
                     env.Paths.Temp,
                     env.Paths.AppData,
                     env.Paths.LocalAppData
                 })
        {
            var result = guard.ValidateForDeletion(path);
            Assert.False(result.Allowed, $"Expected rejection for {path}");
            Assert.NotNull(result.Reason);
        }
    }

    [Fact]
    public void PathGuard_rejects_own_directories_and_traversal()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);
        var ownFile = Path.Combine(env.Paths.BackupsDirectory, "backup.zip");
        Directory.CreateDirectory(env.Paths.BackupsDirectory);

        Assert.False(guard.ValidateForDeletion(ownFile).Allowed);
        Assert.False(guard.ValidateForDeletion(Path.Combine(env.Root, "Fixtures", "..", "..", "Windows")).Allowed);
    }

    [Fact]
    public void PathGuard_allows_fixture_paths_and_rejects_reparse_points()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);
        var file = env.CreateFile("Fixtures/App/data.txt");

        var result = guard.ValidateForDeletion(file);

        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(Path.GetFullPath(file), result.NormalizedPath);
    }

    [Fact]
    public void Junk_guard_requires_a_child_of_the_category_root()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);
        var root = env.CreateDirectory("Temp", "nested");
        var child = env.CreateFile("Temp/nested/file.tmp");
        var outside = env.CreateFile("Fixtures/outside.tmp");

        Assert.False(guard.ValidateJunkItem(root, root).Allowed);
        Assert.False(guard.ValidateJunkItem(outside, root).Allowed);
        Assert.True(guard.ValidateJunkItem(child, root).Allowed);
    }

    [Theory]
    [InlineData("SOFTWARE", false)]
    [InlineData("SOFTWARE\\Vendor", false)]
    [InlineData("SOFTWARE\\Vendor\\App", true)]
    [InlineData("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false)]
    [InlineData("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{00000000-0000-0000-0000-000000000000}", false)]
    [InlineData("SYSTEM\\CurrentControlSet\\Services", false)]
    [InlineData("Software\\OpenUninstaller\\Data", false)]
    public void RegistryGuard_enforces_protected_prefixes(string path, bool allowed)
    {
        var location = new RegistryLocation(RegistryHive.LocalMachine, RegistryView.Registry64, path);

        var result = RegistryGuard.ValidateForDeletion(location);

        Assert.Equal(allowed, result.Allowed);
    }

    [Fact]
    public void RegistryGuard_allows_uninstall_entry_only_in_forced_mode()
    {
        var location = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Example");

        Assert.False(RegistryGuard.ValidateForDeletion(location).Allowed);
        Assert.True(RegistryGuard.ValidateForDeletion(location, allowUninstallEntry: true).Allowed);
    }

    [Fact]
    public void RegistryGuard_scopes_app_compat_and_service_entries_to_direct_children()
    {
        var appPath = new RegistryLocation(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\widget.exe");
        Assert.False(RegistryGuard.ValidateForDeletion(appPath).Allowed);
        Assert.True(RegistryGuard.ValidateForDeletion(appPath, allowAppCompatEntry: true).Allowed);

        var appPathsContainer = new RegistryLocation(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths");
        Assert.False(RegistryGuard.ValidateForDeletion(appPathsContainer, allowAppCompatEntry: true).Allowed);

        var service = new RegistryLocation(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            "SYSTEM\\CurrentControlSet\\Services\\WidgetHelper");
        Assert.False(RegistryGuard.ValidateForDeletion(service).Allowed);
        Assert.True(RegistryGuard.ValidateForDeletion(service, allowServiceEntry: true).Allowed);

        var nestedServiceKey = new RegistryLocation(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            "SYSTEM\\CurrentControlSet\\Services\\WidgetHelper\\Parameters");
        Assert.False(RegistryGuard.ValidateForDeletion(nestedServiceKey, allowServiceEntry: true).Allowed);
    }

    [Fact]
    public void ScheduledTaskGuard_rejects_escapes_names_and_outside_files()
    {
        using var env = new TestEnvironment();
        var taskFile = env.CreateFile("Tasks/Widget/Update", "<Task />");
        var outside = env.CreateFile("Fixtures/evil.xml", "<Task />");

        Assert.True(ScheduledTaskGuard.ValidateForDeletion("Widget\\Update", env.Paths.ScheduledTasksDirectory, taskFile).Allowed);
        Assert.False(ScheduledTaskGuard.ValidateForDeletion("..\\evil", env.Paths.ScheduledTasksDirectory, outside).Allowed);
        Assert.False(ScheduledTaskGuard.ValidateForDeletion("Widget\\Update", env.Paths.ScheduledTasksDirectory, outside).Allowed);
        Assert.False(ScheduledTaskGuard.ValidateTaskName("Widget\\..\\Windows", out _));
        Assert.False(ScheduledTaskGuard.ValidateTaskName("Widget\nUpdate", out _));
    }

    [Fact]
    public void RegistryGuard_rejects_other_hives()
    {
        var location = new RegistryLocation(RegistryHive.ClassesRoot, RegistryView.Registry64, "SOFTWARE\\Vendor\\App");
        Assert.False(RegistryGuard.ValidateForDeletion(location).Allowed);
    }
}
