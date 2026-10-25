using Microsoft.Win32;

namespace OpenUninstaller.Core.Models;

public sealed record TracedRegistryKey
{
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string Path { get; init; }
    public List<string> ValueNames { get; init; } = [];

    public RegistryLocation ToLocation() => new(Hive, View, Path);
}

public sealed record TracedFile
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>
/// The recorded difference between two machine snapshots around an installer run. Traces are stored
/// locally as JSON; they are evidence, not auto-deletion instructions.
/// </summary>
public sealed record TraceDocument
{
    /// <summary>
    /// Schema 2 stores keys created by the installer separately from values added to keys that
    /// already existed. Schema 1 traces keep the old ambiguous list; they are treated as key
    /// traces because the original evidence cannot be recovered safely.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string ProgramName { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? InstallerPath { get; init; }
    public int? InstallerExitCode { get; init; }
    public bool RegistryTruncated { get; init; }
    public bool FilesTruncated { get; init; }
    public List<string> RegistryRoots { get; init; } = [];
    public List<string> FileRoots { get; init; } = [];
    public List<TracedRegistryKey> RegistryKeys { get; init; } = [];

    /// <summary>Values added to keys that existed before the installer ran (schema 2+).</summary>
    public List<TracedRegistryKey> RegistryValues { get; init; } = [];

    public List<TracedFile> Files { get; init; } = [];

    public int KeyCount => RegistryKeys.Count;
    public int ValueCount => RegistryValues.Sum(v => v.ValueNames.Count);
    public int FileCount => Files.Count;
}

/// <summary>Diff produced by comparing two registry snapshots.</summary>
public sealed record RegistrySnapshotDiff
{
    public List<TracedRegistryKey> AddedKeys { get; init; } = [];

    /// <summary>Values added to keys that already existed before the installer ran.</summary>
    public List<TracedRegistryKey> AddedValuesToExistingKeys { get; init; } = [];

    public bool Truncated { get; init; }
}

public sealed record FileSnapshotDiff
{
    public List<TracedFile> AddedPaths { get; init; } = [];
    public bool Truncated { get; init; }
}
