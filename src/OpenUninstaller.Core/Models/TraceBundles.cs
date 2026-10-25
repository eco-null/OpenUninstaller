namespace OpenUninstaller.Core.Models;

/// <summary>Integrity metadata stored beside a portable trace document.</summary>
public sealed record TraceBundleManifest
{
    public int BundleSchemaVersion { get; init; } = 1;
    public required int TraceSchemaVersion { get; init; }
    public required string TraceId { get; init; }
    public required string ProgramName { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public string? AppVersion { get; init; }
    public required string TraceSha256 { get; init; }
    public int RegistryKeys { get; init; }
    public int RegistryValues { get; init; }
    public int Files { get; init; }
}

public sealed record TraceBundleExportResult
{
    public required bool Succeeded { get; init; }
    public required string TraceId { get; init; }
    public required string Path { get; init; }
    public string? BundleSha256 { get; init; }
    public string? Error { get; init; }
}

public sealed record TraceBundleImportResult
{
    public required bool Succeeded { get; init; }
    public TraceDocument? Trace { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; init; } = [];
}
