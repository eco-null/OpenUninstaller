using System.Diagnostics;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

public sealed record BackgroundServiceStatus
{
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public string State { get; init; } = "Unknown";
    public string Detail { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public bool ExecutableExists { get; init; }

    /// <summary>File version of the binary the registered service runs, when it can be read.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>File version of the service binary shipped with this build.</summary>
    public string? BundledVersion { get; init; }

    /// <summary>
    /// The registered service runs another version than this build ships, or runs from a folder
    /// outside Program Files. <see cref="BackgroundServiceManager.Update"/> fixes both.
    /// </summary>
    public bool NeedsUpdate { get; init; }

    public string Label => !Installed
        ? "Not installed"
        : NeedsUpdate ? (Running ? "Running · update available" : $"{State} · update available")
        : Running ? "Running" : State;
}

/// <summary>
/// Installs and controls the optional maintenance service with <c>sc.exe</c>. Mutating calls need an
/// elevated process; the manager reports the access error instead of requesting elevation itself.
/// </summary>
public sealed class BackgroundServiceManager
{
    public const string DefaultServiceName = "OpenUninstallerMaintenance";

    private readonly AppPaths _paths;
    private readonly string _serviceName;
    private readonly string? _configuredExecutable;

    public BackgroundServiceManager(AppPaths paths, string? executablePath = null, string? serviceName = null)
    {
        _paths = paths;
        _serviceName = string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName;
        if (!_serviceName.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
        {
            throw new ArgumentException("The service name contains unsupported characters.", nameof(serviceName));
        }

        _configuredExecutable = executablePath;
    }

    public string ServiceName => _serviceName;

    public string ExecutablePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_configuredExecutable))
            {
                return _configuredExecutable;
            }

