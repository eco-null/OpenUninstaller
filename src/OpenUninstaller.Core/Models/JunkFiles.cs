namespace OpenUninstaller.Core.Models;

/// <summary>Additional locations are searched by name only and are always report-only.</summary>
public sealed record JunkScanOptions
{
    public bool IncludeKnownLocations { get; init; } = true;
    public IReadOnlyList<string> SearchRoots { get; init; } = [];
    /// <summary>Explicit report-only folder contents, regardless of main masks, exclusions or age.</summary>
    public IReadOnlyList<string> IncludeRoots { get; init; } = [];
    public IReadOnlyList<string> Patterns { get; init; } = [];
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromHours(24);
    public bool UseLastAccessTime { get; init; } = true;
    public bool UseAgeFilter { get; init; } = true;
    public bool UseDefaultExclusions { get; init; } = true;
}

/// <summary>A reviewed file identity, rather than permission to delete an arbitrary path.</summary>
public sealed record JunkFile
{
    public required string Id { get; init; }
    public required string CategoryId { get; init; }
    public required string Root { get; init; }
    public required string Path { get; init; }
    public required string Identity { get; init; }
    public long Bytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime LastAccessUtc { get; init; }
    public DateTime CreationTimeUtc { get; init; }
    public bool CanClean { get; init; }
    public required string Reason { get; init; }
    public string SizeDisplay => InstalledProgram.FormatBytes(Bytes);
}
