using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class SafetyRegressionTests
{
    private static BackupService Service(TestEnvironment env, IProcessRunner? runner = null) =>
        new(env.Paths, env.Registry, runner ?? new ProcessRunner(), new FixedClock(DateTimeOffset.UtcNow), FileOperationLog.Null, new PathGuard(env.Paths));
    private static LeftoverItem FileItem(string path, bool directory = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = directory ? LeftoverKind.Directory : LeftoverKind.File,
        DisplayPath = path, FilePath = path, Confidence = LeftoverConfidence.Exact, Reason = "test"
    };
    private static LeftoverItem RegItem(RegistryLocation location, LeftoverKind kind, string? value = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = kind, DisplayPath = location.DisplayPath,
        RegistryLocation = location, ValueName = value, Confidence = LeftoverConfidence.Exact, Reason = "test"
    };

    [Fact]
    public async Task Empty_directories_roundtrip_and_junctions_are_never_captured()
    {
        using var env = new TestEnvironment();
        var root = env.CreateDirectory("Fixtures", "Tree");
        var empty = env.CreateDirectory("Fixtures", "Tree", "Empty", "Leaf");
        var service = Service(env);
        var backup = await service.CreateAsync("tree", [FileItem(root, true)]);
        Assert.True(backup.Succeeded);
        Directory.Delete(root, true);
        Assert.True((await service.RestoreAsync(backup.BackupId, true)).Succeeded);
        Assert.True(Directory.Exists(empty));

        var outside = env.CreateDirectory("Fixtures", "Outside");
        var link = Path.Combine(root, "Link");
        MakeJunction(link, outside);
        try
        {
            var item = FileItem(root, true);
            var deletion = new LeftoverDeletionService(env.Registry, new PathGuard(env.Paths), service, FileOperationLog.Null, new ProcessRunner(), env.Paths);
            var result = await deletion.DeleteAsync("linked", [item], false);
            Assert.Equal(0, result.DeletedCount);
            Assert.True(Directory.Exists(root));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Completely_empty_root_can_be_previewed_and_restored()
    {
        using var env = new TestEnvironment();
        var root = env.CreateDirectory("Fixtures", "EmptyRoot");
        var service = Service(env);
        var backup = await service.CreateAsync("empty", [FileItem(root, true)]);
        Assert.True(backup.Succeeded);
        Directory.Delete(root);
        Assert.True(service.PreviewRestore(backup.BackupId, true).CanRestore);
        var restore = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(restore.Succeeded, string.Join("; ", restore.Entries.Select(e => e.Detail).Concat(restore.Warnings)));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task Legacy_32_bit_physical_export_is_restored_to_the_logical_view()
    {
        using var env = new TestEnvironment();
        var location = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry32, env.RegistryBase + "\\Legacy");
        env.Registry.CreateSubKey(location);
        env.Registry.SetValue(location, "Marker", RegistryValueKind.String, "saved");
        var service = Service(env);
        var backup = await service.CreateAsync("legacy", [RegItem(location, LeftoverKind.RegistryKey)]);
        Assert.True(backup.Succeeded, string.Join("; ", backup.Warnings));
        var entry = Assert.Single(backup.Manifest.Registry);
        var path = Path.Combine(backup.Directory, entry.ExportFile.Replace('/', Path.DirectorySeparatorChar));
        var data = File.ReadAllText(path).Replace("HKEY_CURRENT_USER\\" + location.Path,
            "HKEY_CURRENT_USER\\" + location.RegExePath[(location.RegExePath.IndexOf('\\') + 1)..], StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, data, Encoding.Unicode);
        var manifest = backup.Manifest with { Registry = [entry with { Sha256 = Hashing.Sha256File(path) }] };
        File.WriteAllText(Path.Combine(backup.Directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        env.Registry.DeleteKey(location, true);
        Assert.True(service.PreviewRestore(backup.BackupId, true).CanRestore);
        var restore = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(restore.Succeeded, string.Join("; ", restore.Entries.Select(e => e.Detail)));
        Assert.Equal("saved", env.Registry.GetValue(location, "Marker")?.AsString());
        env.Registry.DeleteKey(location, true);
    }

    [Fact]
    public async Task Invalid_registry_export_never_marks_item_captured()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("BadExport", ("Marker", "saved", RegistryValueKind.String));
        var item = RegItem(location, LeftoverKind.RegistryKey);
        var backup = await Service(env, new InvalidExportRunner()).CreateAsync("bad", [item]);
        Assert.False(backup.Succeeded);
        Assert.DoesNotContain(item.Id, backup.CapturedItemIds);
        Assert.True(env.Registry.KeyExists(location));
    }

    [Fact]
    public async Task Restore_preview_and_copy_block_destination_junctions()
    {
        using var env = new TestEnvironment();
        var root = env.CreateDirectory("Fixtures", "Source");
        var child = env.CreateDirectory("Fixtures", "Source", "Child");
        File.WriteAllText(Path.Combine(child, "data.txt"), "backup");
        var service = Service(env);
        var backup = await service.CreateAsync("junction", [FileItem(root, true)]);
        Directory.Delete(child, true);
        var outside = env.CreateDirectory("Windows", "Fixture");
        var marker = Path.Combine(outside, "data.txt");
        File.WriteAllText(marker, "outside");
        MakeJunction(child, outside);
        try
        {
            Assert.False(service.PreviewRestore(backup.BackupId, true).CanRestore);
            Assert.False((await service.RestoreAsync(backup.BackupId, true)).Succeeded);
            Assert.Equal("outside", File.ReadAllText(marker));
        }
        finally { Directory.Delete(child); }
    }

    [Theory]
    [InlineData(RegistryView.Registry32)]
    [InlineData(RegistryView.Registry64)]
    public async Task Registry_value_restore_preserves_siblings_and_view(RegistryView view)
    {
        using var env = new TestEnvironment();
        var location = new RegistryLocation(RegistryHive.CurrentUser, view, env.RegistryBase + "\\Values");
        env.Registry.CreateSubKey(location);
        env.Registry.SetValue(location, "Target", RegistryValueKind.String, "saved");
        env.Registry.SetValue(location, "Sibling", RegistryValueKind.String, "old");
        var service = Service(env);
        var backup = await service.CreateAsync("values", [RegItem(location, LeftoverKind.RegistryValue, "Target")]);
        Assert.True(backup.Succeeded, string.Join("; ", backup.Warnings));
        env.Registry.SetValue(location, "Target", RegistryValueKind.String, "changed");
        env.Registry.SetValue(location, "Sibling", RegistryValueKind.String, "new");
        var restore = await service.RestoreAsync(backup.BackupId, true);
        Assert.True(restore.Succeeded, string.Join("; ", restore.Entries.Select(e => e.Detail)));
        Assert.Equal("saved", env.Registry.GetValue(location, "Target")?.AsString());
        Assert.Equal("new", env.Registry.GetValue(location, "Sibling")?.AsString());
        env.Registry.DeleteKey(location, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_export_rejects_unrelated_keys_and_deletion_directives(bool deletion)
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Declared", ("Marker", "saved", RegistryValueKind.String));
        var service = Service(env);
        var backup = await service.CreateAsync("registry", [RegItem(location, LeftoverKind.RegistryKey)]);
        var entry = Assert.Single(backup.Manifest.Registry);
        var export = Path.Combine(backup.Directory, entry.ExportFile.Replace('/', Path.DirectorySeparatorChar));
        var payload = deletion
            ? $"[-HKEY_CURRENT_USER\\{location.Path}]"
            : $"[HKEY_CURRENT_USER\\{env.RegistryBase}\\Unexpected]";
        File.WriteAllText(export, $"Windows Registry Editor Version 5.00\r\n\r\n{payload}\r\n\"Marker\"=\"evil\"\r\n", Encoding.Unicode);
        var manifest = backup.Manifest with { Registry = [entry with { Sha256 = Hashing.Sha256File(export) }] };
        File.WriteAllText(Path.Combine(backup.Directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.False(service.PreviewRestore(backup.BackupId, true).CanRestore);
        Assert.False((await service.RestoreAsync(backup.BackupId, true)).Succeeded);
        Assert.Equal("saved", env.Registry.GetValue(location, "Marker")?.AsString());
        Assert.False(env.Registry.KeyExists(new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, env.RegistryBase + "\\Unexpected")));
    }

    [Fact]
    public void Junk_clean_skips_old_tree_with_fresh_or_excluded_descendants()
    {
        using var env = new TestEnvironment();
        var root = env.CreateDirectory("Temp", "Old");
        var fresh = Path.Combine(root, "fresh.tmp");
        File.WriteAllText(fresh, "data");
        Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow.AddHours(-1));
        var service = new JunkService(env.Paths, new PathGuard(env.Paths), FileOperationLog.Null);
        Assert.Equal(0, service.Clean(["user-temp"], true).DeletedItems);
        Assert.Equal(0, service.Clean(["user-temp"], false).DeletedItems);
        Assert.True(File.Exists(fresh));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddHours(-1));
        var excluded = Directory.CreateDirectory(Path.Combine(root, "Documents"));
        excluded.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-1);
        Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(0, service.Clean(["user-temp"], false).DeletedItems);
        Assert.True(Directory.Exists(excluded.FullName));
    }

    [Fact]
    public void Trace_manifest_has_an_independent_size_limit()
    {
        using var env = new TestEnvironment();
        var store = new TraceStore(env.Paths);
        var trace = new TraceDocument { Id = "trace", ProgramName = "fixture", CreatedAt = DateTimeOffset.UtcNow, SchemaVersion = 2 };
        store.Save(trace);
        var service = new TraceBundleService(env.Paths, store, new FixedClock(DateTimeOffset.UtcNow));
        var path = Path.Combine(env.Root, "trace.zip");
        Assert.True(service.Export(trace.Id, path).Succeeded);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("manifest.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(new string('a', 1024 * 1024 + 1));
        }
        var result = service.Import(path);
        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Error);
    }

    [Fact]
    public async Task Prune_uses_the_directory_identity_even_if_manifest_id_is_wrong()
    {
        using var env = new TestEnvironment();
        var service = Service(env);
        var old = await service.CreateAsync("old", [FileItem(env.CreateFile("Fixtures/Prune/old.txt", "old"))]);
        var newer = await service.CreateAsync("new", [FileItem(env.CreateFile("Fixtures/Prune/new.txt", "new"))]);
        var corrupted = old.Manifest with { Id = newer.BackupId, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        File.WriteAllText(Path.Combine(old.Directory, "manifest.json"), JsonSerializer.Serialize(corrupted,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(1, service.Prune(1));
        Assert.False(Directory.Exists(old.Directory));
        Assert.True(Directory.Exists(newer.Directory));
    }

    [Fact]
    public async Task Prune_and_delete_skip_active_restore_then_can_remove_it()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Race", ("Marker", "saved", RegistryValueKind.String));
        var file = env.CreateFile("Fixtures/Race/data.txt", "saved");
        var service = Service(env);
        var old = await service.CreateAsync("old", [RegItem(location, LeftoverKind.RegistryKey), FileItem(file)]);
        var newer = await service.CreateAsync("new", [FileItem(env.CreateFile("Fixtures/Race/new.txt", "new"))]);
        var oldManifest = old.Manifest with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        File.WriteAllText(Path.Combine(old.Directory, "manifest.json"), JsonSerializer.Serialize(oldManifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Delete(file);
        env.Registry.DeleteKey(location, true);
        var gate = new GateRunner();
        var task = Service(env, gate).RestoreAsync(old.BackupId, true);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.False(Service(env).Delete(old.BackupId));
            Assert.Equal(0, Service(env).Prune(1));
            Assert.True(Directory.Exists(old.Directory));
        }
        finally { gate.Release.TrySetResult(); }
        Assert.True((await task).Succeeded);
        Assert.Equal("saved", File.ReadAllText(file));
        Assert.Equal(1, Service(env).Prune(1));
        Assert.False(Directory.Exists(old.Directory));
        Assert.True(Directory.Exists(newer.Directory));
    }

    private static void MakeJunction(string path, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(path); info.ArgumentList.Add(target);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
    private sealed class InvalidExportRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, string arguments, bool shellExecute, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(arguments, "\"([^\"]+)\"");
            File.WriteAllText(matches[1].Groups[1].Value, "Windows Registry Editor Version 5.00\r\n\r\n[-HKEY_CURRENT_USER\\Software\\OUTests]\r\n", Encoding.Unicode);
            return Task.FromResult(new ProcessResult { Started = true, ExitCode = 0 });
        }
    }
    private sealed class GateRunner : IProcessRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessResult> RunAsync(string fileName, string arguments, bool shellExecute, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var result = await new ProcessRunner().RunAsync(fileName, arguments, shellExecute, timeout, cancellationToken);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return result;
        }
    }
}
