namespace OpenUninstaller.Core.Models;

public sealed record LeftoverSnapshotItem
{
    public required string Id { get; init; }
    public required LeftoverKind Kind { get; init; }
    public required string DisplayPath { get; init; }
    public required LeftoverConfidence Confidence { get; init; }
    public bool IsProtected { get; init; }

    public static LeftoverSnapshotItem FromItem(LeftoverItem item) => new()
    {
        Id = item.Id,
        Kind = item.Kind,
        DisplayPath = item.DisplayPath,
        Confidence = item.Confidence,
        IsProtected = item.IsProtected
    };
}

public sealed record LeftoverScanSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required string ProgramId { get; init; }
    public required string ProgramName { get; init; }
    public required DateTimeOffset ScannedAt { get; init; }
    public bool UsedTrace { get; init; }
    public string? TraceId { get; init; }
    public List<LeftoverSnapshotItem> Items { get; init; } = [];
}

public sealed class LeftoverScanDiff
{
    public LeftoverScanSnapshot? Previous { get; init; }
    public List<LeftoverSnapshotItem> NewItems { get; init; } = [];
    public List<LeftoverSnapshotItem> ResolvedItems { get; init; } = [];
    public List<LeftoverSnapshotItem> PersistentItems { get; init; } = [];

    public bool HasPrevious => Previous is not null;
    public int NewCount => NewItems.Count;
    public int ResolvedCount => ResolvedItems.Count;
    public int PersistentCount => PersistentItems.Count;

    public string Summary => Previous is null
        ? "No previous scan was stored for this application."
        : $"{NewCount} new · {ResolvedCount} resolved · {PersistentCount} unchanged since " +
          $"{Previous.ScannedAt.ToLocalTime():yyyy-MM-dd HH:mm}.";
}
