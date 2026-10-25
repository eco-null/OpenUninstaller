namespace OpenUninstaller.Core.Models;

/// <summary>Progress payload shared by CLI and UI.</summary>
public sealed record OperationProgress(string Stage, string Message, double? Fraction = null);

public static class OperationStages
{
    public const string Preparing = "preparing";
    public const string RunningUninstaller = "running-uninstaller";
    public const string Verifying = "verifying";
    public const string Scanning = "scanning";
    public const string BackingUp = "backing-up";
    public const string Deleting = "deleting";
    public const string Restoring = "restoring";
    public const string Cleaning = "cleaning";
    public const string Completed = "completed";
}

public sealed record DoctorReport
{
    public required string AppVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public required bool IsElevated { get; init; }
    public required string DataDirectory { get; init; }
    public required bool DataDirectoryWritable { get; init; }
    public required string BackupDirectory { get; init; }
    public required string TraceDirectory { get; init; }
    public required string LogDirectory { get; init; }
    public int ProgramCount { get; init; }
    public int TraceCount { get; init; }
    public int BackupCount { get; init; }
    public List<string> Checks { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Healthy => DataDirectoryWritable;
}
