using Microsoft.Win32;

namespace OpenUninstaller.Core.Models;

public enum AutorunSourceKind
{
    UserRun,
    UserRunOnce,
    MachineRun,
    MachineRunOnce,
    UserStartupFolder,
    CommonStartupFolder,
    MachineService,
    ScheduledTask,
    DisabledRegistry,
    DisabledStartupFolder,
    DisabledService,
    DisabledScheduledTask,
    PackagedStartupTask
}

public sealed record AutorunEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required AutorunSourceKind Source { get; init; }
    public required string LocationDisplay { get; init; }
    public bool IsEnabled { get; init; }
    public RegistryLocation? RegistryLocation { get; init; }
    public string? FilePath { get; init; }
    public string? TaskName { get; init; }
    public string? Publisher { get; init; }
    public string? ExecutablePath { get; init; }
    public bool IsWindowsComponent { get; init; }
    public bool IsApplicationStartup { get; init; } = true;
    public RegistryLocation? StartupApprovalLocation { get; init; }
    public string? StartupApprovalValueName { get; init; }

    public string StateLabel => IsEnabled ? "Enabled" : "Disabled";
    public string ToggleLabel => Source == AutorunSourceKind.PackagedStartupTask
        ? "Manage" : IsEnabled ? "Disable" : "Enable";

    public string SourceLabel => Source switch
    {
        AutorunSourceKind.UserRun => "User Run",
        AutorunSourceKind.UserRunOnce => "User RunOnce",
        AutorunSourceKind.MachineRun => "Machine Run",
        AutorunSourceKind.MachineRunOnce => "Machine RunOnce",
        AutorunSourceKind.UserStartupFolder => "User Startup",
        AutorunSourceKind.CommonStartupFolder => "All users Startup",
        AutorunSourceKind.MachineService => "Windows service",
        AutorunSourceKind.ScheduledTask => "Scheduled task",
        AutorunSourceKind.DisabledRegistry => "Disabled (registry)",
        AutorunSourceKind.DisabledService => "Disabled (service)",
        AutorunSourceKind.DisabledScheduledTask => "Disabled (task)",
        AutorunSourceKind.PackagedStartupTask => "Windows app",
        _ => "Disabled (Startup folder)"
    };
}

public sealed record AutorunScanResult
{
    public List<AutorunEntry> Entries { get; } = [];
    public List<string> Warnings { get; } = [];
    public int EnabledCount => Entries.Count(e => e.IsEnabled);
    public int DisabledCount => Entries.Count(e => !e.IsEnabled);
}

public enum AutorunHistoryAction
{
    Disabled,
    Enabled,
    RolledBack
}

public sealed record AutorunHistoryRecord
{
    public required string Id { get; init; }
    public required string EntryId { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required AutorunSourceKind Source { get; init; }
    public required AutorunHistoryAction Action { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? PreviousState { get; init; }
    public string? NewState { get; init; }
    public string? Detail { get; init; }
}

public sealed record AutorunHistoryEntry
{
    public required string Id { get; init; }
    public required string EntryId { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required AutorunSourceKind Source { get; init; }
    public required AutorunHistoryAction Action { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? PreviousState { get; init; }
    public string? NewState { get; init; }
    public string? Detail { get; init; }
    public bool CanRollback { get; init; }

    public string ActionLabel => Action switch
    {
        AutorunHistoryAction.Disabled => "Disabled",
        AutorunHistoryAction.Enabled => "Enabled",
        AutorunHistoryAction.RolledBack => "Rolled back",
        _ => Action.ToString()
    };

    public string SourceLabel => Source switch
    {
        AutorunSourceKind.UserRun => "User Run",
        AutorunSourceKind.UserRunOnce => "User RunOnce",
        AutorunSourceKind.MachineRun => "Machine Run",
        AutorunSourceKind.MachineRunOnce => "Machine RunOnce",
        AutorunSourceKind.UserStartupFolder => "User Startup",
        AutorunSourceKind.CommonStartupFolder => "All users Startup",
        AutorunSourceKind.MachineService => "Windows service",
        AutorunSourceKind.ScheduledTask => "Scheduled task",
        AutorunSourceKind.DisabledRegistry => "Registry value",
        AutorunSourceKind.DisabledService => "Windows service",
        AutorunSourceKind.DisabledScheduledTask => "Scheduled task",
        _ => "Startup item"
    };

    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record AutorunRollbackResult
{
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Messages { get; } = [];
    public bool Succeeded => FailedCount == 0;
}

public sealed record JunkCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public long Bytes { get; set; }
    public int ItemCount { get; set; }
    public bool CanClean { get; init; } = true;
    public bool RequiresElevation { get; init; }
    public List<string> Warnings { get; } = [];

    public string SizeDisplay => InstalledProgram.FormatBytes(Bytes);
}

public sealed record JunkScanResult
{
    public List<JunkCategory> Categories { get; init; } = [];
    public long TotalBytes => Categories.Sum(c => c.Bytes);
    public int TotalItems => Categories.Sum(c => c.ItemCount);
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }
}

public sealed record JunkCleanResult
{
    public bool DryRun { get; init; }
    public long FreedBytes { get; set; }
    public int DeletedItems { get; set; }
    public int SkippedItems { get; set; }
    public int FailedItems { get; set; }
    public List<string> Messages { get; } = [];
    public TimeSpan Duration { get; set; }
    public bool Succeeded => FailedItems == 0;
}

public sealed record HunterMatch
{
    public string? ExePath { get; init; }
    public string? ProcessName { get; init; }
    public int ProcessId { get; init; }
    public string? WindowTitle { get; init; }
    public InstalledProgram? Program { get; init; }
    public int Score { get; init; }
    public string Reason { get; init; } = "No matching application found.";
    public bool Matched => Program is not null;
}

public sealed record UninstallOptions
{
    public bool PreferSilent { get; init; }
    public bool Force { get; init; }
    public bool SkipVendorUninstaller { get; init; }
    public TimeSpan? WaitTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed record UninstallCommandInfo
{
    public required string Executable { get; init; }
    public required string Arguments { get; init; }
    public required string DisplayCommand { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public string? ProductCode { get; init; }
    public string? Error { get; init; }
    public bool IsUsable => Error is null && !string.IsNullOrWhiteSpace(Executable);
}

public sealed record UninstallResult
{
    public required string ProgramId { get; init; }
    public required string ProgramName { get; init; }
    public bool Succeeded { get; init; }
    public bool Cancelled { get; init; }
    public bool Forced { get; init; }
    public bool StillRegistered { get; init; }
    public bool TimedOut { get; init; }
    public bool RebootRequired { get; init; }
    public int? ExitCode { get; init; }
    public string? CommandLine { get; init; }
    public TimeSpan Duration { get; init; }
    public List<string> Messages { get; init; } = [];
    public string? LogPath { get; set; }
}
