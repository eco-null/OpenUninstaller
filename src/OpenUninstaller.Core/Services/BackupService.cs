using System.IO.Compression;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Captures registry keys and files before deletion. Deletion code must only remove items that were
/// captured successfully.
/// </summary>
public sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly IRegistryStore _registry;
    private readonly IProcessRunner _runner;
    private readonly IClock _clock;
    private readonly IOperationLog _log;
    private readonly PathGuard _pathGuard;

    public BackupService(
        AppPaths paths,
        IRegistryStore registry,
        IProcessRunner runner,
        IClock clock,
        IOperationLog log,
        PathGuard pathGuard)
    {
        _paths = paths;
        _registry = registry;
        _runner = runner;
        _clock = clock;
        _log = log;
        _pathGuard = pathGuard;
    }

    public string RootDirectory => _paths.BackupsDirectory;

    internal Action<string, string>? RestoreTestHook { get; set; }

    public async Task<BackupCreationResult> CreateAsync(
        string programName,
        IReadOnlyList<LeftoverItem> items,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var id = $"{now:yyyyMMdd-HHmmss}-{Hashing.Slug(programName)}-{Guid.NewGuid():N}";
        var directory = Path.Combine(RootDirectory, id);
        var registryDirectory = Path.Combine(directory, "registry");
        var staging = Path.Combine(directory, ".staging");
        var warnings = new List<string>();
        var captured = new HashSet<string>(StringComparer.Ordinal);
        var manifest = new BackupManifest
        {
            Id = id,
            CreatedAt = now,
            ProgramName = programName,
            AppVersion = _paths.AppVersion
        };

        try
        {
            System.IO.Directory.CreateDirectory(registryDirectory);
            System.IO.Directory.CreateDirectory(staging);

            var index = 0;
            var registryItems = items
                .Where(i => i.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue
                    or LeftoverKind.Service or LeftoverKind.PathEntry or LeftoverKind.FirewallRule)
                .Where(i => i.RegistryLocation is not null)
                .GroupBy(i => i.RegistryLocation!.Value)
                .ToList();

            foreach (var group in registryItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                var location = group.Key;
                var representative = group.First();

                var isService = group.Any(i => i.Kind == LeftoverKind.Service);
                var isPath = group.Any(i => i.Kind == LeftoverKind.PathEntry);
                var isFirewall = group.Any(i => i.Kind == LeftoverKind.FirewallRule);
                var isValueOnly = !isService && !isPath
                    && group.All(i => i.Kind is LeftoverKind.RegistryValue or LeftoverKind.FirewallRule);
                var isAppCompat = group.Any(i => i.IsAppCompatEntry);
                var isAssociation = group.Any(i => i.IsAssociationEntry);
                var isCom = group.Any(i => i.IsComEntry);
                var guard = isService
                    ? RegistryGuard.ValidateForDeletion(location, allowServiceEntry: true)
                    : isPath
                        ? RegistryGuard.ValidatePathEntryForDeletion(
                            location,
                            representative.ValueName,
                            representative.PathSegment,
                            _paths,
                            representative.IsMachinePath)
                        : isFirewall
                            ? RegistryGuard.ValidateFirewallRuleValue(
                                location, representative.ValueName, _paths.FirewallRulesRoot)
                            : isValueOnly
                                ? RegistryGuard.ValidateValueForDeletion(location)
                                : RegistryGuard.ValidateForDeletion(
                                    location,
                                    allowAppCompatEntry: isAppCompat,
                                    allowAssociationEntry: isAssociation,
                                    allowComEntry: isCom);
                if (!guard.Allowed)
                {
                    warnings.Add($"{representative.DisplayPath}: {guard.Reason}");
                    continue;
                }

                if (isValueOnly)
                {
                    var missing = group
                        .Where(i => string.IsNullOrWhiteSpace(i.ValueName)
                                    || _registry.GetValue(location, i.ValueName) is null)
                        .ToList();
                    foreach (var item in missing)
                    {
                        warnings.Add($"{item.DisplayPath}: the value no longer exists.");
                    }

                    if (missing.Count > 0)
                    {
                        continue;
                    }
                }

                var fileName = $"{index:000}-{Hashing.Slug(representative.DisplayPath)}.reg";
                var exportPath = Path.Combine(registryDirectory, fileName);
                _log.Write("backup", "export-registry", representative.DisplayPath, "started");

                var result = await _runner.RunAsync(
                    RegExePath(),
                    $"export \"{location.HiveName}\\{location.Path}\" \"{exportPath}\" /y{RegistryViewArgument(location.View)}",
                    shellExecute: false,
                    TimeSpan.FromSeconds(60),
                    cancellationToken).ConfigureAwait(false);

                if (!result.Started || result.ExitCode != 0 || !File.Exists(exportPath))
                {
                    warnings.Add($"{representative.DisplayPath}: registry export failed. The item will not be deleted.");
                    _log.Write("backup", "export-registry", representative.DisplayPath, "failed", result.Error ?? $"exit={result.ExitCode}");
                    continue;
                }

                var backupEntry = new BackupRegistryEntry
                {
                    Hive = location.HiveName,
                    View = location.View.ToString(),
                    Path = location.Path,
                    ExportFile = $"registry/{fileName}",
                    Sha256 = Hashing.Sha256File(exportPath),
                    Values = group
                        .Where(i => isValueOnly || isPath)
                        .Where(i => (i.Kind is LeftoverKind.RegistryValue or LeftoverKind.FirewallRule
                                     or LeftoverKind.PathEntry)
                                    && i.ValueName is not null)
                        .Select(i => i.ValueName!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
                if (!TryPrepareRegistryImport(backupEntry, exportPath, out _, out var exportError))
                {
                    warnings.Add($"{representative.DisplayPath}: registry export cannot be restored safely ({exportError}). The item will not be deleted.");
                    continue;
                }
                manifest.Registry.Add(backupEntry);
                foreach (var item in group)
                {
                    captured.Add(item.Id);
                }

                _log.Write("backup", "export-registry", representative.DisplayPath, "completed");
            }

            var fileEntries = items
                .Where(i => i.Kind is not (LeftoverKind.RegistryKey or LeftoverKind.RegistryValue
                    or LeftoverKind.Service or LeftoverKind.PathEntry or LeftoverKind.FirewallRule))
                .ToList();
            foreach (var item in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = item.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    warnings.Add($"{item.DisplayPath}: missing file path.");
                    continue;
                }

                var isTask = item.Kind == LeftoverKind.ScheduledTask;
                var guard = isTask
                    ? _pathGuard.ValidateJunkItem(path, _paths.ScheduledTasksDirectory)
                    : _pathGuard.ValidateForDeletion(path, recursive: item.Kind == LeftoverKind.Directory);
                if (!guard.Allowed)
                {
                    warnings.Add($"{item.DisplayPath}: {guard.Reason}");
                    continue;
                }

                var prefix = ArchivePrefix(path);
                var destination = Path.Combine(staging, prefix.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    var bytes = CopyToStaging(path, destination, cancellationToken);
                    manifest.Files.Add(new BackupFileEntry
                    {
                        OriginalPath = path,
                        Kind = isTask
                            ? "scheduled-task"
                            : item.Kind == LeftoverKind.Directory ? "directory" : "file",
                        ArchivePrefix = prefix,
                        Bytes = bytes.Bytes,
                        ItemCount = bytes.Items,
                        TaskName = isTask ? item.TaskName : null
                    });
                    captured.Add(item.Id);
                    _log.Write("backup", "copy", path, "completed");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{path}: could not be backed up ({ex.Message}). The item will not be deleted.");
                    _log.Write("backup", "copy", path, "failed", ex.Message);
                    TryDeleteDirectory(destination);
                }
            }

            var archivePath = Path.Combine(directory, "files.zip");
            if (manifest.Files.Count > 0)
            {
                ZipFile.CreateFromDirectory(staging, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
                manifest = manifest with { ArchiveSha256 = Hashing.Sha256File(archivePath) };
            }

            TryDeleteDirectory(staging);

            var complete = warnings.Count == 0;
            manifest = manifest with { Complete = complete };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));

            if (manifest.Registry.Count == 0 && manifest.Files.Count == 0)
            {
                TryDeleteDirectory(directory);
                return new BackupCreationResult
                {
                    Succeeded = false,
                    BackupId = id,
                    Directory = directory,
                    Manifest = manifest,
                    Warnings = warnings
                };
            }

            _log.Write("backup", "create", directory, complete ? "completed" : "partial", $"{manifest.Registry.Count} keys, {manifest.Files.Count} file items");

            return new BackupCreationResult
            {
                Succeeded = true,
                BackupId = id,
                Directory = directory,
                Manifest = manifest,
                CapturedItemIds = captured,
                Warnings = warnings
            };
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public IReadOnlyList<BackupRecord> List()
    {
        if (!System.IO.Directory.Exists(RootDirectory))
        {
            return [];
        }

        var records = new List<BackupRecord>();
        foreach (var directory in System.IO.Directory.EnumerateDirectories(RootDirectory))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is not null)
                {
                    records.Add(new BackupRecord { Manifest = manifest, Directory = directory });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return records.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public BackupRecord? Get(string id)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        var directory = Path.Combine(RootDirectory, id);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), JsonOptions);
            return manifest is null ? null : new BackupRecord { Manifest = manifest, Directory = directory };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Explains what a restore would do without changing anything. Missing or damaged backup
    /// entries are reported as blocked rather than silently skipped.
    /// </summary>
    public RestorePreview PreviewRestore(string backupId, bool overwrite)
    {
        var preview = new RestorePreview { BackupId = backupId, Overwrite = overwrite };
        var record = Get(backupId);
        if (record is null)
        {
            preview.Warnings.Add("Backup not found or its manifest is unreadable.");
            return preview;
        }

        var manifest = record.Manifest;
        foreach (var entry in manifest.Registry)
        {
            var target = $"{entry.Hive}\\{entry.Path}";
            var exportPath = Path.Combine(record.Directory, entry.ExportFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(exportPath))
            {
                preview.Entries.Add(new RestorePreviewEntry(target, "Registry key", RestorePreviewAction.Blocked, "The registry export is missing from the backup."));
                continue;
            }

            if (!string.Equals(Hashing.Sha256File(exportPath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                preview.Entries.Add(new RestorePreviewEntry(target, "Registry key", RestorePreviewAction.Blocked, "The registry export hash does not match the manifest."));
                continue;
            }

            if (!TryPrepareRegistryImport(entry, exportPath, out _, out var registryError))
            {
                preview.Entries.Add(new RestorePreviewEntry(target, "Registry key", RestorePreviewAction.Blocked, registryError));
                continue;
            }

            var location = TryParseLocation(entry);
            var exists = location is not null && _registry.KeyExists(location.Value);
            preview.Entries.Add(new RestorePreviewEntry(
                target,
                "Registry key",
                RestorePreviewAction.Restore,
                exists ? "Import will merge this key; matching values are overwritten." : "Import will recreate this key."));
        }

        var archivePath = Path.Combine(record.Directory, "files.zip");
        var archiveUsable = manifest.Files.Count == 0;
        if (manifest.Files.Count > 0)
        {
            if (!File.Exists(archivePath))
            {
                preview.Warnings.Add("The file archive is missing; file entries are blocked.");
            }
            else if (manifest.ArchiveSha256 is { Length: > 0 } expected
                     && !string.Equals(Hashing.Sha256File(archivePath), expected, StringComparison.OrdinalIgnoreCase))
            {
                preview.Warnings.Add("The file archive hash does not match the manifest; file entries are blocked.");
            }
            else
            {
                archiveUsable = true;
            }
        }

        foreach (var file in manifest.Files)
        {
            var isTask = file.Kind == "scheduled-task";
            var target = isTask ? file.TaskName ?? file.OriginalPath : file.OriginalPath;
            var kind = isTask ? "Scheduled task" : file.Kind == "directory" ? "Folder" : "File";
            if (!archiveUsable)
            {
                preview.Entries.Add(new RestorePreviewEntry(target, kind, RestorePreviewAction.Blocked, "The file archive is missing or damaged.", file.Bytes));
                continue;
            }

            if (isTask)
            {
                if (!ScheduledTaskGuard.ValidateTaskName(file.TaskName, out var taskError))
                {
                    preview.Entries.Add(new RestorePreviewEntry(target, kind, RestorePreviewAction.Blocked, taskError ?? "Invalid task name.", file.Bytes));
                    continue;
                }

                var taskPath = ScheduledTaskGuard.XmlPath(_paths.ScheduledTasksDirectory, file.TaskName!);
                preview.Entries.Add(new RestorePreviewEntry(
                    target,
                    kind,
                    File.Exists(taskPath)
                        ? overwrite ? RestorePreviewAction.Overwrite : RestorePreviewAction.Skip
                        : RestorePreviewAction.Restore,
                    File.Exists(taskPath) ? "The task already exists." : "The task will be recreated from its XML.",
                    file.Bytes));
                continue;
            }

            var guard = _pathGuard.ValidateForDeletion(file.OriginalPath, recursive: file.Kind == "directory");
            if (!guard.Allowed)
            {
                preview.Entries.Add(new RestorePreviewEntry(target, kind, RestorePreviewAction.Blocked, guard.Reason ?? "The target is not allowed.", file.Bytes));
                continue;
            }

            var targetExists = File.Exists(file.OriginalPath) || Directory.Exists(file.OriginalPath);
            if (file.Kind == "directory" && (overwrite || !targetExists)
                && !ValidateArchiveTargets(archivePath, file, out var targetError))
            {
                preview.Entries.Add(new RestorePreviewEntry(target, kind, RestorePreviewAction.Blocked, targetError, file.Bytes));
                continue;
            }

            var exists = targetExists;
            preview.Entries.Add(new RestorePreviewEntry(
                target,
                kind,
                exists
                    ? overwrite ? RestorePreviewAction.Overwrite : RestorePreviewAction.Skip
                    : RestorePreviewAction.Restore,
                exists ? "The target already exists." : "The target does not exist.",
                file.Bytes));
        }

        return preview;
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupId,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var lifecycle = TryAcquireBackupLock(backupId);
        if (lifecycle is null)
            return new RestoreResult { BackupId = backupId, Succeeded = false, Warnings = { "Backup is unavailable or in use." } };
        var record = Get(backupId);
        if (record is null)
        {
            return new RestoreResult
            {
                BackupId = backupId,
                Succeeded = false,
                Duration = stopwatch.Elapsed,
                Warnings = { "Backup not found or its manifest is unreadable." }
            };
        }

        var result = new RestoreResult { BackupId = backupId, Succeeded = false, Duration = stopwatch.Elapsed };
        var manifest = record.Manifest;

        foreach (var entry in manifest.Registry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exportPath = Path.Combine(record.Directory, entry.ExportFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(exportPath))
            {
                result.Entries.Add(new RestoreEntryResult($"{entry.Hive}\\{entry.Path}", RestoreEntryStatus.Failed, "Backup file is missing."));
                continue;
            }

            if (!string.Equals(Hashing.Sha256File(exportPath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Entries.Add(new RestoreEntryResult($"{entry.Hive}\\{entry.Path}", RestoreEntryStatus.Failed, "Backup file hash mismatch."));
                continue;
            }

            if (!TryPrepareRegistryImport(entry, exportPath, out var registryText, out var registryError))
            {
                result.Entries.Add(new RestoreEntryResult($"{entry.Hive}\\{entry.Path}", RestoreEntryStatus.Failed, registryError));
                continue;
            }

            var importPath = Path.Combine(Path.GetTempPath(), "openuninstaller-reg-" + Guid.NewGuid().ToString("N") + ".reg");
            ProcessResult run;
            try
            {
                File.WriteAllText(importPath, registryText, Encoding.Unicode);
                var viewArg = RegistryViewArgument(TryParseLocation(entry)!.Value.View);
                run = await _runner.RunAsync(RegExePath(), $"import \"{importPath}\"{viewArg}",
                    shellExecute: false, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(importPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            var restoredKey = TryParseLocation(entry);
            var verified = run.Started && run.ExitCode == 0
                           && restoredKey is not null
                           && _registry.KeyExists(restoredKey.Value);
            var status = verified ? RestoreEntryStatus.Restored : RestoreEntryStatus.Failed;
            result.Entries.Add(new RestoreEntryResult(
                $"{entry.Hive}\\{entry.Path}",
                status,
                status == RestoreEntryStatus.Restored
                    ? null
                    : run.Error ?? $"reg import exited with {run.ExitCode} and the key was not found afterwards."));
            _log.Write("restore", "import-registry", $"{entry.Hive}\\{entry.Path}", status.ToString());
        }

        if (manifest.Files.Count > 0)
        {
            var archivePath = Path.Combine(record.Directory, "files.zip");
            if (!File.Exists(archivePath))
            {
                result.Warnings.Add("The file archive is missing; files were not restored.");
            }
            else if (manifest.ArchiveSha256 is { Length: > 0 } expected
                     && !string.Equals(Hashing.Sha256File(archivePath), expected, StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add("The file archive hash does not match the manifest; files were not restored.");
            }
            else
            {
                var extraction = Path.Combine(Path.GetTempPath(), "openuninstaller-restore-" + Guid.NewGuid().ToString("N"));
                try
                {
                    System.IO.Directory.CreateDirectory(extraction);
                    ZipFile.ExtractToDirectory(archivePath, extraction, overwriteFiles: true);

                    foreach (var fileEntry in manifest.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var source = Path.GetFullPath(Path.Combine(extraction, fileEntry.ArchivePrefix.Replace('/', Path.DirectorySeparatorChar)));
                        if (!PathGuard.IsUnder(source, extraction))
                        {
                            result.Entries.Add(new RestoreEntryResult(fileEntry.OriginalPath, RestoreEntryStatus.Failed, "Invalid archive prefix."));
                            continue;
                        }
                        var target = fileEntry.OriginalPath;
                        if (fileEntry.Kind == "scheduled-task")
                        {
                            await RestoreScheduledTaskAsync(fileEntry, source, overwrite, result, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        var guard = _pathGuard.ValidateForDeletion(target, recursive: fileEntry.Kind == "directory");
                        if (!guard.Allowed)
                        {
                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Failed, guard.Reason));
                            continue;
                        }

                        if (!overwrite && (File.Exists(target) || System.IO.Directory.Exists(target)))
                        {
                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Skipped, "Target exists. Enable overwrite to restore."));
                            continue;
                        }

                        if (fileEntry.Kind == "directory" && !ValidateArchiveTargets(archivePath, fileEntry, out var targetError))
                        {
                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Failed, targetError));
                            continue;
                        }

                        try
                        {
                            RestoreTestHook?.Invoke("before-pin", target);
                            using var pinned = new PinnedRestore(_pathGuard, RestoreTestHook, cancellationToken);
                            pinned.Restore(source, target, overwrite);

                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Restored));
                            _log.Write("restore", "copy", target, "restored");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
                        {
                            var detail = ex is DllNotFoundException or EntryPointNotFoundException
                                ? "Transactional filesystem restore is unavailable on this system."
                                : ex.Message;
                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Failed, detail));
                            _log.Write("restore", "copy", target, "failed", ex.Message);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    result.Warnings.Add($"Could not extract the file archive: {ex.Message}");
                }
                finally
                {
                    TryDeleteDirectory(extraction);
                }
            }
        }

        result.Succeeded = result.Entries.Count > 0
                           && result.FailedCount == 0
                           && result.Warnings.Count == 0
                           && result.Entries.All(e => e.Status == RestoreEntryStatus.Restored);
        return result;
    }

    public bool Delete(string id)
    {
        if (!IsSafeId(id))
        {
            return false;
        }

        using var lifecycle = TryAcquireBackupLock(id);
        if (lifecycle is null) return false;
        var directory = Path.Combine(RootDirectory, id);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            _log.Write("backup", "delete", directory, "deleted");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("backup", "delete", directory, "failed", ex.Message);
            return false;
        }
    }

    public int Prune(int keep)
    {
        if (keep < 1)
        {
            keep = 1;
        }

        var records = List();
        var removed = 0;
        foreach (var record in records.Skip(keep))
        {
            if (Delete(Path.GetFileName(record.Directory)))
            {
                removed++;
                _log.Write("backup", "prune", record.Directory, "deleted");
            }
        }

        return removed;
    }

    private async Task RestoreScheduledTaskAsync(
        BackupFileEntry entry,
        string source,
        bool overwrite,
        RestoreResult result,
        CancellationToken cancellationToken)
    {
        var label = entry.TaskName ?? entry.OriginalPath;
        if (string.IsNullOrWhiteSpace(entry.TaskName))
        {
            result.Entries.Add(new RestoreEntryResult(label, RestoreEntryStatus.Failed, "The backup has no task name."));
            return;
        }

        if (!File.Exists(source))
        {
            result.Entries.Add(new RestoreEntryResult(label, RestoreEntryStatus.Failed, "The task XML is missing from the archive."));
            return;
        }

        if (!ScheduledTaskGuard.ValidateTaskName(entry.TaskName, out var nameError))
        {
            result.Entries.Add(new RestoreEntryResult(label, RestoreEntryStatus.Failed, nameError));
            return;
        }

        if (!overwrite && File.Exists(ScheduledTaskGuard.XmlPath(_paths.ScheduledTasksDirectory, entry.TaskName)))
        {
            result.Entries.Add(new RestoreEntryResult(
                label,
                RestoreEntryStatus.Skipped,
                "Target exists. Enable overwrite to restore."));
            return;
        }

        var run = await _runner.RunAsync(
            SchtasksExePath(),
            $"/Create /TN \"{entry.TaskName}\" /XML \"{source}\" /F",
            shellExecute: false,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

        var restored = run.Started
                       && run.ExitCode == 0
                       && File.Exists(Path.Combine(
                           _paths.ScheduledTasksDirectory,
                           entry.TaskName.Replace('\\', Path.DirectorySeparatorChar)));
        result.Entries.Add(new RestoreEntryResult(
            label,
            restored ? RestoreEntryStatus.Restored : RestoreEntryStatus.Failed,
            restored ? null : run.Error ?? $"schtasks exited with {run.ExitCode}."));
        _log.Write("restore", "create-scheduled-task", label, restored ? "Restored" : "Failed");
    }

    private static string ArchivePrefix(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var drive = root.TrimEnd(':', '\\');
        if (drive.Length == 0)
        {
            drive = "root";
        }

        var remainder = full[root.Length..];
        return (drive + "/" + remainder.Replace('\\', '/')).Trim('/');
    }

    private static RegistryLocation? TryParseLocation(BackupRegistryEntry entry)
    {
        if (!Enum.TryParse<RegistryHive>(entry.Hive == "HKLM" ? "LocalMachine" : entry.Hive == "HKCU" ? "CurrentUser" : entry.Hive, true, out var hive)
            || !Enum.TryParse<RegistryView>(entry.View, true, out var view))
        {
            return null;
        }

        return new RegistryLocation(hive, view, entry.Path);
    }

    private static (long Bytes, int Items) CopyToStaging(string source, string destination, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var count = 0;
        void Copy(string current, string target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Directory contains a reparse point; it cannot be backed up safely.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                    Copy(child, Path.Combine(target, Path.GetFileName(child)));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(current, target, overwrite: true);
                bytes += new FileInfo(target).Length;
                count++;
            }
        }
        Copy(source, destination);
        return (bytes, count);
    }

    private bool ValidateArchiveTargets(string archivePath, BackupFileEntry file, out string error)
    {
        error = "The archive contains an unsafe target.";
        try
        {
            var prefix = file.ArchivePrefix.Replace('\\', '/').TrimEnd('/') + "/";
            if (prefix.StartsWith('/') || prefix[..^1].Split('/').Any(part => part is "" or "." or ".." || part.Contains(':')))
                return false;
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (entries.Count == 0) return false;
            foreach (var item in entries)
            {
                var relative = item.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(file.OriginalPath, relative));
                if (relative.Length == 0) continue; // The ZIP entry for an empty root.
                if (!PathGuard.IsUnder(target, file.OriginalPath) || !_pathGuard.ValidateForDeletion(target, recursive: false).Allowed)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Lock files stay outside backup directories so their identity survives deletion. FileShare.None
    // provides non-thread-affine, cross-process ownership for the whole async restore.
    private FileStream? TryAcquireBackupLock(string id)
    {
        if (!IsSafeId(id)) return null;
        try
        {
            var locks = Path.Combine(RootDirectory, ".locks");
            Directory.CreateDirectory(locks);
            return new FileStream(Path.Combine(locks, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string RegistryViewArgument(RegistryView view) => view switch
    {
        RegistryView.Registry32 => " /reg:32",
        RegistryView.Registry64 => " /reg:64",
        _ => ""
    };

    // Parse the export before importing it. For value backups reg.exe exported the entire key;
    // emit only the explicitly captured values, even when the export includes siblings/subkeys.
    private static bool TryPrepareRegistryImport(BackupRegistryEntry entry, string path, out string output, out string error)
    {
        output = "";
        error = "The registry export is invalid or outside the declared backup scope.";
        var location = TryParseLocation(entry);
        if (location is null || !Enum.IsDefined(location.Value.View) ||
            string.IsNullOrWhiteSpace(entry.Path) || entry.Path.StartsWith('\\') ||
            entry.Path.EndsWith('\\') || entry.Path.Contains("\\\\", StringComparison.Ordinal)) return false;
        var hive = location.Value.Hive switch
        {
            RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
            RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
            RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
            RegistryHive.Users => "HKEY_USERS",
            RegistryHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
            _ => ""
        };
        if (hive.Length == 0) return false;
        var declared = hive + "\\" + entry.Path;
        // Older backups exported a 32-bit view using an explicit WOW6432Node path.
        // Normalize only that exact legacy root and its descendants to the logical view.
        var legacy = location.Value.View == RegistryView.Registry32
            ? hive + "\\" + location.Value.RegExePath[(location.Value.RegExePath.IndexOf('\\') + 1)..]
            : null;
        var valueOnly = entry.Values.Count > 0;
        var wanted = new HashSet<string>(entry.Values, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !string.Equals(lines[0].TrimStart('\ufeff'), "Windows Registry Editor Version 5.00", StringComparison.Ordinal)) return false;
        var result = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        var inKey = false;
        var exactKey = false;
        var sawRoot = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.StartsWith("[-", StringComparison.Ordinal)) return false;
                var key = line[1..^1];
                if (legacy is not null && (string.Equals(key, legacy, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith(legacy + "\\", StringComparison.OrdinalIgnoreCase)))
                    key = declared + key[legacy.Length..];
                exactKey = string.Equals(key, declared, StringComparison.OrdinalIgnoreCase);
                if (!exactKey && !key.StartsWith(declared + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains('[') || key.Contains(']')) return false;
                inKey = true;
                sawRoot |= exactKey;
                if (!valueOnly || exactKey) result.Append('[').Append(key).Append("]\r\n");
                continue;
            }
            if (!inKey) return false;
            var match = Regex.Match(line, "^(?:@|\"((?:[^\"\\\\]|\\\\[\\\\\"])*)\")=(.*)$");
            if (!match.Success) return false;
            var name = line.StartsWith('@') ? "" : match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            var data = match.Groups[2].Value;
            if (data == "-" || !(data.StartsWith('"') || Regex.IsMatch(data, @"^(?:dword|hex(?:\([0-9a-fA-F]+\))?):", RegexOptions.IgnoreCase))) return false;
            var record = new StringBuilder(line);
            while (line.EndsWith('\\'))
            {
                if (++i >= lines.Length) return false;
                line = lines[i].Trim();
                if (!Regex.IsMatch(line, @"^[0-9a-fA-F,\\\s]*$")) return false;
                record.Append("\r\n  ").Append(line);
            }
            if (!valueOnly || (exactKey && wanted.Contains(name)))
            {
                result.Append(record).Append("\r\n");
                if (exactKey) found.Add(name);
            }
        }
        if (!sawRoot || (valueOnly && !wanted.IsSubsetOf(found))) return false;
        output = result.ToString();
        return true;
    }

    private static string RegExePath()
    {
        var system = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(system) ? "reg.exe" : Path.Combine(system, "reg.exe");
    }

    private static string SchtasksExePath()
    {
        var system = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(system) ? "schtasks.exe" : Path.Combine(system, "schtasks.exe");
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSafeId(string id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Length <= 100
           && id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
           && !id.Contains("..", StringComparison.Ordinal);
}
