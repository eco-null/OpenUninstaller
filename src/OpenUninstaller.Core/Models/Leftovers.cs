namespace OpenUninstaller.Core.Models;

public enum LeftoverKind
{
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,
    File,
    Directory,
    PathEntry,
    FirewallRule
}

public enum LeftoverConfidence
{
    Low = 20,
    Medium = 40,
    Strong = 70,
    High = 90,
    Exact = 100
}

/// <summary>
/// One candidate for removal. Instances are mutable only for the review selection flag; the
/// identity and evidence fields are immutable.
/// </summary>
public sealed class LeftoverItem
{
    public required string Id { get; init; }
    public required LeftoverKind Kind { get; init; }
    public required string DisplayPath { get; init; }
    public required LeftoverConfidence Confidence { get; init; }
    public required string Reason { get; init; }
    public bool IsFromTrace { get; init; }
    public long SizeBytes { get; set; }
    public RegistryLocation? RegistryLocation { get; init; }
    public string? FilePath { get; init; }

    /// <summary>Value name when <see cref="Kind"/> is <see cref="LeftoverKind.RegistryValue"/>.</summary>
    public string? ValueName { get; init; }

    /// <summary>Task Scheduler path (for example <c>Vendor\Updater</c>) for a scheduled task.</summary>
    public string? TaskName { get; init; }

    /// <summary>True for a direct App Paths or Classes\Applications entry.</summary>
    public bool IsAppCompatEntry { get; init; }

    /// <summary>True for a direct <c>Classes\&lt;ProgID&gt;</c> file-association registration.</summary>
    public bool IsAssociationEntry { get; init; }

    /// <summary>True for a direct <c>Classes\CLSID\&lt;guid&gt;</c> COM registration.</summary>
    public bool IsComEntry { get; init; }

    /// <summary>True when a <see cref="LeftoverKind.Service"/> entry is a driver service.</summary>
    public bool IsDriverEntry { get; init; }

    /// <summary>True when a service still has its binary, but the binary is inside the app's install folder.</summary>
    public bool IsOwnedServiceEntry { get; init; }

    /// <summary>True for a single value under the Windows Defender Firewall rule store.</summary>
    public bool IsFirewallRule { get; init; }

    /// <summary>The exact <c>PATH</c> segment for <see cref="LeftoverKind.PathEntry"/>.</summary>
    public string? PathSegment { get; init; }

    /// <summary>True when a PATH entry comes from the machine environment instead of the user's.</summary>
    public bool IsMachinePath { get; init; }

    /// <summary>Guard rejection reason, when the item is displayed but cannot be selected.</summary>
    public string? ProtectedReason { get; set; }

    public bool IsProtected => !string.IsNullOrEmpty(ProtectedReason);

    private bool _selected;
    public bool IsSelected
    {
        get => _selected && !IsProtected;
        set => _selected = value && !IsProtected;
    }

    public string ConfidenceLabel => IsFromTrace
        ? "Exact (traced)"
        : Confidence switch
        {
            LeftoverConfidence.Exact => "Exact",
            LeftoverConfidence.High => "High",
            LeftoverConfidence.Strong => "Strong",
            LeftoverConfidence.Medium => "Medium",
            _ => "Low"
        };

    public string KindLabel => Kind switch
    {
        LeftoverKind.RegistryKey => "Registry",
        LeftoverKind.RegistryValue => "Value",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Task",
        LeftoverKind.Directory => "Folder",
        LeftoverKind.PathEntry => "PATH",
        LeftoverKind.FirewallRule => "Firewall",
        _ => "File"
    };

    public string SizeDisplay => SizeBytes > 0 ? InstalledProgram.FormatBytes(SizeBytes) : "";
}

public sealed class LeftoverGroup
{
    public required string Title { get; init; }
    public required LeftoverKind Kind { get; init; }
    public List<LeftoverItem> Items { get; } = [];
    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
}

public sealed class LeftoverScanResult
{
    public required string ProgramId { get; init; }
    public required string ProgramName { get; init; }
    public bool UsedTrace { get; init; }
    public string? TraceId { get; init; }
    public TimeSpan Duration { get; set; }
    public bool Truncated { get; set; }

    /// <summary>Items hidden because the user explicitly ignored them for this application.</summary>
    public int IgnoredCount { get; set; }

    public List<LeftoverItem> Items { get; } = [];
    public List<string> Warnings { get; } = [];

    public IReadOnlyList<LeftoverGroup> Groups
    {
        get
        {
            var registry = new LeftoverGroup
            {
                Title = "Registry, associations, COM, services, PATH and firewall",
                Kind = LeftoverKind.RegistryKey
            };
            var tasks = new LeftoverGroup { Title = "Scheduled tasks", Kind = LeftoverKind.ScheduledTask };
            var files = new LeftoverGroup { Title = "Files and folders", Kind = LeftoverKind.File };
            registry.Items.AddRange(Items.Where(i => i.Kind is
                LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.Service
                or LeftoverKind.PathEntry or LeftoverKind.FirewallRule));
            tasks.Items.AddRange(Items.Where(i => i.Kind == LeftoverKind.ScheduledTask));
            files.Items.AddRange(Items.Where(i => i.Kind is LeftoverKind.File or LeftoverKind.Directory));
            return [registry, tasks, files];
        }
    }

    public int SelectableCount => Items.Count(i => !i.IsProtected);
    public int PreSelectedCount => Items.Count(i => i.IsSelected);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);

    public void ApplyDefaultSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = !item.IsProtected && item.Confidence >= LeftoverConfidence.High;
        }
    }

    public void SelectAllHighConfidence()
    {
        foreach (var item in Items)
        {
            item.IsSelected = !item.IsProtected && item.Confidence >= LeftoverConfidence.High;
        }
    }

    public void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }
}

public enum DeletionStatus
{
    Deleted,
    Skipped,
    Protected,
    BackupFailed,
    Failed,
    WouldDelete
}

public sealed record DeletionItemResult(
    string DisplayPath,
    LeftoverKind Kind,
    DeletionStatus Status,
    string? Detail = null);

public sealed class LeftoverDeletionResult
{
    public required string ProgramName { get; init; }
    public bool DryRun { get; init; }
    public string? BackupId { get; set; }
    public string? BackupDirectory { get; set; }
    public TimeSpan Duration { get; set; }
    public List<DeletionItemResult> Items { get; } = [];
    public List<string> Warnings { get; } = [];

    public int DeletedCount => Items.Count(i => i.Status is DeletionStatus.Deleted or DeletionStatus.WouldDelete);
    public int FailedCount => Items.Count(i => i.Status is DeletionStatus.Failed or DeletionStatus.BackupFailed);
    public int SkippedCount => Items.Count(i => i.Status is DeletionStatus.Skipped or DeletionStatus.Protected);

    public bool Succeeded => FailedCount == 0;
}
