using System.IO.Compression;
using System.Text.Json;
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
                        // The value set changed between scanning and backup. Do not export a
                        // partial group and then delete a sibling the user did not review.
                        continue;
                    }
                }

                var fileName = $"{index:000}-{Hashing.Slug(representative.DisplayPath)}.reg";
                var exportPath = Path.Combine(registryDirectory, fileName);
                _log.Write("backup", "export-registry", representative.DisplayPath, "started");

                var result = await _runner.RunAsync(
                    RegExePath(),
                    $"export \"{location.RegExePath}\" \"{exportPath}\" /y",
                    shellExecute: false,
                    TimeSpan.FromSeconds(60),
                    cancellationToken).ConfigureAwait(false);

                if (!result.Started || result.ExitCode != 0 || !File.Exists(exportPath))
                {
                    warnings.Add($"{representative.DisplayPath}: registry export failed. The item will not be deleted.");
                    _log.Write("backup", "export-registry", representative.DisplayPath, "failed", result.Error ?? $"exit={result.ExitCode}");
                    continue;
                }

                manifest.Registry.Add(new BackupRegistryEntry
                {
                    Hive = location.HiveName,
                    View = location.View.ToString(),
                    Path = location.Path,
                    ExportFile = $"registry/{fileName}",
                    Sha256 = Hashing.Sha256File(exportPath),
                    Values = group
                        .Where(i => (i.Kind is LeftoverKind.RegistryValue or LeftoverKind.FirewallRule
                                     or LeftoverKind.PathEntry)
                                    && i.ValueName is not null)
                        .Select(i => i.ValueName!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                });
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

            // The manifest is written even for partial backups so captured items stay restorable.
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
                // Ignore an unreadable backup; it must not break the list.
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

            var exists = File.Exists(file.OriginalPath) || System.IO.Directory.Exists(file.OriginalPath);
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

            var run = await _runner.RunAsync(
                RegExePath(),
                $"import \"{exportPath}\"",
                shellExecute: false,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);

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
                        var source = Path.Combine(extraction, fileEntry.ArchivePrefix.Replace('/', Path.DirectorySeparatorChar));
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

                        try
                        {
                            if (Directory.Exists(source))
                            {
                                CopyTree(source, target, overwrite);
                            }
                            else if (File.Exists(source))
                            {
                                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                File.Copy(source, target, overwrite);
                            }
                            else
                            {
                                result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Failed, "Archive entry is missing."));
                                continue;
                            }

                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Restored));
                            _log.Write("restore", "copy", target, "restored");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            result.Entries.Add(new RestoreEntryResult(target, RestoreEntryStatus.Failed, ex.Message));
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
            if (TryDeleteDirectory(record.Directory))
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
        if (File.Exists(source))
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            return (new FileInfo(destination).Length, 1);
        }

        System.IO.Directory.CreateDirectory(destination);
        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = false,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            bytes += new FileInfo(target).Length;
            count++;
        }

        return (bytes, count);
    }

    private static void CopyTree(string source, string destination, bool overwrite)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
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
