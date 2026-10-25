namespace OpenUninstaller.Core.Models;

public sealed record BackupRegistryEntry
{
    public required string Hive { get; init; }
    public required string View { get; init; }
    public required string Path { get; init; }
    public required string ExportFile { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>Values that were removed individually; the export also contains their siblings.</summary>
    public List<string> Values { get; init; } = [];
}

public sealed record BackupFileEntry
{
    public required string OriginalPath { get; init; }
    public required string Kind { get; init; }
    public required string ArchivePrefix { get; init; }
    public long Bytes { get; init; }
    public int ItemCount { get; init; }

    /// <summary>Task Scheduler path when <see cref="Kind"/> is <c>scheduled-task</c>.</summary>
    public string? TaskName { get; init; }
}

public sealed record BackupManifest
{
    /// <summary>Schema 2 adds value names and scheduled-task metadata; schema 1 stays readable.</summary>
    public int SchemaVersion { get; init; } = 2;
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string ProgramName { get; init; }
    public string? AppVersion { get; init; }
    public bool Complete { get; init; }
    public string? ArchiveSha256 { get; init; }
    public List<BackupRegistryEntry> Registry { get; init; } = [];
    public List<BackupFileEntry> Files { get; init; } = [];
}

public sealed record BackupRecord
{
    public required BackupManifest Manifest { get; init; }
    public required string Directory { get; init; }

    public string Id => Manifest.Id;
    public string ProgramName => Manifest.ProgramName;
    public DateTimeOffset CreatedAt => Manifest.CreatedAt;
    public int RegistryCount => Manifest.Registry.Count;
    public int FileCount => Manifest.Files.Count;
    public long TotalBytes => Manifest.Files.Sum(f => f.Bytes);

    public string Summary =>
        $"{RegistryCount} registry key(s), {FileCount} file item(s), {InstalledProgram.FormatBytes(TotalBytes)}";

    public string CreatedDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CompletenessLabel => Manifest.Complete ? "Complete" : "Partial";
}

public sealed record BackupCreationResult
{
    public required bool Succeeded { get; init; }
    public required string BackupId { get; init; }
    public required string Directory { get; init; }
    public required BackupManifest Manifest { get; init; }
    public IReadOnlyCollection<string> CapturedItemIds { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public enum RestorePreviewAction
{
    Restore,
    Overwrite,
    Skip,
    Blocked
}

public sealed record RestorePreviewEntry(
    string Target,
    string Kind,
    RestorePreviewAction Action,
    string Detail,
    long Bytes = 0)
{
    public string ActionLabel => Action switch
    {
        RestorePreviewAction.Restore => "Restore",
        RestorePreviewAction.Overwrite => "Overwrite",
        RestorePreviewAction.Skip => "Skip",
        _ => "Blocked"
    };
}

public sealed class RestorePreview
{
    public required string BackupId { get; init; }
    public bool Overwrite { get; init; }
    public List<RestorePreviewEntry> Entries { get; } = [];
    public List<string> Warnings { get; } = [];

    public int RestoreCount => Entries.Count(e => e.Action is RestorePreviewAction.Restore or RestorePreviewAction.Overwrite);
    public int SkipCount => Entries.Count(e => e.Action == RestorePreviewAction.Skip);
    public int BlockedCount => Entries.Count(e => e.Action == RestorePreviewAction.Blocked);
    public bool CanRestore => BlockedCount == 0 && RestoreCount > 0;
    public string Summary => $"{RestoreCount} to restore · {SkipCount} skipped · {BlockedCount} blocked";
}

public enum RestoreEntryStatus
{
    Restored,
    Skipped,
    Failed
}

public sealed record RestoreEntryResult(
    string Target,
    RestoreEntryStatus Status,
    string? Detail = null);

public sealed class RestoreResult
{
    public required string BackupId { get; init; }
    public required bool Succeeded { get; set; }
    public TimeSpan Duration { get; init; }
    public List<RestoreEntryResult> Entries { get; } = [];
    public List<string> Warnings { get; } = [];

    public int RestoredCount => Entries.Count(e => e.Status == RestoreEntryStatus.Restored);
    public int FailedCount => Entries.Count(e => e.Status == RestoreEntryStatus.Failed);
}
