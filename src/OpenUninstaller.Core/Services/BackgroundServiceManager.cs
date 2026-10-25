using System.Diagnostics;
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

    public string Label => !Installed
        ? "Not installed"
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
        return new BackgroundServiceStatus
        {
            Installed = true,
            Running = state is "RUNNING" or "START_PENDING",
            State = state ?? "Unknown",
            Detail = state is null ? "The service state could not be parsed." : string.Empty,
            ExecutablePath = executable,
            ExecutableExists = exists
        };
    }

    public bool Install(out string? error)
    {
        error = null;
        if (!File.Exists(ExecutablePath))
        {
            error = $"The service executable was not found at {ExecutablePath}. Publish the app with scripts/publish.ps1 first.";
            return false;
        }

        var binary = ExecutablePath.Replace("\"", string.Empty, StringComparison.Ordinal);
        var create = RunSc(
            $"create \"{_serviceName}\" binPath= \"\\\"{binary}\\\"\" start= auto " +
            $"DisplayName= \"OpenUninstaller maintenance\"");
        if (create.ExitCode != 0)
        {
            error = DescribeFailure("create", create);
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

        return true;
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
