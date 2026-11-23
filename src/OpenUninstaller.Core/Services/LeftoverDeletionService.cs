using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// The only service allowed to delete leftovers. Every item passes the guard and must appear in the
/// backup's captured set; otherwise it is skipped.
/// </summary>
public sealed class LeftoverDeletionService
{
    private readonly IRegistryStore _registry;
    private readonly PathGuard _pathGuard;
    private readonly BackupService _backup;
    private readonly IOperationLog _log;
    private readonly IProcessRunner _runner;
    private readonly AppPaths _paths;

    public LeftoverDeletionService(
        IRegistryStore registry,
        PathGuard pathGuard,
        BackupService backup,
        IOperationLog log,
        IProcessRunner runner,
        AppPaths paths)
    {
        _registry = registry;
        _pathGuard = pathGuard;
        _backup = backup;
        _log = log;
        _runner = runner;
        _paths = paths;
    }

    public async Task<LeftoverDeletionResult> DeleteAsync(
        string programName,
        IReadOnlyList<LeftoverItem> items,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LeftoverDeletionResult
        {
            ProgramName = programName,
            DryRun = dryRun
        };

        if (items.Count == 0)
        {
            result.Warnings.Add(L("NoItemsWereSelected"));
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        if (dryRun)
        {
            foreach (var item in items)
            {
                var (allowed, reason) = Validate(item);
                result.Items.Add(new DeletionItemResult(
                    item.DisplayPath,
                    item.Kind,
                    allowed ? DeletionStatus.WouldDelete : DeletionStatus.Protected,
                    allowed ? L("BackupAndDeleteWouldRun") : reason));
            }

            result.Duration = stopwatch.Elapsed;
            _log.Write("delete", "dry-run", programName, "completed", $"{items.Count} item(s)");
            return result;
        }

        // Review protections include ownership and boot-driver evidence that a path/registry
        // allow-list cannot reconstruct. Enforce them before capturing or mutating anything.
        var eligible = items.Where(item => Validate(item).Allowed).ToList();
        if (eligible.Count == 0)
        {
            foreach (var item in items)
            {
                result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind,
                    DeletionStatus.Protected, Validate(item).Reason));
            }

            result.Duration = stopwatch.Elapsed;
            return result;
        }

        var backup = await _backup.CreateAsync(programName, eligible, cancellationToken).ConfigureAwait(false);
        result.BackupId = backup.Succeeded ? backup.BackupId : null;
        result.BackupDirectory = backup.Succeeded ? backup.Directory : null;
        result.Warnings.AddRange(backup.Warnings);

