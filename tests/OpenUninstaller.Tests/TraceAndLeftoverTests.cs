using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class TraceAndLeftoverTests
{
    private static TraceDocument CreateTrace(TestEnvironment env, string name, params TracedFile[] files)
        => new()
        {
            Id = "20260101-000000-" + name.ToLowerInvariant(),
            ProgramName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            Files = [.. files],
            RegistryKeys =
            [
                new TracedRegistryKey
                {
                    Hive = RegistryHive.CurrentUser,
                    View = RegistryView.Registry64,
                    Path = $"{env.RegistryBase}\\TraceVendor\\{name}"
                }
            ]
        };

    [Fact]
    public void Trace_store_round_trips_and_finds_by_name()
    {
        using var env = new TestEnvironment();
        var store = new TraceStore(env.Paths);
        var trace = CreateTrace(env, "TraceApp");
        store.Save(trace);

        var loaded = store.Get(trace.Id);
        Assert.NotNull(loaded);
        Assert.Equal("TraceApp", loaded.ProgramName);
        Assert.Single(store.List());
        Assert.Equal(trace.Id, store.FindFor(TestPrograms.Create("TraceApp"))?.Id);
        Assert.True(store.Delete(trace.Id));
        Assert.Null(store.Get(trace.Id));
    }

    [Fact]
    public void Trace_store_ignores_corrupt_files()
    {
        using var env = new TestEnvironment();
        Directory.CreateDirectory(env.Paths.TracesDirectory);
        File.WriteAllText(Path.Combine(env.Paths.TracesDirectory, "broken.json"), "{not json");

        var store = new TraceStore(env.Paths);

        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Trace_scan_returns_exact_items_and_marks_protected_paths()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("TraceVendor\\TraceApp", ("DisplayName", "TraceApp", RegistryValueKind.String));
        var appFolder = env.CreateDirectory("ProgramData", "TraceApp");
        env.CreateFile("ProgramData/TraceApp/settings.ini");
        var protectedFile = env.CreateFile("Windows/system-leftover.txt");
        var store = new TraceStore(env.Paths);
        store.Save(new TraceDocument
        {
            SchemaVersion = 2,
            Id = "trace-app",
            ProgramName = "Trace App",
            CreatedAt = DateTimeOffset.UtcNow,
            RegistryKeys =
            [
                new TracedRegistryKey
                {
                    Hive = RegistryHive.CurrentUser,
                    View = RegistryView.Registry64,
                    Path = location.Path
                }
            ],
            Files =
            [
                new TracedFile { Path = appFolder, IsDirectory = true },
                new TracedFile { Path = protectedFile, IsDirectory = false }
            ]
        });
        var scanner = new LeftoverScanner(env.Registry, store, env.Paths, new PathGuard(env.Paths));
        var program = TestPrograms.Create("Trace App", publisher: "TraceVendor");

        var result = await scanner.ScanAsync(program);

        Assert.True(result.UsedTrace);
        Assert.Equal("trace-app", result.TraceId);
        Assert.Contains(result.Items, i => i.Kind == LeftoverKind.RegistryKey && i.Confidence == LeftoverConfidence.Exact);
        Assert.Contains(result.Items, i => i.Kind == LeftoverKind.Directory && i.Confidence == LeftoverConfidence.Exact);
        var protectedItem = Assert.Single(result.Items.Where(i => i.IsProtected));
        Assert.Contains("Windows", protectedItem.ProtectedReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Items.Where(i => i.IsFromTrace && !i.IsProtected), i => Assert.True(i.IsSelected));
    }

    [Fact]
    public async Task Heuristic_scan_finds_install_location_and_matching_folders()
    {
        using var env = new TestEnvironment();
        var installLocation = env.CreateDirectory("Program Files", "Contoso", "Widget");
        env.CreateFile("Program Files/Contoso/Widget/widget.exe");
        env.CreateFile("ProgramData/Contoso Widget/settings.dat");
        var program = TestPrograms.Create(
            "Contoso Widget Pro",
            publisher: "Contoso",
            installLocation: installLocation);
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

        var result = await scanner.ScanAsync(program);

        Assert.False(result.UsedTrace);
        var installItem = Assert.Single(result.Items.Where(i => i.DisplayPath == installLocation));
        Assert.Equal(LeftoverConfidence.High, installItem.Confidence);
        Assert.True(installItem.IsSelected);
        Assert.Contains(result.Items, i => i.DisplayPath.EndsWith("Contoso Widget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Items, i => i.DisplayPath.Contains("widget.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Heuristic_scan_never_returns_protected_windows_paths()
    {
        using var env = new TestEnvironment();
        env.CreateFile("Windows/Temp/not-ours.tmp");
        var program = TestPrograms.Create("Windows Thing", installLocation: env.Paths.WindowsDirectory);
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

        var result = await scanner.ScanAsync(program);

        Assert.DoesNotContain(result.Items, i => i.DisplayPath.StartsWith(env.Paths.WindowsDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Heuristic_scan_descends_into_vendor_folder_for_the_product_folder()
    {
        using var env = new TestEnvironment();
        var chrome = env.CreateDirectory("LocalAppData", "Google", "Chrome");
        env.CreateDirectory("LocalAppData", "Google", "Chrome", "User Data");
        var drive = env.CreateDirectory("LocalAppData", "Google", "Drive");
        var widget = env.CreateDirectory("Roaming", "Contoso", "Widget");
        var program = TestPrograms.Create("Google Chrome", publisher: "Google");
        var widgetProgram = TestPrograms.Create("Contoso Widget", publisher: "Contoso");
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

        var chromeScan = await scanner.ScanAsync(program);
        var widgetScan = await scanner.ScanAsync(widgetProgram);

        var chromeItem = Assert.Single(chromeScan.Items.Where(i => i.DisplayPath == chrome));
        Assert.Equal(LeftoverConfidence.High, chromeItem.Confidence);
        Assert.DoesNotContain(chromeScan.Items, i => i.DisplayPath == drive);
        Assert.DoesNotContain(chromeScan.Items, i => i.DisplayPath == Path.Combine(env.Paths.LocalAppData, "Google"));

        var widgetItem = Assert.Single(widgetScan.Items.Where(i => i.DisplayPath == widget));
        Assert.Equal(LeftoverConfidence.High, widgetItem.Confidence);
        Assert.True(widgetItem.IsSelected);
        Assert.DoesNotContain(widgetScan.Items, i => i.DisplayPath == Path.Combine(env.Paths.AppData, "Contoso"));
    }

    [Fact]
    public async Task Heuristic_scan_skips_paths_owned_by_another_installed_program()
    {
        using var env = new TestEnvironment();
        var sharedRoot = env.CreateDirectory("Program Files", "Contoso");
        var otherRoot = env.CreateDirectory("Program Files", "Contoso", "Other");
        env.CreateFile("Program Files/Contoso/Other/other.exe");
        env.CreateFile("Program Files/Contoso/suite.dat");
        var program = TestPrograms.Create(
            "Contoso Suite",
            publisher: "Contoso",
            installLocation: sharedRoot);
        var other = TestPrograms.Create(
            "Contoso Other",
            publisher: "Contoso",
            installLocation: otherRoot,
            displayIcon: Path.Combine(otherRoot, "other.exe"));
        var scanner = new LeftoverScanner(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

        var result = await scanner.ScanAsync(program, otherPrograms: [program, other]);

        Assert.DoesNotContain(result.Items, i => i.DisplayPath == sharedRoot);
        Assert.DoesNotContain(result.Items, i => i.DisplayPath == otherRoot);
        Assert.Contains(result.Warnings, w => w.Contains("Contoso Other", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Trace_scan_marks_a_path_used_by_another_program_as_protected()
    {
        using var env = new TestEnvironment();
        var shared = env.CreateDirectory("Program Files", "Shared");
        var otherRoot = env.CreateDirectory("Program Files", "Shared", "Other");
        env.CreateFile("Program Files/Shared/Other/other.exe");
        var store = new TraceStore(env.Paths);
        store.Save(new TraceDocument
        {
            Id = "shared-trace",
            ProgramName = "Shared App",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = [new TracedFile { Path = shared, IsDirectory = true }]
        });
        var program = TestPrograms.Create("Shared App");
        var other = TestPrograms.Create(
            "Other App",
            installLocation: otherRoot,
            displayIcon: Path.Combine(otherRoot, "other.exe"));
        var scanner = new LeftoverScanner(env.Registry, store, env.Paths, new PathGuard(env.Paths));

        var result = await scanner.ScanAsync(program, otherPrograms: [program, other]);

        var item = Assert.Single(result.Items.Where(i => i.DisplayPath == shared));
        Assert.True(item.IsProtected);
        Assert.False(item.IsSelected);
        Assert.Contains("Other App", item.ProtectedReason ?? string.Empty);
    }

    [Fact]
    public void Name_normalizer_extracts_distinctive_tokens()
    {
        var tokens = Core.Support.NameNormalizer.MatchTokens("Contoso Widget Pro 2024", "Contoso Inc.");
        Assert.Contains("widget", tokens);
        Assert.DoesNotContain("pro", tokens);
        Assert.DoesNotContain("contoso", tokens);
    }
}
