using System.Globalization;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Service;

internal static class Worker
{
    public static async Task RunAsync(AppPaths paths, bool once, CancellationToken cancellationToken)
    {
        var clock = new SystemClock();
        var registry = new WindowsRegistryStore();
        var guard = new PathGuard(paths);
        var log = new FileOperationLog(paths.LogsDirectory, "service");
        var backups = new BackupService(paths, registry, new ProcessRunner(), clock, log, guard);
        var junk = new JunkService(paths, guard, log);
        var maintenance = new MaintenanceService(paths, backups, junk, clock, log);

        DateTimeOffset? lastRun = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = MaintenanceSettings.Load(paths);
            var now = clock.UtcNow;
            var due = lastRun is null
                      || now - lastRun.Value >= TimeSpan.FromMinutes(settings.IntervalMinutes);
            if (due)
            {
                var result = await maintenance.RunOnceAsync(settings, cancellationToken).ConfigureAwait(false);
                ServiceLog.Write(paths, result);
                lastRun = clock.UtcNow;
                if (once)
                {
                    return;
                }
            }

            var remaining = lastRun is null
                ? TimeSpan.FromSeconds(15)
                : TimeSpan.FromMinutes(settings.IntervalMinutes) - (clock.UtcNow - lastRun.Value);
            var wait = remaining < TimeSpan.FromSeconds(15)
                ? TimeSpan.FromSeconds(15)
                : remaining > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : remaining;
            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

internal static class ServiceLog
{
    public static void Write(AppPaths paths, MaintenanceRunResult result)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            var file = Path.Combine(paths.LogsDirectory, "service.log");
            var builder = new System.Text.StringBuilder();
            builder.Append(result.RanAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.Append(result.Cancelled ? " CANCELLED" : result.Succeeded ? " OK" : " WARN");
            builder.AppendLine();
            foreach (var step in result.Steps)
            {
                builder.Append("  ").AppendLine(step);
            }

            foreach (var warning in result.Warnings)
            {
                builder.Append("  ! ").AppendLine(warning);
            }

            File.AppendAllText(file, builder.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