        if (!backup.Succeeded)
        {
            foreach (var item in items)
            {
                var validation = Validate(item);
                result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind,
                    validation.Allowed ? DeletionStatus.BackupFailed : DeletionStatus.Protected,
                    validation.Allowed ? L("BackupWasNotCreatedNothingWasDeleted") : validation.Reason));
            }

            result.Warnings.Add(L("DeletionWasAbortedBecauseNoItemCouldBe"));
            result.Duration = stopwatch.Elapsed;
            _log.Write("delete", "abort", programName, "backup-failed");
            return result;
        }

        var captured = backup.CapturedItemIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (allowed, reason) = Validate(item);
            if (!allowed)
            {
                result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Protected, reason));
                continue;
            }

            if (!captured.Contains(item.Id))
            {
                result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.BackupFailed, L("NotCapturedByTheBackup")));
                continue;
            }

            try
            {
                _log.Write("delete", "start", item.DisplayPath, "started", item.Kind.ToString());
                if ((item.Kind is LeftoverKind.RegistryKey or LeftoverKind.Service) && item.RegistryLocation is { } location)
                {
                    _registry.DeleteKey(location, recursive: true);
                    if (_registry.KeyExists(location))
                    {
                        result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Failed, L("TheKeyStillExists")));
                        continue;
                    }
                }
                else if (item.Kind is LeftoverKind.RegistryValue or LeftoverKind.FirewallRule
                         && item.RegistryLocation is { } valueLocation
                         && item.ValueName is { Length: > 0 } valueName)
                {
                    _registry.DeleteValue(valueLocation, valueName);
                    if (_registry.GetValue(valueLocation, valueName) is not null)
                    {
                        result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Failed, L("TheValueStillExists")));
                        continue;
                    }
                }
                else if (item.Kind == LeftoverKind.PathEntry
                         && item.RegistryLocation is { } pathLocation
                         && item.PathSegment is { Length: > 0 } segment)
                {
                    if (!RemovePathSegment(pathLocation, segment))
                    {
                        result.Items.Add(new DeletionItemResult(
                            item.DisplayPath, item.Kind, DeletionStatus.Failed,
                            L("ThePATHSegmentCouldNotBeRemovedSafely")));
                        continue;
                    }
                }
                else if (item.Kind == LeftoverKind.ScheduledTask)
                {
                    if (!await DeleteScheduledTaskAsync(item, cancellationToken).ConfigureAwait(false))
                    {
                        result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Failed, L("TheScheduledTaskStillExists")));
                        continue;
                    }
                }
                else if (item.FilePath is { } path)
                {
                    // Verified handles: when the app runs elevated, a junction swapped in after
                    // validation cannot redirect the deletion, and read-only files are cleared on
                    // the handle (the item is already backed up).
                    if (Directory.Exists(path))
                    {
                        VerifiedFileSystem.DeleteTree(path);
                    }
                    else if (File.Exists(path))
                    {
                        VerifiedFileSystem.DeleteFile(path);
                    }

                    if (Directory.Exists(path) || File.Exists(path))
                    {
                        result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Failed, L("ThePathStillExists")));
                        continue;
                    }
                }
                else
                {
                    result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Failed, L("ItemHasNoTarget")));
                    continue;
                }

                result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Deleted));
                _log.Write("delete", "complete", item.DisplayPath, "deleted");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Items.Add(new DeletionItemResult(item.DisplayPath, item.Kind, DeletionStatus.Failed, ex.Message));
                _log.Write("delete", "failed", item.DisplayPath, "failed", ex.Message);
            }
        }

        result.Duration = stopwatch.Elapsed;
        _log.Write("delete", "finish", programName, result.Succeeded ? "completed" : "partial",
            $"deleted={result.DeletedCount} failed={result.FailedCount} skipped={result.SkippedCount}");
        return result;
    }

    private (bool Allowed, string? Reason) Validate(LeftoverItem item)
    {
        if (item.IsProtected)
        {
            return (false, item.ProtectedReason);
        }

        if (item.Kind == LeftoverKind.PathEntry)
        {
            if (item.RegistryLocation is not { } pathLocation || string.IsNullOrWhiteSpace(item.PathSegment))
            {
                return (false, L("MissingPATHLocationOrSegment"));
            }

            var pathEntryGuard = RegistryGuard.ValidatePathEntryForDeletion(
                pathLocation, item.ValueName, item.PathSegment, _paths, item.IsMachinePath);
            if (!pathEntryGuard.Allowed)
            {
                return (false, pathEntryGuard.Reason);
            }

            var current = _registry.GetValue(pathLocation, "Path")?.AsString();
            if (current is null
                || !current.Split(';').Any(s => string.Equals(s.Trim(), item.PathSegment, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, L("ThePATHSegmentNoLongerExists"));
            }

            return (true, null);
        }

        if (item.Kind == LeftoverKind.FirewallRule)
        {
            if (item.RegistryLocation is not { } firewallLocation || string.IsNullOrWhiteSpace(item.ValueName))
            {
                return (false, L("MissingFirewallRuleLocationOrValueName"));
            }

            var firewallGuard = RegistryGuard.ValidateFirewallRuleValue(
                firewallLocation, item.ValueName, _paths.FirewallRulesRoot);
            if (!firewallGuard.Allowed)
            {
                return (false, firewallGuard.Reason);
            }

            if (_registry.GetValue(firewallLocation, item.ValueName) is null)
            {
                return (false, L("TheFirewallRuleNoLongerExists"));
            }

            return (true, null);
        }

        if (item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.Service)
        {
            if (item.RegistryLocation is null)
            {
                return (false, L("MissingRegistryLocation"));
            }

            var registryGuard = item.Kind switch
            {
                LeftoverKind.Service => RegistryGuard.ValidateForDeletion(
                    item.RegistryLocation.Value, allowServiceEntry: true),
                LeftoverKind.RegistryValue => item.IsFirewallRule
                    ? RegistryGuard.ValidateFirewallRuleValue(
                        item.RegistryLocation.Value, item.ValueName, _paths.FirewallRulesRoot)
                    : RegistryGuard.ValidateValueForDeletion(item.RegistryLocation.Value),
                _ => RegistryGuard.ValidateForDeletion(
                    item.RegistryLocation.Value,
                    allowAppCompatEntry: item.IsAppCompatEntry,
                    allowAssociationEntry: item.IsAssociationEntry,
                    allowComEntry: item.IsComEntry,
                    allowProductRootEntry: item.IsProductRootEntry)
            };
            if (!registryGuard.Allowed)
            {
                return (false, registryGuard.Reason);
            }

            if (item.Kind == LeftoverKind.RegistryValue
                && (string.IsNullOrWhiteSpace(item.ValueName)
                    || _registry.GetValue(item.RegistryLocation.Value, item.ValueName) is null))
            {
                return (false, L("TheRegistryValueNoLongerExists"));
            }

            return (true, null);
        }

        if (item.Kind == LeftoverKind.ScheduledTask)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) || string.IsNullOrWhiteSpace(item.TaskName))
            {
                return (false, L("MissingScheduledTaskPathOrName"));
            }

            var taskGuard = ScheduledTaskGuard.ValidateForDeletion(
                item.TaskName, _paths.ScheduledTasksDirectory, item.FilePath);
            return (taskGuard.Allowed, taskGuard.Reason);
        }

        if (string.IsNullOrWhiteSpace(item.FilePath))
        {
            return (false, L("MissingFilePath2"));
        }

        var pathGuard = _pathGuard.ValidateForDeletion(item.FilePath, recursive: item.Kind == LeftoverKind.Directory);
        return (pathGuard.Allowed, pathGuard.Reason);
    }

    /// <summary>
    /// Rewrites PATH by removing exactly the reviewed segment. It re-reads the value, refuses when
    /// the value changed in a way that no longer contains the segment, and keeps the original value
    /// kind. The whole environment key is already exported by the backup.
    /// </summary>
    private bool RemovePathSegment(RegistryLocation location, string segment)
    {
        var value = _registry.GetValue(location, "Path");
        if (value is null)
        {
            return false;
        }

        var parts = value.AsString()?.Split(';').ToList();
        if (parts is null)
        {
            return false;
        }

        var removed = parts.RemoveAll(p => string.Equals(p.Trim(), segment, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return false;
        }

        var updated = string.Join(';', parts);
        var kind = value.Kind is Microsoft.Win32.RegistryValueKind.ExpandString
            ? Microsoft.Win32.RegistryValueKind.ExpandString
            : Microsoft.Win32.RegistryValueKind.String;
        _registry.SetValue(location, "Path", kind, updated);
        if (_paths.BroadcastEnvironmentChanges)
        {
            EnvironmentBroadcast.Notify();
        }

        return true;
    }

    private async Task<bool> DeleteScheduledTaskAsync(LeftoverItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.TaskName))
        {
            return false;
        }

        var system = Environment.SystemDirectory;
        var schtasks = string.IsNullOrWhiteSpace(system) ? "schtasks.exe" : Path.Combine(system, "schtasks.exe");
        var run = await _runner.RunAsync(
            schtasks,
            $"/Delete /TN \"{item.TaskName}\" /F",
            shellExecute: false,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

        var xml = ScheduledTaskGuard.XmlPath(_paths.ScheduledTasksDirectory, item.TaskName);
        return run.Started && run.ExitCode == 0 && !File.Exists(xml);
    }
}
