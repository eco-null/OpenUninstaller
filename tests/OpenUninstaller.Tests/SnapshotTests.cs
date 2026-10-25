using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void Registry_diff_reports_added_keys_and_values()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey("Snapshot", ("Existing", "one", RegistryValueKind.String));
        var scope = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{env.RegistryBase}\\Snapshot");
        var snapshotter = new RegistrySnapshotter(env.Registry);

        var before = snapshotter.Capture([scope]);
        env.Registry.CreateSubKey(new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{env.RegistryBase}\\Snapshot\\Child"));
        env.Registry.SetValue(
            new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, $"{env.RegistryBase}\\Snapshot"),
            "Added",
            RegistryValueKind.String,
            "two");
        var after = snapshotter.Capture([scope]);

        var diff = RegistrySnapshotter.Diff(before, after);

        Assert.Contains(diff.AddedKeys, key => key.Path.EndsWith("Snapshot\\Child", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            diff.AddedValuesToExistingKeys,
            key => key.Path.EndsWith("Snapshot", StringComparison.OrdinalIgnoreCase) && key.ValueNames.Contains("Added"));
        Assert.False(diff.Truncated);
    }

    [Fact]
    public void Registry_diff_collapses_children_of_added_keys()
    {
        using var env = new TestEnvironment();
        var scope = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{env.RegistryBase}\\Collapse");
        env.Registry.CreateSubKey(scope);
        var before = new RegistrySnapshotter(env.Registry).Capture([scope]);
        env.Registry.CreateSubKey(new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, $"{scope.Path}\\Child\\Grandchild"));
        var after = new RegistrySnapshotter(env.Registry).Capture([scope]);

        var diff = RegistrySnapshotter.Diff(before, after);

        var added = Assert.Single(diff.AddedKeys);
        Assert.EndsWith("Child", added.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_capture_respects_the_entry_cap()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey("Cap", ("Value", "x", RegistryValueKind.String));
        env.AddRegistryKey("Cap\\Child", ("Value", "y", RegistryValueKind.String));
        var scope = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{env.RegistryBase}\\Cap");

        var snapshot = new RegistrySnapshotter(env.Registry).Capture([scope], maxKeys: 1);

        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void File_diff_reports_added_paths_and_collapses_directories()
    {
        using var env = new TestEnvironment();
        var root = env.CreateDirectory("SnapshotFiles");
        var snapshotter = new FileSnapshotter(env.Paths);
        var before = snapshotter.Capture([root]);

        var directory = env.CreateDirectory("SnapshotFiles", "NewFolder");
        env.CreateFile("SnapshotFiles/NewFolder/nested.txt");
        env.CreateFile("SnapshotFiles/single.txt");
        var after = snapshotter.Capture([root]);

        var diff = FileSnapshotter.Diff(before, after);

        Assert.Contains(diff.AddedPaths, e => e.Path.EndsWith("single.txt", StringComparison.OrdinalIgnoreCase));
        var folder = Assert.Single(diff.AddedPaths, e => e.IsDirectory);
        Assert.Equal(directory, folder.Path);
        Assert.DoesNotContain(diff.AddedPaths, e => e.Path.Contains("nested.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void File_snapshot_skips_temp_and_missing_roots()
    {
        using var env = new TestEnvironment();
        env.CreateFile("Temp/ephemeral.tmp");
        env.CreateFile("SnapshotFiles/keep.txt");
        var snapshotter = new FileSnapshotter(env.Paths);

        var snapshot = snapshotter.Capture([env.Paths.Temp, Path.Combine(env.Root, "Missing")]);

        Assert.DoesNotContain(snapshot.Entries.Keys, path => path.EndsWith("ephemeral.tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(snapshot.Entries);
    }
}
