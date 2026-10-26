using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class FalsePositiveSafetyTests
{
    private static LeftoverScanner CreateScanner(TestEnvironment env)
        => new(env.Registry, new TraceStore(env.Paths), env.Paths, new PathGuard(env.Paths));

    [Fact]
    public void ScoreName_rejects_cross_product_false_positives()
    {
        // 1. Visual Studio Code vs Visual Studio
        var vsTokens = NameNormalizer.Tokens("Visual Studio");
        var vsPublisher = NameNormalizer.Tokens("Microsoft Corporation");
        Assert.Equal(0, LeftoverScanner.ScoreName("Visual Studio Code", "visualstudio", vsTokens, vsPublisher, allowPublisherOnly: false));

        var vscTokens = NameNormalizer.Tokens("Visual Studio Code");
        Assert.Equal(0, LeftoverScanner.ScoreName("Visual Studio", "visualstudiocode", vscTokens, vsPublisher, allowPublisherOnly: false));

        // 2. Adobe Photoshop vs Adobe Illustrator
        var psTokens = NameNormalizer.Tokens("Adobe Photoshop");
        var adobePublisher = NameNormalizer.Tokens("Adobe Systems Incorporated");
        Assert.Equal(0, LeftoverScanner.ScoreName("Adobe Illustrator", "adobephotoshop", psTokens, adobePublisher, allowPublisherOnly: false));

        // 3. GOM Player vs GOM Audio
        var gomPlayerTokens = NameNormalizer.Tokens("GOM Player");
        var gomPublisher = NameNormalizer.Tokens("GOM & Company");
        Assert.Equal(0, LeftoverScanner.ScoreName("GOM Audio", "gomplayer", gomPlayerTokens, gomPublisher, allowPublisherOnly: false));

        // 4. Epic Games Launcher vs Fortnite
        var epicLauncherTokens = NameNormalizer.Tokens("Epic Games Launcher");
        var epicPublisher = NameNormalizer.Tokens("Epic Games, Inc.");
        Assert.Equal(0, LeftoverScanner.ScoreName("Fortnite", "epicgameslauncher", epicLauncherTokens, epicPublisher, allowPublisherOnly: false));

        // 5. Google Chrome vs Google Drive
        var chromeTokens = NameNormalizer.Tokens("Google Chrome");
        var googlePublisher = NameNormalizer.Tokens("Google LLC");
        Assert.Equal(0, LeftoverScanner.ScoreName("Google Drive", "googlechrome", chromeTokens, googlePublisher, allowPublisherOnly: false));
    }

    [Fact]
    public void IsVendorFolder_identifies_vendor_roots_and_preserves_them()
    {
        var epicPublisher = NameNormalizer.Tokens("Epic Games, Inc.");
        var epicLauncherProduct = NameNormalizer.Tokens("Epic Games Launcher");

        // Epic Games is the vendor folder, not the product folder
        Assert.True(LeftoverScanner.IsVendorFolder("Epic Games", epicPublisher, epicLauncherProduct));

        // Epic Games Launcher is the product folder, not a vendor root
        Assert.False(LeftoverScanner.IsVendorFolder("Epic Games Launcher", epicPublisher, epicLauncherProduct));

        // Adobe vs Adobe Photoshop
        var adobePublisher = NameNormalizer.Tokens("Adobe Systems");
        var photoshopProduct = NameNormalizer.Tokens("Adobe Photoshop");
        Assert.True(LeftoverScanner.IsVendorFolder("Adobe", adobePublisher, photoshopProduct));
        Assert.False(LeftoverScanner.IsVendorFolder("Adobe Photoshop", adobePublisher, photoshopProduct));

        // Known multi-product vendors are identified even without publisher context
        Assert.True(LeftoverScanner.IsVendorFolder("Valve", publisherTokens: null));
        Assert.True(LeftoverScanner.IsVendorFolder("Ubisoft", publisherTokens: null));
        Assert.True(LeftoverScanner.IsVendorFolder("Electronic Arts", publisherTokens: null));
        Assert.True(LeftoverScanner.IsVendorFolder("GOM & Company", publisherTokens: null));
    }

    [Fact]
    public async Task Scanner_never_selects_vendor_root_folder_as_leftover()
    {
        using var env = new TestEnvironment();

        // Simulate Program Files containing Epic Games with Launcher and Fortnite
        var epicGamesRoot = env.CreateDirectory("Program Files", "Epic Games");
        var launcherDir = env.CreateDirectory("Program Files", "Epic Games", "Launcher");
        var fortniteDir = env.CreateDirectory("Program Files", "Epic Games", "Fortnite");

        env.CreateFile("Program Files/Epic Games/Launcher/EpicGamesLauncher.exe", "launcher");
        env.CreateFile("Program Files/Epic Games/Fortnite/FortniteClient.exe", "game");

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create(
            "Epic Games Launcher",
            publisher: "Epic Games, Inc.",
            installLocation: launcherDir);

        var scan = await scanner.ScanAsync(program, otherPrograms: []);

        // The specific launcher directory can be found, but the vendor root MUST NOT be in items
        Assert.DoesNotContain(scan.Items, i => string.Equals(i.FilePath, epicGamesRoot, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scan.Items, i => string.Equals(i.FilePath, fortniteDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scanner_protects_vendor_folder_even_if_recorded_as_install_location()
    {
        using var env = new TestEnvironment();

        // Suppose a broken installer wrote InstallLocation = C:\Program Files\Epic Games
        var epicGamesRoot = env.CreateDirectory("Program Files", "Epic Games");
        var launcherDir = env.CreateDirectory("Program Files", "Epic Games", "Launcher");
        var fortniteDir = env.CreateDirectory("Program Files", "Epic Games", "Fortnite");

        env.CreateFile("Program Files/Epic Games/Launcher/EpicGamesLauncher.exe", "launcher");
        env.CreateFile("Program Files/Epic Games/Fortnite/FortniteClient.exe", "game");

        var scanner = CreateScanner(env);
        var program = TestPrograms.Create(
            "Epic Games Launcher",
            publisher: "Epic Games, Inc.",
            installLocation: epicGamesRoot); // Path points to root!

        var scan = await scanner.ScanAsync(program, otherPrograms: []);

        // Root vendor directory MUST NOT be offered as a leftover item to delete
        Assert.DoesNotContain(scan.Items, i => string.Equals(i.FilePath, epicGamesRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(scan.Warnings, w => w.Contains("Vendor folder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathGuard_rejects_user_credentials_and_personal_roots()
    {
        using var env = new TestEnvironment();
        var guard = new PathGuard(env.Paths);

        // SSH, AWS, Azure, Kube, GPG credentials
        var sshKey = Path.Combine(env.Paths.UserProfile, ".ssh", "id_ed25519");
        var awsCreds = Path.Combine(env.Paths.UserProfile, ".aws", "credentials");
        var azureToken = Path.Combine(env.Paths.UserProfile, ".azure", "tokens.json");
        var kubeConfig = Path.Combine(env.Paths.UserProfile, ".kube", "config");
        var gpgKey = Path.Combine(env.Paths.UserProfile, ".gnupg", "secring.gpg");

        Assert.False(guard.ValidateForDeletion(sshKey).Allowed);
        Assert.False(guard.ValidateForDeletion(awsCreds).Allowed);
        Assert.False(guard.ValidateForDeletion(azureToken).Allowed);
        Assert.False(guard.ValidateForDeletion(kubeConfig).Allowed);
        Assert.False(guard.ValidateForDeletion(gpgKey).Allowed);

        // Recursive deletion of Documents, Desktop, Downloads roots
        var docs = Path.Combine(env.Paths.UserProfile, "Documents");
        var desktop = Path.Combine(env.Paths.UserProfile, "Desktop");
        var downloads = Path.Combine(env.Paths.UserProfile, "Downloads");

        Assert.False(guard.ValidateForDeletion(docs, recursive: true).Allowed);
        Assert.False(guard.ValidateForDeletion(desktop, recursive: true).Allowed);
        Assert.False(guard.ValidateForDeletion(downloads, recursive: true).Allowed);

        // Junk item guard rejects any files under user personal folders
        Assert.False(guard.ValidateJunkItem(Path.Combine(docs, "important.docx"), docs).Allowed);
        Assert.False(guard.ValidateJunkItem(Path.Combine(desktop, "file.txt"), desktop).Allowed);
        Assert.False(guard.ValidateJunkItem(sshKey, env.Paths.UserProfile).Allowed);
    }
}
