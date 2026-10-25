using OpenUninstaller.Core.Localization;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class NewFeatureTests
{
    [Fact]
    public void Localization_loads_all_45_non_arabic_languages()
    {
        var service = new LocalizationService();
        Assert.Equal(45, service.AvailableLanguages.Count);

        // Verify Arabic-script languages are strictly excluded
        Assert.DoesNotContain(service.AvailableLanguages, l => l.Id.Equals("arabic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(service.AvailableLanguages, l => l.Id.Equals("kurdish", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(service.AvailableLanguages, l => l.Id.Equals("persian", StringComparison.OrdinalIgnoreCase));

        // Verify key languages are present
        Assert.Contains(service.AvailableLanguages, l => l.Id.Equals("english", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(service.AvailableLanguages, l => l.Id.Equals("turkish", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(service.AvailableLanguages, l => l.Id.Equals("german", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(service.AvailableLanguages, l => l.Id.Equals("french", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(service.AvailableLanguages, l => l.Id.Equals("japanese", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Localization_switches_languages_and_falls_back_to_english()
    {
        var service = new LocalizationService();

        service.SetLanguage("turkish");
        Assert.Equal("turkish", service.CurrentLanguage);
        Assert.Equal("Kaldır", service.GetString("ActionUninstall"));
        Assert.Equal("Yenile", service.GetString("ActionRefresh"));
        Assert.Equal("Gereksiz Dosyalar", service.GetString("NavJunk"));

        // Fallback to English for non-existing key
        Assert.Equal("CustomFallback", service.GetString("NonExistentKey123", "CustomFallback"));

        // Change notification
        string? notified = null;
        service.LanguageChanged += lang => notified = lang;
        service.SetLanguage("german");
        Assert.Equal("german", notified);
        Assert.Equal("Deinstallieren", service.GetString("ActionUninstall"));
    }

    [Fact]
    public void Context_menu_service_handles_registration_gracefully()
    {
        var service = new ContextMenuService();

        // Invalid path fails gracefully without throwing
        var result = service.Register(@"C:\nonexistent_path_openuninstaller.exe");
        Assert.False(result);

        // Unregister succeeds cleanly
        var unregistered = service.Unregister();
        Assert.True(unregistered);
    }

    [Fact]
    public void Live_activity_tracker_starts_tracks_and_cleans_up_on_dispose()
    {
        using var tracker = new LiveActivityTracker();
        Assert.False(tracker.IsRunning);

        tracker.Start();
        Assert.True(tracker.IsRunning);

        // Disposing stops everything immediately
        tracker.Dispose();
        Assert.False(tracker.IsRunning);
    }
}
