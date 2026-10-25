namespace OpenUninstaller.Core.Models;

public enum LiveChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
    ProcessStarted
}

public sealed class LiveTrackedFileEvent
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required LiveChangeKind Kind { get; init; }
    public required DateTime Timestamp { get; init; }
    public long Size { get; init; }
}

public sealed class LiveTrackedProcess
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public required DateTime StartTime { get; init; }
}
