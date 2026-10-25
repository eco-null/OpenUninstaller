using Microsoft.Win32;

namespace OpenUninstaller.Core.Models;

public enum ProgramArchitecture
{
    Unknown,
    X64,
    X86
}

/// <summary>One entry from the Windows uninstall registry.</summary>
public sealed record InstalledProgram
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallCommand { get; init; }
    public string? QuietUninstallCommand { get; init; }
    public string? DisplayIcon { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public DateOnly? InstallDate { get; init; }
    public required RegistryLocation Location { get; init; }
    public ProgramArchitecture Architecture { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public bool IsSystemComponent { get; init; }
    public string? UrlInfoAbout { get; init; }
    public string? Comments { get; init; }

    public bool IsMachineWide => Location.IsMachine;

    public string RegistryPath => Location.DisplayPath;

    public string LocationDisplay => string.IsNullOrWhiteSpace(InstallLocation)
        ? "No install location recorded"
        : InstallLocation;

    /// <summary>Size shown in the UI, falling back to an em dash.</summary>
    public string SizeDisplay => EstimatedSizeBytes is > 0
        ? FormatBytes(EstimatedSizeBytes.Value)
        : "—";

    public string ArchitectureDisplay => Architecture switch
    {
        ProgramArchitecture.X64 => "64-bit",
        ProgramArchitecture.X86 => "32-bit",
        _ => "—"
    };

    public bool IsNew => InstallDate.HasValue &&
        InstallDate.Value >= DateOnly.FromDateTime(DateTime.Today.AddDays(-7));

    public bool IsLarge => EstimatedSizeBytes >= 500L * 1024 * 1024;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.#} {units[unit]}";
    }
}
