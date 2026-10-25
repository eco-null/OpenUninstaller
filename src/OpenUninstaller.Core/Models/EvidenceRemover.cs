using System.IO;

namespace OpenUninstaller.Core.Models;

public sealed record DriveFreeSpaceInfo
{
    public required string DriveName { get; init; }
    public required string VolumeLabel { get; init; }
    public DriveType DriveType { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }

    public string TotalSizeDisplay => InstalledProgram.FormatBytes(TotalBytes);
    public string FreeSpaceDisplay => InstalledProgram.FormatBytes(FreeBytes);
    public double FreePercentage => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100.0 : 0.0;
    public string DriveTypeDisplay => DriveType switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable Disk",
        DriveType.Network => "Network Drive",
        DriveType.CDRom => "Optical Drive",
        DriveType.Ram => "RAM Disk",
        _ => "Unknown Disk"
    };

    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{DriveTypeDisplay} ({DriveName.TrimEnd('\\')})"
        : $"{VolumeLabel} ({DriveName.TrimEnd('\\')})";

    public string DriveLetter => DriveName.TrimEnd('\\');
}

public sealed record WipeProgress
{
    public required string DriveName { get; init; }
    public long TotalBytesToWipe { get; init; }
    public long BytesWiped { get; init; }
    public double PercentComplete { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public required string StageMessage { get; init; }
}

public sealed record WipeResult
{
    public required string DriveName { get; init; }
    public long TotalBytesWiped { get; init; }
    public long BytesWiped => TotalBytesWiped;
    public string BytesWipedDisplay => InstalledProgram.FormatBytes(TotalBytesWiped);
    public string DriveLetter => DriveName.TrimEnd('\\');
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
}
