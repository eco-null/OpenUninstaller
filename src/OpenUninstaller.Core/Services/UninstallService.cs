using System.Diagnostics;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using static OpenUninstaller.Core.Localization.UiText;

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
            messages.Add(command.Error ?? L("TheVendorUninstallerIsUnavailable"));
            messages.Add(L("UseForcedUninstallToRemoveTheRegistrationAnd"));
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

        var result = await _runner.RunWithDescendantsAsync(
            command.Executable,
            command.Arguments,
            pending: () => _registry.KeyExists(program.Location),
            options.WaitTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            messages.Add(F("CouldNotStartTheUninstaller", result.Error ?? L("UnknownError")));
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
            messages.Add(L("TheVendorUninstallerIsStillRunningOpenUninstallerDid"));
            messages.Add(L("CloseTheUninstallerThenRefreshTheListAnd"));
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

        progress?.Report(new OperationProgress(OperationStages.Verifying, L("CheckingTheUninstallRegistration")));
        var stillRegistered = _registry.KeyExists(program.Location);
        var rebootRequired = result.ExitCode is 1641 or 3010;
        var successCode = result.ExitCode is null || SuccessExitCodes.Contains(result.ExitCode.Value);

        messages.Add(F("UninstallerExitedWithCode", result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"));
        if (stillRegistered)
        {
            messages.Add(L("TheRegistryEntryIsStillPresentTheVendor"));
        }
        else
        {
            messages.Add(L("TheRegistryEntryWasRemoved"));
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
            messages.Add(F("ForcedUninstallWasRefused", guard.Reason));
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
            messages.Add(L("TheUninstallRegistrationEntryWasRemoved"));
            _log.Write("force-uninstall", "delete-key", program.RegistryPath, "completed");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            messages.Add(F("CouldNotRemoveTheRegistrationEntry", ex.Message));
            _log.Write("force-uninstall", "delete-key", program.RegistryPath, "failed", ex.Message);
        }

        var stillRegistered = _registry.KeyExists(program.Location);
        if (!stillRegistered)
        {
            messages.Add(L("RunTheLeftoverScanNextToReviewFiles"));
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
