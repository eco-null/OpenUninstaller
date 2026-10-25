using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class InventoryAndBrowserTests
{
    [Fact]
    public void Msix_inventory_reads_the_manifest_without_writing_anything()
    {
        using var env = new TestEnvironment();
        var packageRoot = env.CreateDirectory("StorePackages", "Widget.App_1.2.3.4_x64__abc");
        var manifest = Path.Combine(packageRoot, "AppxManifest.xml");
        File.WriteAllText(manifest, """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Widget.App" Publisher="CN=Widget" Version="1.2.3.4" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>Widget Store App</DisplayName>
                <PublisherDisplayName>Widget Corp</PublisherDisplayName>
              </Properties>
            </Package>
            """);

        const string fullName = "Widget.App_1.2.3.4_x64__abc";
        var key = new RegistryLocation(
            env.Paths.MsixPackagesRoot.Hive,
            env.Paths.MsixPackagesRoot.View,
            $"{env.Paths.MsixPackagesRoot.Path}\\{fullName}");
        env.Registry.CreateSubKey(key);
        env.Registry.SetValue(key, "PackageRootFolder", RegistryValueKind.String, packageRoot);
        var before = File.ReadAllText(manifest);

        var packages = new MsixInventoryService(env.Registry, env.Paths).Scan();

        var package = Assert.Single(packages);
        Assert.Equal("Widget Store App", package.DisplayName);
        Assert.Equal("Widget.App", package.Name);
        Assert.Equal("1.2.3.4", package.Version);
        Assert.Equal("x64", package.Architecture);
        Assert.Equal("Widget Corp", package.Publisher);
        Assert.False(package.IsFramework);
        Assert.Equal(before, File.ReadAllText(manifest));
        Assert.True(env.Registry.KeyExists(key));
    }

    [Fact]
    public void Msix_inventory_falls_back_to_the_package_name_when_the_manifest_is_missing()
    {
        using var env = new TestEnvironment();
        var key = new RegistryLocation(
            env.Paths.MsixPackagesRoot.Hive,
            env.Paths.MsixPackagesRoot.View,
            $"{env.Paths.MsixPackagesRoot.Path}\\Broken_1.0.0.0_x64__def");
        env.Registry.CreateSubKey(key);
        env.Registry.SetValue(key, "PackageRootFolder", RegistryValueKind.String, Path.Combine(env.Root, "nowhere"));

        var package = Assert.Single(new MsixInventoryService(env.Registry, env.Paths).Scan());
        Assert.Equal("Broken", package.Name);
        Assert.Equal("Broken", package.DisplayName);
        Assert.Equal(Path.Combine(env.Root, "nowhere"), package.InstallLocation);
    }

    [Fact]
    public void Browser_scan_finds_only_history_and_cookie_files()
    {
        using var env = new TestEnvironment();
        env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/History", "history");
        env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Network/Cookies", "cookies");
        env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Login Data", "passwords");
        env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Bookmarks", "bookmarks");
        env.CreateFile("Roaming/Mozilla/Firefox/Profiles/abc.default/places.sqlite", "history");
        env.CreateFile("Roaming/Mozilla/Firefox/Profiles/abc.default/cookies.sqlite", "cookies");
        env.CreateFile("Roaming/Mozilla/Firefox/Profiles/abc.default/key4.db", "keys");

        var service = new BrowserDataService(env.Paths, new PathGuard(env.Paths), _ => false);
        var scan = service.Scan();

        Assert.Equal(2, scan.Profiles.Count);
        Assert.Equal(4, scan.TotalFiles);
        Assert.Contains(scan.Profiles, p => p.Browser == "Chrome" && p.HistoryCount == 1 && p.CookieCount == 1);
        Assert.Contains(scan.Profiles, p => p.Browser == "Firefox" && p.HistoryCount == 1 && p.CookieCount == 1);
        Assert.DoesNotContain(scan.Profiles.SelectMany(p => p.Files),
            f => f.Path.EndsWith("Login Data", StringComparison.OrdinalIgnoreCase)
                 || f.Path.EndsWith("Bookmarks", StringComparison.OrdinalIgnoreCase)
                 || f.Path.EndsWith("key4.db", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Browser_clean_dry_run_deletes_nothing_and_real_clean_keeps_other_data()
    {
        using var env = new TestEnvironment();
        var history = env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/History", "history");
        var cookies = env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Network/Cookies", "cookies");
        var passwords = env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Login Data", "passwords");
        var bookmarks = env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/Bookmarks", "bookmarks");

        var service = new BrowserDataService(env.Paths, new PathGuard(env.Paths), _ => false);
        var profiles = service.Scan().Profiles;

        var dry = service.Clean(profiles, dryRun: true);
        Assert.Equal(2, dry.DeletedFiles);
        Assert.True(File.Exists(history));
        Assert.True(File.Exists(cookies));

        var real = service.Clean(profiles, dryRun: false);
        Assert.Equal(2, real.DeletedFiles);
        Assert.Empty(real.Messages);
        Assert.False(File.Exists(history));
        Assert.False(File.Exists(cookies));
        Assert.True(File.Exists(passwords));
        Assert.True(File.Exists(bookmarks));
    }

    [Fact]
    public void Browser_clean_refuses_to_touch_a_running_browser()
    {
        using var env = new TestEnvironment();
        var history = env.CreateFile("LocalAppData/Google/Chrome/User Data/Default/History", "history");
        var service = new BrowserDataService(env.Paths, new PathGuard(env.Paths), _ => true);

        var result = service.Clean(service.Scan().Profiles, dryRun: false);

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(result.Skipped > 0);
        Assert.Contains(result.Messages, m => m.Contains("Chrome is running", StringComparison.Ordinal));
        Assert.True(File.Exists(history));
    }
}
