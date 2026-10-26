using System.IO.Compression;
using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class StateAndPreviewTests
{
    private static LeftoverItem Item(
        string id,
        LeftoverKind kind = LeftoverKind.File,
        string? path = null,
        LeftoverConfidence confidence = LeftoverConfidence.High)
        => new()
        {
            Id = id,
            Kind = kind,
            DisplayPath = path ?? id,
            Confidence = confidence,
            Reason = "test",
            FilePath = path
        };

    [Fact]
    public void Ignored_store_round_trips_and_matches_by_program()
    {
        using var env = new TestEnvironment();
        var store = new IgnoredLeftoverStore(env.Paths);
        var program = TestPrograms.Create("Ignored App");
        var other = TestPrograms.Create("Other App");
        var item = Item("file|c:\\data\\ignored", path: @"C:\data\ignored");

        Assert.Equal(1, store.Add(program.Id, program.Name, [item], DateTimeOffset.UtcNow));
        Assert.Equal(0, store.Add(program.Id, program.Name, [item], DateTimeOffset.UtcNow));
        Assert.Contains(item.Id, store.GetIgnoredItemIds(program));
        Assert.Empty(store.GetIgnoredItemIds(other));
        Assert.True(File.Exists(store.FilePath));

        Assert.Equal(1, store.Clear(program.Id, program.Name));
        Assert.Empty(store.GetIgnoredItemIds(program));
    }

    [Fact]
    public void Ignored_store_treats_a_corrupt_file_as_empty()
    {
        using var env = new TestEnvironment();
        Directory.CreateDirectory(env.Paths.DataDirectory);
        File.WriteAllText(Path.Combine(env.Paths.DataDirectory, "ignored-leftovers.json"), "{not json");

        var store = new IgnoredLeftoverStore(env.Paths);

        Assert.Empty(store.GetIgnoredItemIds(TestPrograms.Create("Any App")));
    }

    [Fact]
    public async Task Scanner_hides_ignored_items_and_reports_them()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateDirectory("ProgramData", "IgnoredApp");
        env.CreateFile("ProgramData/IgnoredApp/settings.ini");
        var traces = new TraceStore(env.Paths);
        traces.Save(new TraceDocument
        {
            SchemaVersion = 2,
            Id = "ignored-trace",
            ProgramName = "Ignored App",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = [new TracedFile { Path = folder, IsDirectory = true }]
        });

        var ignored = new IgnoredLeftoverStore(env.Paths);
        var scanner = new LeftoverScanner(env.Registry, traces, env.Paths, new PathGuard(env.Paths), ignored);
        var program = TestPrograms.Create("Ignored App");
        var first = await scanner.ScanAsync(program);
        var folderItem = Assert.Single(first.Items.Where(i => i.Kind == LeftoverKind.Directory));
        ignored.Add(program.Id, program.Name, [folderItem], DateTimeOffset.UtcNow);

        var second = await scanner.ScanAsync(program);

        Assert.DoesNotContain(second.Items, i => i.Id == folderItem.Id);
        Assert.Equal(1, second.IgnoredCount);
        Assert.Contains(second.Warnings, w => w.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_history_reports_new_resolved_and_persistent_items()
    {
        using var env = new TestEnvironment();
        var history = new LeftoverHistoryStore(env.Paths);
        var program = TestPrograms.Create("History App");
        var first = new LeftoverScanResult { ProgramId = program.Id, ProgramName = program.Name };
        first.Items.Add(Item("file|a", path: @"C:\data\a"));
        first.Items.Add(Item("file|b", path: @"C:\data\b"));
        var firstDiff = history.CompareAndSave(first, DateTimeOffset.UtcNow);

        Assert.False(firstDiff.HasPrevious);

        var second = new LeftoverScanResult { ProgramId = program.Id, ProgramName = program.Name };
        second.Items.Add(Item("file|a", path: @"C:\data\a"));
        second.Items.Add(Item("file|c", path: @"C:\data\c"));
        var secondDiff = history.CompareAndSave(second, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(secondDiff.HasPrevious);
        Assert.Single(secondDiff.PersistentItems);
        Assert.Single(secondDiff.NewItems);
        Assert.Single(secondDiff.ResolvedItems);
        Assert.Equal("file|c", secondDiff.NewItems[0].Id);
        Assert.Equal("file|b", secondDiff.ResolvedItems[0].Id);
        Assert.NotNull(history.Get(program));
    }

    [Fact]
    public void Trace_bundle_round_trips_and_expands_paths()
    {
        using var env = new TestEnvironment();
        var store = new TraceStore(env.Paths);
        var trace = new TraceDocument
        {
            SchemaVersion = 2,
            Id = "bundle-trace",
            ProgramName = "Bundle App",
            CreatedAt = DateTimeOffset.UtcNow,
            InstallLocation = Path.Combine(env.Paths.ProgramFiles, "Bundle"),
            Files =
            [
                new TracedFile
                {
                    Path = Path.Combine(env.Paths.ProgramFiles, "Bundle", "bundle.exe"),
                    IsDirectory = false
                }
            ]
        };
        store.Save(trace);
        var service = new TraceBundleService(env.Paths, store, new FixedClock(DateTimeOffset.UtcNow));
        var bundlePath = Path.Combine(env.Root, "bundle.zip");

        var export = service.Export(trace.Id, bundlePath);
        var import = service.Import(bundlePath);

        Assert.True(export.Succeeded, export.Error);
        Assert.True(export.BundleSha256 is { Length: 64 });
        Assert.True(import.Succeeded, import.Error);
        Assert.NotNull(import.Trace);
        Assert.NotEqual(trace.Id, import.Trace!.Id);
        Assert.Equal(trace.Files[0].Path, import.Trace.Files[0].Path);
        Assert.Equal(trace.InstallLocation, import.Trace.InstallLocation);

        using var archive = ZipFile.OpenRead(bundlePath);
        var portable = archive.GetEntry("trace.json")!;
        using var reader = new StreamReader(portable.Open());
        Assert.Contains("%ProgramFiles%", reader.ReadToEnd(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trace_bundle_rejects_a_tampered_trace()
    {
        using var env = new TestEnvironment();
        var store = new TraceStore(env.Paths);
        var trace = new TraceDocument
        {
            SchemaVersion = 2,
            Id = "tamper-trace",
            ProgramName = "Tamper App",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = [new TracedFile { Path = Path.Combine(env.Paths.ProgramData, "Tamper", "a.dat") }]
        };
        store.Save(trace);
        var service = new TraceBundleService(env.Paths, store, new FixedClock(DateTimeOffset.UtcNow));
        var bundlePath = Path.Combine(env.Root, "tamper.zip");
        Assert.True(service.Export(trace.Id, bundlePath).Succeeded);

        using (var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("trace.json")!;
            entry.Delete();
            var replacement = archive.CreateEntry("trace.json");
            using var writer = new StreamWriter(replacement.Open());
            writer.Write("{\"schemaVersion\":2,\"id\":\"tamper-trace\",\"programName\":\"Tamper App\"}");
        }

        var import = service.Import(bundlePath);

        Assert.False(import.Succeeded);
        Assert.Contains("hash", import.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_preview_reports_restore_and_skip_actions()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("PreviewVendor\\PreviewApp", ("DisplayName", "Preview App", RegistryValueKind.String));
        var folder = env.CreateDirectory("ProgramData", "PreviewApp");
        env.CreateFile("ProgramData/PreviewApp/settings.ini");
        var log = Core.Abstractions.FileOperationLog.Null;
        var backup = new BackupService(env.Paths, env.Registry, new Core.Abstractions.ProcessRunner(), new FixedClock(DateTimeOffset.UtcNow), log, new PathGuard(env.Paths));
        var created = await backup.CreateAsync("Preview App",
        [
            new LeftoverItem
            {
                Id = "registry|preview",
                Kind = LeftoverKind.RegistryKey,
                DisplayPath = location.DisplayPath,
                Confidence = LeftoverConfidence.Exact,
                Reason = "test",
                RegistryLocation = location
            },
            new LeftoverItem
            {
                Id = "folder|preview",
                Kind = LeftoverKind.Directory,
                DisplayPath = folder,
                Confidence = LeftoverConfidence.Exact,
                Reason = "test",
                FilePath = folder
            }
        ]);

        Assert.True(created.Succeeded, string.Join("; ", created.Warnings));
        Assert.Empty(created.Warnings);
        env.Registry.DeleteKey(location, recursive: true);
        Directory.Delete(folder, recursive: true);
        for (var i = 0; i < 50 && Directory.Exists(folder); i++)
        {
            Thread.Sleep(10);
        }
        Assert.False(Directory.Exists(folder));

        var preview = backup.PreviewRestore(created.BackupId, overwrite: false);

        Assert.True(preview.CanRestore);
        Assert.Equal(2, preview.RestoreCount);
        Assert.Equal(0, preview.BlockedCount);
        Assert.Contains(preview.Entries, e => e.Kind == "Registry key" && e.Action == RestorePreviewAction.Restore);
        Assert.Contains(preview.Entries, e => e.Kind == "Folder" && e.Action == RestorePreviewAction.Restore);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.ini"), "new");
        var skip = backup.PreviewRestore(created.BackupId, overwrite: false);
        var overwrite = backup.PreviewRestore(created.BackupId, overwrite: true);
        Assert.Contains(skip.Entries, e => e.Kind == "Folder" && e.Action == RestorePreviewAction.Skip);
        Assert.Contains(overwrite.Entries, e => e.Kind == "Folder" && e.Action == RestorePreviewAction.Overwrite);
    }
}
