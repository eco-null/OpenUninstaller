using System.Runtime.InteropServices;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Environment self-check used by the CLI and the About/Settings page.</summary>
public sealed class DoctorService
{
    private readonly AppPaths _paths;
    private readonly ProgramScanner _scanner;
    private readonly TraceStore _traces;
    private readonly BackupService _backups;
    private readonly IElevationService _elevation;

    public DoctorService(
        AppPaths paths,
        ProgramScanner scanner,
        TraceStore traces,
        BackupService backups,
        IElevationService elevation)
    {
        _paths = paths;
        _scanner = scanner;
        _traces = traces;
        _backups = backups;
        _elevation = elevation;
    }

    public DoctorReport Run()
    {
        var warnings = new List<string>();
        var checks = new List<string>();
        var writable = false;

        try
        {
            _paths.EnsureDirectories();
            var probe = Path.Combine(_paths.DataDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            writable = true;
            checks.Add("Data directory is writable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Data directory is not writable: {ex.Message}");
        }

        int programCount = 0;
        try
        {
            programCount = _scanner.Scan().Count;
            checks.Add($"Detected {programCount} installed applications.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            warnings.Add($"Inventory failed: {ex.Message}");
        }

        int traceCount = 0;
        int backupCount = 0;
        try
        {
            traceCount = _traces.List().Count;
            backupCount = _backups.List().Count;
            checks.Add($"{traceCount} trace(s), {backupCount} backup(s) available.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Local store check failed: {ex.Message}");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            warnings.Add("OpenUninstaller requires Windows.");
        }

        checks.Add(_elevation.IsElevated
            ? "Running elevated: machine-wide changes are available."
            : "Running unelevated: HKLM and some folders need elevation.");

        if (!_elevation.IsElevated)
        {
            warnings.Add("Some cleanup operations will be refused without administrator rights.");
        }

        return new DoctorReport
        {
            AppVersion = _paths.AppVersion,
            OperatingSystem = RuntimeInformation.OSDescription,
            IsElevated = _elevation.IsElevated,
            DataDirectory = _paths.DataDirectory,
            DataDirectoryWritable = writable,
            BackupDirectory = _paths.BackupsDirectory,
            TraceDirectory = _paths.TracesDirectory,
            LogDirectory = _paths.LogsDirectory,
            ProgramCount = programCount,
            TraceCount = traceCount,
            BackupCount = backupCount,
            Checks = checks,
            Warnings = warnings
        };
    }
}
