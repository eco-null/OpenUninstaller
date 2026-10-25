namespace OpenUninstaller.Core.Models;

/// <summary>
/// A leftover the user explicitly asked OpenUninstaller to stop offering. Ignoring never deletes
/// anything; it only hides the same item from later scans of the same application.
/// </summary>
public sealed record IgnoredLeftoverEntry
{
    public required string ProgramId { get; init; }
    public required string ProgramName { get; init; }
    public required string ItemId { get; init; }
    public required string DisplayPath { get; init; }
    public required DateTimeOffset IgnoredAt { get; init; }
}

public sealed record IgnoredLeftoverDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<IgnoredLeftoverEntry> Entries { get; init; } = [];
}
