namespace OpenUninstaller.Core.Models;

/// <summary>A file or folder the user asked to overwrite before deletion.</summary>
public sealed record ShredTarget
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Bytes { get; init; }
    public int FileCount { get; init; }
}

public sealed record ShredPlan
{
    public List<ShredTarget> Targets { get; init; } = [];
    public List<string> Rejected { get; init; } = [];
    public long TotalBytes => Targets.Sum(t => t.Bytes);
    public int TotalFiles => Targets.Sum(t => t.FileCount);
    public bool CanShred => Targets.Count > 0;
}

public sealed record ShredResult
{
    public bool DryRun { get; init; }
    public int Passes { get; init; }
    public int ShreddedFiles { get; set; }
    public long ShreddedBytes { get; set; }
    public int DeletedDirectories { get; set; }
    public List<string> Skipped { get; } = [];
    public List<string> Failed { get; } = [];
    public List<string> Rejected { get; } = [];
    public TimeSpan Duration { get; set; }
    public bool Succeeded => Failed.Count == 0 && Rejected.Count == 0;
}

public sealed record PrivacyCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public long Bytes { get; set; }
    public int ItemCount { get; set; }
    public string? Warning { get; set; }
    public string SizeDisplay => InstalledProgram.FormatBytes(Bytes);
}

public sealed record PrivacyScanResult
{
    public List<PrivacyCategory> Categories { get; init; } = [];
    public long TotalBytes => Categories.Sum(c => c.Bytes);
    public int TotalItems => Categories.Sum(c => c.ItemCount);
}

public sealed record PrivacyCleanResult
{
    public bool DryRun { get; init; }
    public long Bytes { get; set; }
    public int ItemsDeleted { get; set; }
    public int FailedItems { get; set; }
    public List<string> Messages { get; } = [];
    public TimeSpan Duration { get; set; }
    public bool Succeeded => FailedItems == 0;
}