            var parent = Directory.GetParent(_paths.AppBaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
            var candidates = new[]
            {
                Path.Combine(_paths.AppBaseDirectory, "service", "OpenUninstaller.Service.exe"),
                Path.Combine(_paths.AppBaseDirectory, "OpenUninstaller.Service.exe"),
                Path.Combine(_paths.AppBaseDirectory, "service", "ou-maintenance.exe"),
                Path.Combine(parent ?? _paths.AppBaseDirectory, "service", "OpenUninstaller.Service.exe"),
                Path.Combine(parent ?? _paths.AppBaseDirectory, "OpenUninstaller.Service.exe")
            };
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public BackgroundServiceStatus Query()
    {
        var executable = ExecutablePath;
        var exists = File.Exists(executable);
        if (!File.Exists(ScExePath()))
        {
            return new BackgroundServiceStatus
            {
                State = "Unavailable",
                Detail = "sc.exe was not found.",
                ExecutablePath = executable,
                ExecutableExists = exists
            };
        }

        var run = RunSc($"query \"{_serviceName}\"");
        if (run.ExitCode == 1060)
        {
            return new BackgroundServiceStatus
            {
                Detail = "The maintenance service is not installed.",
                ExecutablePath = executable,
                ExecutableExists = exists
            };
        }

        if (run.ExitCode != 0)
        {
            return new BackgroundServiceStatus
            {
                State = "Error",
                Detail = FirstLine(run.Output) ?? $"sc query exited with {run.ExitCode}.",
                ExecutablePath = executable,
                ExecutableExists = exists
            };
        }

        var state = ParseState(run.Output);
        var binary = ParseBinaryPath(RunSc($"qc \"{_serviceName}\"").Output);
        var installedVersion = binary is null ? null : FileVersion(binary);
        var bundledVersion = exists ? FileVersion(executable) : null;
        var outsideProgramFiles = binary is not null && !IsAdminOnlyLocation(binary);
        var needsUpdate = exists && (outsideProgramFiles || IsOutdated(installedVersion, bundledVersion));

        var detail = state is null ? "The service state could not be parsed." : string.Empty;
        if (outsideProgramFiles)
        {
            // Installs made before the binaries were staged under Program Files run LocalSystem
            // code from the per-user application folder.
            detail = $"The service runs {binary}, which non-administrators may be able to replace. " +
                     "Update the service to move it under Program Files.";
        }
        else if (needsUpdate)
        {
            detail = $"The service runs version {installedVersion ?? "unknown"}; this build includes " +
                     $"{bundledVersion}. Update the service to run the new version.";
        }

        return new BackgroundServiceStatus
        {
            Installed = true,
            Running = state is "RUNNING" or "START_PENDING",
            State = state ?? "Unknown",
            Detail = detail,
            ExecutablePath = executable,
            ExecutableExists = exists,
            InstalledVersion = installedVersion,
            BundledVersion = bundledVersion,
            NeedsUpdate = needsUpdate
        };
    }

    /// <summary>
    /// Moves an installed service to the binaries of this build: stops it, re-stages the files
    /// under Program Files (keeping the service's own logs), points the registration at them and
    /// starts it again. Also moves services registered before 1.0.10 out of the per-user folder.
    /// If the new files cannot be staged, the previous copy is left in place and restarted.
    /// </summary>
    public bool Update(out string? error)
    {
        error = null;
        if (!File.Exists(ExecutablePath))
        {
            error = $"The service executable was not found at {ExecutablePath}. Publish the app with scripts/publish.ps1 first.";
            return false;
        }

        if (!Query().Installed)
        {
            error = "The maintenance service is not installed.";
            return false;
        }

        var stop = RunSc($"stop \"{_serviceName}\"");
        if (stop.ExitCode is not (0 or 1062))
        {
            error = DescribeFailure("stop", stop);
            return false;
        }

        if (!WaitForState("STOPPED", TimeSpan.FromSeconds(30)))
        {
            error = "The maintenance service did not stop within 30 seconds; nothing was changed.";
            return false;
        }

        string binary;
        try
        {
            binary = StageBinaries();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunSc($"start \"{_serviceName}\"");
            error = $"Could not copy the service files to {InstalledDirectory}: {ex.Message} The previous version was restarted.";
            return false;
        }

        var config = RunSc($"config \"{_serviceName}\" binPath= \"\\\"{binary}\\\"\"");
        if (config.ExitCode != 0)
        {
            error = DescribeFailure("config", config);
            return false;
        }

        var start = RunSc($"start \"{_serviceName}\"");
        if (start.ExitCode != 0 && start.ExitCode != 1056)
        {
            error = DescribeFailure("start", start);
            return false;
        }

        return true;
    }

    internal static bool IsOutdated(string? installedVersion, string? bundledVersion)
        => !string.IsNullOrWhiteSpace(bundledVersion)
           && !string.Equals(installedVersion, bundledVersion, StringComparison.OrdinalIgnoreCase);

    private static string? FileVersion(string path)
    {
        try
        {
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool WaitForState(string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var run = RunSc($"query \"{_serviceName}\"");
            if (run.ExitCode == 0 && ParseState(run.Output) == state)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Where <see cref="Install"/> places the service binaries. The service runs as LocalSystem, so
    /// it must never execute from the (per-user, writable) application folder: any unelevated
    /// process could replace the executable and run code as SYSTEM. Program Files is admin-only.
    /// </summary>
    public string InstalledDirectory => Path.Combine(_paths.ProgramFiles, "OpenUninstaller", "Maintenance");

    public bool Install(out string? error)
    {
        error = null;
        if (!File.Exists(ExecutablePath))
        {
            error = $"The service executable was not found at {ExecutablePath}. Publish the app with scripts/publish.ps1 first.";
            return false;
        }

        if (Query().Installed)
        {
            error = "The maintenance service is already installed. Remove it first to reinstall it.";
            return false;
        }

        string binary;
        try
        {
            binary = StageBinaries();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not copy the service files to {InstalledDirectory}: {ex.Message}";
            return false;
        }

        var create = RunSc(
            $"create \"{_serviceName}\" binPath= \"\\\"{binary}\\\"\" start= auto " +
            $"DisplayName= \"OpenUninstaller maintenance\"");
        if (create.ExitCode != 0)
        {
            error = DescribeFailure("create", create);
            TryRemoveStagedBinaries();
            return false;
        }

        RunSc($"description \"{_serviceName}\" \"Runs OpenUninstaller maintenance: backup retention, log expiry and an optional junk cleanup.\"");
        var start = RunSc($"start \"{_serviceName}\"");
        if (start.ExitCode != 0 && start.ExitCode != 1056)
        {
            error = DescribeFailure("start", start);
            return false;
        }

        return true;
    }

    public bool Remove(out string? error)
    {
        error = null;
        RunSc($"stop \"{_serviceName}\"");
        var delete = RunSc($"delete \"{_serviceName}\"");
        if (delete.ExitCode != 0 && delete.ExitCode != 1060)
        {
            error = DescribeFailure("delete", delete);
            return false;
        }

        // sc stop only requests the stop; wait for the process to release its files.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Query().Installed && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }

        if (!TryRemoveStagedBinaries())
        {
            error = $"The service was removed, but {InstalledDirectory} could not be deleted yet. Delete it after the service process exits.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Copies this build's service folder to <see cref="InstalledDirectory"/>. The copy is built
    /// next to it first and swapped in, so a failed copy leaves the previous version intact. The
    /// service's own <c>Logs</c> folder moves over to the new copy.
    /// </summary>
    internal string StageBinaries()
    {
        var source = Path.GetFullPath(Path.GetDirectoryName(ExecutablePath)!).TrimEnd('\\');
        var destination = Path.GetFullPath(InstalledDirectory).TrimEnd('\\');
        var binary = Path.Combine(destination, Path.GetFileName(ExecutablePath));
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return binary;
        }

        var staging = destination + ".new";
        var previous = destination + ".old";
        DeleteDirectoryIfExists(staging);
        DeleteDirectoryIfExists(previous);
        try
        {
            CopyDirectory(source, staging);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        if (Directory.Exists(destination))
        {
            // The service process may need a moment to release its files after it stops.
            MoveDirectoryWithRetry(destination, previous);
            try
            {
                var logs = Path.Combine(previous, LogsFolder);
                if (Directory.Exists(logs))
                {
                    Directory.Move(logs, Path.Combine(staging, LogsFolder));
                }

                Directory.Move(staging, destination);
            }
            catch
            {
                var stagedLogs = Path.Combine(staging, LogsFolder);
                if (Directory.Exists(stagedLogs) && !Directory.Exists(Path.Combine(previous, LogsFolder)))
                {
                    Directory.Move(stagedLogs, Path.Combine(previous, LogsFolder));
                }

                if (!Directory.Exists(destination))
                {
                    Directory.Move(previous, destination);
                }

                TryDeleteDirectory(staging);
                throw;
            }

            TryDeleteDirectory(previous);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
        }

        return binary;
    }

    private const string LogsFolder = "Logs";

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var logs = Path.Combine(source, LogsFolder) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            // A service that ran from this folder (before 1.0.10) wrote its logs here; they are
            // not part of the binaries.
            if (file.StartsWith(logs, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectoryIfExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool TryRemoveStagedBinaries()
    {
        var directory = InstalledDirectory;
        try
        {
            if (Directory.Exists(directory)
                && !string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(ExecutablePath)!).TrimEnd('\\'),
                    Path.GetFullPath(directory).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Start(out string? error)
    {
        var run = RunSc($"start \"{_serviceName}\"");
        error = run.ExitCode is 0 or 1056 ? null : DescribeFailure("start", run);
        return error is null;
    }

    public bool Stop(out string? error)
    {
        var run = RunSc($"stop \"{_serviceName}\"");
        error = run.ExitCode is 0 or 1062 ? null : DescribeFailure("stop", run);
        return error is null;
    }

    private static string? ParseState(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var parts = trimmed[(colon + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? parts[1].ToUpperInvariant()
                : null;
        }

        return null;
    }

    /// <summary>Reads BINARY_PATH_NAME from <c>sc qc</c> output and returns the executable path.</summary>
    internal static string? ParseBinaryPath(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                var end = value.IndexOf('"', 1);
                value = end < 0 ? value[1..] : value[1..end];
            }

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private bool IsAdminOnlyLocation(string binary)
        => PathGuard.IsUnder(binary, _paths.ProgramFiles)
           || (!string.IsNullOrWhiteSpace(_paths.ProgramFilesX86) && PathGuard.IsUnder(binary, _paths.ProgramFilesX86));

    private static string DescribeFailure(string action, (int ExitCode, string Output) run)
    {
        var detail = FirstLine(run.Output);
        return detail is null
            ? $"sc {action} failed with exit code {run.ExitCode}. Administrator rights may be required."
            : $"sc {action} failed ({run.ExitCode}): {detail}";
    }

    private static string? FirstLine(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !line.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase));

    private static (int ExitCode, string Output) RunSc(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ScExePath(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return (-1, "sc.exe could not be started.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return (-1, "sc.exe timed out.");
            }

            Task.WaitAll(stdout, stderr);
            return (process.ExitCode, string.Join(
                Environment.NewLine,
                new[] { stdout.Result, stderr.Result }.Where(t => !string.IsNullOrWhiteSpace(t))));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }

    private static string ScExePath()
    {
        var system = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(system)
            ? "sc.exe"
            : Path.Combine(system, "sc.exe");
    }
}
