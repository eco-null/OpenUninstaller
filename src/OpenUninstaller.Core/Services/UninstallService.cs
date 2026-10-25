using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Runs (or forces) the removal of an application and verifies the registration afterwards.
/// Leftover discovery and deletion are separate, explicitly approved steps.
/// </summary>
public sealed class UninstallService
{
    private static readonly HashSet<int> SuccessExitCodes = [0, 1605, 1641, 3010];

    private readonly IRegistryStore _registry;
    private readonly IProcessRunner _runner;
    private readonly IOperationLog _log;

    public UninstallService(IRegistryStore registry, IProcessRunner runner, IOperationLog log)
    {
        _registry = registry;
        _runner = runner;
        _log = log;
    }

    public async Task<UninstallResult> RunAsync(
        InstalledProgram program,
        UninstallOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();

        if (options.Force || options.SkipVendorUninstaller)
        {
            return Force(program, messages, stopwatch);
        }

        var command = UninstallCommandResolver.Resolve(program, options.PreferSilent);
        if (!command.IsUsable)
        {
            messages.Add(command.Error ?? "The vendor uninstaller is unavailable.");
            messages.Add("Use forced uninstall to remove the registration and scan for leftovers.");
            _log.Write("uninstall", "resolve", program.RegistryPath, "failed", command.Error);
            return new UninstallResult
            {
                ProgramId = program.Id,
                ProgramName = program.Name,
                Succeeded = false,
                StillRegistered = true,
                CommandLine = command.DisplayCommand,
                Duration = stopwatch.Elapsed,
                Messages = messages
            };
        }

        progress?.Report(new OperationProgress(OperationStages.RunningUninstaller, command.DisplayCommand));
        _log.Write("uninstall", "run", program.RegistryPath, "started", command.DisplayCommand);

        var result = await _runner.RunAsync(
            command.Executable,
            command.Arguments,
            shellExecute: true,
            options.WaitTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            messages.Add($"Could not start the uninstaller: {result.Error ?? "unknown error"}.");
            _log.Write("uninstall", "run", command.DisplayCommand, "failed", result.Error);
            return new UninstallResult
            {
                ProgramId = program.Id,
                ProgramName = program.Name,
                Succeeded = false,
                StillRegistered = true,
                CommandLine = command.DisplayCommand,
                Duration = stopwatch.Elapsed,
                Messages = messages
            };
        }

        if (result.TimedOut || result.StillRunning)
        {
            messages.Add("The vendor uninstaller is still running. OpenUninstaller did not terminate it.");
            messages.Add("Close the uninstaller, then refresh the list and scan again.");
            _log.Write("uninstall", "wait", command.DisplayCommand, "timed-out", $"pid={result.ProcessId}");
            return new UninstallResult
            {
                ProgramId = program.Id,
                ProgramName = program.Name,
                Succeeded = false,
                TimedOut = result.TimedOut,
                StillRegistered = _registry.KeyExists(program.Location),
                ExitCode = result.ExitCode,
                CommandLine = command.DisplayCommand,
                Duration = stopwatch.Elapsed,
                Messages = messages
            };
        }

        progress?.Report(new OperationProgress(OperationStages.Verifying, "Checking the uninstall registration"));
        var stillRegistered = _registry.KeyExists(program.Location);
        var rebootRequired = result.ExitCode is 1641 or 3010;
        var successCode = result.ExitCode is null || SuccessExitCodes.Contains(result.ExitCode.Value);

        messages.Add($"Uninstaller exited with code {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
        if (stillRegistered)
        {
            messages.Add("The registry entry is still present. The vendor uninstaller may have been cancelled, or cleanup is incomplete.");
        }
        else
        {
            messages.Add("The registry entry was removed.");
        }

        _log.Write(
            "uninstall",
            "verify",
            program.RegistryPath,
            stillRegistered ? "still-registered" : "removed",
            $"exit={result.ExitCode}");

        progress?.Report(new OperationProgress(OperationStages.Completed, stillRegistered ? "Still registered" : "Uninstalled"));

        return new UninstallResult
        {
            ProgramId = program.Id,
            ProgramName = program.Name,
            Succeeded = !stillRegistered && successCode && !cancellationToken.IsCancellationRequested,
            Cancelled = cancellationToken.IsCancellationRequested,
            StillRegistered = stillRegistered,
            RebootRequired = rebootRequired,
            ExitCode = result.ExitCode,
            CommandLine = command.DisplayCommand,
            Duration = stopwatch.Elapsed,
            Messages = messages
        };
    }

    private UninstallResult Force(InstalledProgram program, List<string> messages, Stopwatch stopwatch)
    {
        var guard = RegistryGuard.ValidateForDeletion(program.Location, allowUninstallEntry: true);
        if (!guard.Allowed)
        {
            messages.Add($"Forced uninstall was refused: {guard.Reason}");
            _log.Write("force-uninstall", "guard", program.RegistryPath, "rejected", guard.Reason);
            return new UninstallResult
            {
                ProgramId = program.Id,
                ProgramName = program.Name,
                Succeeded = false,
                Forced = true,
                StillRegistered = true,
                Duration = stopwatch.Elapsed,
                Messages = messages
            };
        }

        try
        {
            _log.Write("force-uninstall", "delete-key", program.RegistryPath, "started");
            _registry.DeleteKey(program.Location, recursive: true);
            messages.Add("The uninstall registration entry was removed.");
            _log.Write("force-uninstall", "delete-key", program.RegistryPath, "completed");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            messages.Add($"Could not remove the registration entry: {ex.Message}");
            _log.Write("force-uninstall", "delete-key", program.RegistryPath, "failed", ex.Message);
        }

        var stillRegistered = _registry.KeyExists(program.Location);
        if (!stillRegistered)
        {
            messages.Add("Run the leftover scan next to review files and keys the vendor left behind.");
        }

        return new UninstallResult
        {
            ProgramId = program.Id,
            ProgramName = program.Name,
            Succeeded = !stillRegistered,
            Forced = true,
            StillRegistered = stillRegistered,
            Duration = stopwatch.Elapsed,
            Messages = messages
        };
    }
}
