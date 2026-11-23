using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.Core.Services;

public sealed record MaintenanceRunResult
{
    public required DateTimeOffset RanAt { get; init; }
    public List<string> Steps { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Cancelled { get; init; }
    public bool Succeeded => Warnings.Count == 0 && !Cancelled;
}

/// <summary>
/// One maintenance pass shared by the desktop "run now" action, the CLI and the Windows service:
/// prune backups, expire logs and scan snapshots, and optionally clean allow-listed junk.
/// Every step is individually reported; one failure never stops the rest.
/// </summary>
public sealed class MaintenanceService
{
    private readonly AppPaths _paths;
    private readonly BackupService _backups;
    private readonly JunkService _junk;
    private readonly IClock _clock;
    private readonly IOperationLog _log;

    public MaintenanceService(
        AppPaths paths,
        BackupService backups,
        JunkService junk,
        IClock clock,
        IOperationLog log)
    {
        _paths = paths;
        _backups = backups;
        _junk = junk;
        _clock = clock;
        _log = log;
    }

    public Task<MaintenanceRunResult> RunOnceAsync(
        MaintenanceSettings settings,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            settings.Normalize();
            var result = new MaintenanceRunResult { RanAt = _clock.UtcNow };
            if (!settings.Enabled)
            {
                result.Warnings.Add(L("MaintenanceIsDisabledInMaintenanceJson"));
                return result;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pruned = _backups.Prune(settings.KeepBackups);
                result.Steps.Add(F("BackupsKeptRemoved", settings.KeepBackups, pruned));

                var expiredLogs = ExpireFiles(
                    _paths.LogsDirectory,
                    "*.jsonl",
                    settings.LogRetentionDays,
                    _log.FilePath);
                // The service keeps its own logs next to its binaries, outside the user-writable tree.
                var ownLogs = Path.GetDirectoryName(_log.FilePath);
                if (ownLogs is not null && !string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownLogs)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.LogsDirectory)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    expiredLogs += ExpireFiles(ownLogs, "*.jsonl", settings.LogRetentionDays, _log.FilePath);
                }
                result.Steps.Add(F("LogsRemovedOlderThanDayS", expiredLogs, settings.LogRetentionDays));

                var expiredSnapshots = ExpireFiles(
                    Path.Combine(_paths.DataDirectory, "ScanHistory"),
                    "*.json",
                    settings.ScanHistoryRetentionDays,
                    keep: null);
                result.Steps.Add(
                    F("ScanHistoryRemovedOlderThanDayS", expiredSnapshots, settings.ScanHistoryRetentionDays));

                if (settings.CleanJunk && settings.JunkCategories.Count > 0)
                {
                    var cleanup = _junk.Clean(settings.JunkCategories, dryRun: false, cancellationToken);
                    var detail = F("JunkFreed", InstalledProgram.FormatBytes(cleanup.FreedBytes)) +
                                 F("ItemSFailed", cleanup.DeletedItems, cleanup.FailedItems);
                    result.Steps.Add(detail);
                    result.Warnings.AddRange(cleanup.Messages);
                }

                _log.Write("maintenance", "run", _paths.DataDirectory, result.Warnings.Count == 0 ? "completed" : "partial",
                    string.Join(" | ", result.Steps));
                return result;
            }
            catch (OperationCanceledException)
            {
                return result with { Cancelled = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add(ex.Message);
                _log.Write("maintenance", "run", _paths.DataDirectory, "failed", ex.Message);
                return result;
            }
        }, cancellationToken);
    }

    /// <summary>Expires audit files without ever touching the current operation log.</summary>
    private static int ExpireFiles(string directory, string pattern, int retentionDays, string? keep)
    {
        if (!Directory.Exists(directory) || retentionDays < 1)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                {
                    continue;
                }

                VerifiedFileSystem.DeleteFile(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
