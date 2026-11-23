using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class NewFeatureTests
{
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
