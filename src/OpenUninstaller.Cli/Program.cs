using System.Text;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Cli;

internal static class Program
{
    private static string AppVersion => AppPaths.CreateDefault().AppVersion;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            PrintHelp();
            return ExitCodes.Success;
        }

        var command = args[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h" or "-?")
        {
            PrintHelp();
            return ExitCodes.Success;
        }

        if (command is "version" or "--version")
        {
            Console.WriteLine($"OpenUninstaller CLI {AppVersion}");
            return ExitCodes.Success;
        }

        ArgumentSet options;
        try
        {
            options = ArgumentSet.Parse(args[1..]);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancelling after the current item…");
        };

        try
        {
            return await DispatchAsync(command, options, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (options.Has("verbose"))
            {
                Console.Error.WriteLine(ex);
            }

            return ExitCodes.Error;
        }
    }

    private static Task<int> DispatchAsync(string command, ArgumentSet args, CancellationToken ct)
    {
        return command switch
        {
            "doctor" => DoctorAsync(args),
            "list" => ListAsync(args),
            "uninstall" => UninstallAsync(args, ct),
            "force-uninstall" => ForceUninstallAsync(args, ct),
            "scan-leftovers" => ScanLeftoversAsync(args, ct),
            "trace-install" => TraceInstallAsync(args, ct),
            "traces" => TracesAsync(args),
            "backups" => BackupsAsync(args, ct),
            "autoruns" => AutorunsAsync(args),
            "junk" => JunkAsync(args, ct),
            "privacy" => PrivacyAsync(args),
            "browser" => BrowserAsync(args, ct),
            "store" or "msix" => StoreAsync(args),
            "shred" => ShredAsync(args),
            "maintenance" => MaintenanceAsync(args, ct),
            "service" => ServiceAsync(args),
            "hunter" => HunterAsync(args),
            "windows-tools" => WindowsToolsAsync(args),
            "wipe-free-space" or "evidence-remover" => WipeFreeSpaceAsync(args, ct),
            _ => UnknownCommand(command)
        };
    }

    private static Task<int> UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return Task.FromResult(ExitCodes.Error);
    }

    // ------------------------------------------------------------------ doctor

    private static Task<int> DoctorAsync(ArgumentSet args)
    {
        var services = AppServices.Create("doctor");
        var report = services.Doctor.Run();
        if (args.Has("json"))
        {
            Output.Json(new
            {
                appVersion = report.AppVersion,
                operatingSystem = report.OperatingSystem,
                isElevated = report.IsElevated,
                dataDirectory = report.DataDirectory,
                dataDirectoryWritable = report.DataDirectoryWritable,
                backupDirectory = report.BackupDirectory,
                traceDirectory = report.TraceDirectory,
                logDirectory = report.LogDirectory,
                programCount = report.ProgramCount,
                traceCount = report.TraceCount,
                backupCount = report.BackupCount,
                checks = report.Checks,
                warnings = report.Warnings,
                healthy = report.Healthy
            });
            return Task.FromResult(report.Healthy ? ExitCodes.Success : ExitCodes.Error);
        }

        Console.WriteLine($"OpenUninstaller {report.AppVersion}");
        Console.WriteLine($"OS:            {report.OperatingSystem}");
        Console.WriteLine($"Elevated:      {(report.IsElevated ? "yes" : "no")}");
        Console.WriteLine($"Data:          {report.DataDirectory}");
        Console.WriteLine($"Writable:      {(report.DataDirectoryWritable ? "yes" : "no")}");
        Console.WriteLine($"Backups:       {report.BackupDirectory} ({report.BackupCount})");
        Console.WriteLine($"Traces:        {report.TraceDirectory} ({report.TraceCount})");
        Console.WriteLine($"Logs:          {report.LogDirectory}");
        Console.WriteLine($"Programs:      {report.ProgramCount}");
        foreach (var check in report.Checks)
        {
            Console.WriteLine($"  ok  {check}");
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"  !   {warning}");
        }

        return Task.FromResult(report.Healthy ? ExitCodes.Success : ExitCodes.Error);
    }

    // -------------------------------------------------------------------- list

    private static Task<int> ListAsync(ArgumentSet args)
    {
        var services = AppServices.Create("list");
        var programs = services.Scanner.Scan(includeSystemComponents: args.Has("all"))
            .ToList();

        var filter = args.Value("filter") ?? args.FirstPositional;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            programs = programs.Where(p =>
                    p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (p.Publisher?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (p.InstallLocation?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        if (args.Has("json"))
        {
            Output.Json(programs.Select(Output.ProgramDto));
            return Task.FromResult(ExitCodes.Success);
        }

        if (args.Has("csv"))
        {
            Console.WriteLine("Name,Version,Publisher,Architecture,SizeBytes,InstallDate,UninstallCommand,RegistryPath");
            foreach (var p in programs)
            {
                Console.WriteLine(string.Join(",", new[]
                {
                    Output.Csv(p.Name),
                    Output.Csv(p.Version),
                    Output.Csv(p.Publisher),
                    Output.Csv(p.ArchitectureDisplay),
                    Output.Csv(p.EstimatedSizeBytes?.ToString()),
                    Output.Csv(p.InstallDate?.ToString("yyyy-MM-dd")),
                    Output.Csv(p.UninstallCommand),
                    Output.Csv(p.RegistryPath)
                }));
            }

            return Task.FromResult(ExitCodes.Success);
        }

        var rows = programs
            .Select(p => new[]
            {
                Output.Shorten(p.Name, 58),
                Output.Shorten(p.Version, 18),
                Output.Shorten(p.Publisher, 30),
                p.SizeDisplay,
                p.ArchitectureDisplay
            })
            .ToList();
        Output.Table(rows, ["Name", "Version", "Publisher", "Size", "Arch"]);
        Console.WriteLine();
        Console.WriteLine($"{programs.Count} application(s). Use --json for machine-readable output.");
        return Task.FromResult(ExitCodes.Success);
    }

    // --------------------------------------------------------------- uninstall

    private static Task<int> ForceUninstallAsync(ArgumentSet args, CancellationToken ct)
    {
        args = CloneWith(args, "force");
        return UninstallAsync(args, ct);
    }

    private static async Task<int> UninstallAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("uninstall");
        var target = AppServices.RequireTarget(args);
        var (program, code) = services.ResolveProgram(target);
        if (program is null)
        {
            return code;
        }

        var silent = args.Has("silent");
        var yes = args.Has("yes");
        var dryRun = args.Has("dry-run");
        var noScan = args.Has("no-scan");
        var force = args.Has("force");
        var timeoutSeconds = args.IntValue("timeout", 1800);
        var timeout = timeoutSeconds is > 0 ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

        if (force && program.IsMachineWide && !services.Elevation.IsElevated)
        {
            Console.Error.WriteLine("A machine-wide forced uninstall requires administrator rights. Re-run from an elevated prompt.");
            return ExitCodes.ElevationRequired;
        }

        var command = UninstallCommandResolver.Resolve(program, silent);
        var progress = args.Has("json") ? null : new ConsoleProgress();

        LeftoverScanResult? preview = null;
        if (!noScan)
        {
            preview = await services.Leftovers.ScanAsync(program, services.Scanner.Scan(), progress, ct).ConfigureAwait(false);
        }

        if (!yes || dryRun)
        {
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    status = dryRun ? "dry-run" : "confirmation-required",
                    program = Output.ProgramDto(program),
                    command = new
                    {
                        display = command.DisplayCommand,
                        executable = command.Executable,
                        arguments = command.Arguments,
                        isWindowsInstaller = command.IsWindowsInstaller,
                        error = command.Error
                    },
                    leftovers = preview?.Items.Select(Output.LeftoverDto),
                    leftoverCount = preview?.Items.Count ?? 0,
                    backupDirectory = services.Paths.BackupsDirectory,
                    note = dryRun
                        ? "Dry run: nothing was uninstalled or deleted."
                        : "Re-run with --yes to run the uninstaller and delete the high-confidence leftovers."
                });
            }
            else
            {
                Console.WriteLine($"Application: {program.Name} {program.Version}".TrimEnd());
                Console.WriteLine($"Registry:    {program.RegistryPath}");
                Console.WriteLine($"Source:      {program.InstallLocation ?? "(no install location)"}");
                Console.WriteLine($"Command:     {command.DisplayCommand}");
                if (command.Error is not null)
                {
                    Console.WriteLine($"Warning:     {command.Error}");
                }

                Console.WriteLine($"Leftovers:   {preview?.Items.Count ?? 0} candidate(s), {preview?.PreSelectedCount ?? 0} high-confidence");
                foreach (var item in preview?.Items.Take(15) ?? [])
                {
                    Console.WriteLine($"  [{(item.IsSelected ? "x" : " ")}] {item.KindLabel,-8} {item.DisplayPath}  ({item.ConfidenceLabel})");
                }

                Console.WriteLine();
                Console.WriteLine(dryRun
                    ? "Dry run: nothing was uninstalled or deleted."
                    : "Re-run with --yes to perform the uninstall. A backup is created before any deletion.");
            }

            return dryRun ? ExitCodes.Success : ExitCodes.ConfirmationRequired;
        }

        UninstallResult uninstall;
        try
        {
            uninstall = await services.Uninstall.RunAsync(
                program,
                new UninstallOptions
                {
                    PreferSilent = silent,
                    Force = force,
                    WaitTimeout = timeout
                },
                progress,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (!args.Has("json"))
        {
            Console.WriteLine(uninstall.Succeeded ? "Vendor uninstall completed." : "Vendor uninstall did not complete cleanly.");
            foreach (var message in uninstall.Messages)
            {
                Console.WriteLine($"  {message}");
            }
        }

        if (uninstall.TimedOut || uninstall.StillRegistered)
        {
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    status = "incomplete",
                    uninstall = ProjectUninstall(uninstall),
                    note = uninstall.TimedOut
                        ? "The vendor uninstaller is still running. Nothing was deleted."
                        : "The registration entry is still present. Nothing was deleted; use --force if appropriate."
                });
            }

            return ExitCodes.Error;
        }

        if (noScan)
        {
            if (args.Has("json"))
            {
                Output.Json(new { status = "uninstalled", uninstall = ProjectUninstall(uninstall) });
            }

            return ExitCodes.Success;
        }

        var scan = await services.Leftovers.ScanAsync(program, services.Scanner.Scan(), progress, ct).ConfigureAwait(false);
        var selected = scan.Items.Where(i => i.IsSelected).ToList();
        LeftoverDeletionResult? deletion = null;
        if (selected.Count > 0)
        {
            deletion = await services.Deletion.DeleteAsync(program.Name, selected, dryRun: false, ct).ConfigureAwait(false);
        }

        if (args.Has("json"))
        {
            Output.Json(new
            {
                status = "completed",
                uninstall = ProjectUninstall(uninstall),
                scan = new
                {
                    usedTrace = scan.UsedTrace,
                    traceId = scan.TraceId,
                    durationMs = (long)scan.Duration.TotalMilliseconds,
                    truncated = scan.Truncated,
                    warnings = scan.Warnings,
                    items = scan.Items.Select(Output.LeftoverDto)
                },
                deletion = deletion is null
                    ? null
                    : new
                    {
                        backupId = deletion.BackupId,
                        backupDirectory = deletion.BackupDirectory,
                        deleted = deletion.DeletedCount,
                        failed = deletion.FailedCount,
                        skipped = deletion.SkippedCount,
                        items = deletion.Items,
                        warnings = deletion.Warnings
                    },
                logPath = services.LogPath
            });
            return ExitCodes.Success;
        }

        Console.WriteLine();
        Console.WriteLine($"Leftovers found: {scan.Items.Count} ({scan.PreSelectedCount} high-confidence)");
        if (deletion is null)
        {
            Console.WriteLine("No high-confidence leftovers were selected. Nothing was deleted.");
        }
        else
        {
            Console.WriteLine($"Deleted: {deletion.DeletedCount}  Failed: {deletion.FailedCount}  Skipped: {deletion.SkippedCount}");
            if (deletion.BackupId is not null)
            {
                Console.WriteLine($"Backup:  {deletion.BackupDirectory}");
                Console.WriteLine($"Restore: oucli backups restore {deletion.BackupId} --yes");
            }

            foreach (var warning in deletion.Warnings)
            {
                Console.WriteLine($"  ! {warning}");
            }
        }

        Console.WriteLine($"Log:     {services.LogPath}");
        return deletion is { Succeeded: false } ? ExitCodes.Error : ExitCodes.Success;
    }

    private static object ProjectUninstall(UninstallResult result) => new
    {
        succeeded = result.Succeeded,
        forced = result.Forced,
        stillRegistered = result.StillRegistered,
        timedOut = result.TimedOut,
        rebootRequired = result.RebootRequired,
        exitCode = result.ExitCode,
        commandLine = result.CommandLine,
        durationMs = (long)result.Duration.TotalMilliseconds,
        messages = result.Messages
    };

    // ---------------------------------------------------------- scan-leftovers

    private static async Task<int> ScanLeftoversAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("scan-leftovers");
        var target = AppServices.RequireTarget(args);
        var (program, code) = services.ResolveProgram(target);
        if (program is null)
        {
            return code;
        }

        var progress = args.Has("json") ? null : new ConsoleProgress();
        var scan = await services.Leftovers.ScanAsync(program, services.Scanner.Scan(), progress, ct).ConfigureAwait(false);

        if (args.Has("delete") && args.Has("ignore"))
        {
            Console.Error.WriteLine("--delete and --ignore cannot be combined in one scan.");
            return ExitCodes.Error;
        }

        LeftoverScanDiff? diff = null;
        if (args.Has("diff"))
        {
            diff = services.History.CompareAndSave(scan, DateTimeOffset.UtcNow);
        }

        var ignoredAdded = 0;
        if (args.Has("ignore"))
        {
            var selected = scan.Items.Where(i => i.IsSelected).ToList();
            ignoredAdded = services.Ignored.Add(program.Id, program.Name, selected, DateTimeOffset.UtcNow);
            scan.IgnoredCount += ignoredAdded;
        }

        LeftoverDeletionResult? deletion = null;
        if (args.Has("delete"))
        {
            if (!args.Has("yes"))
            {
                Console.Error.WriteLine("--delete requires --yes. Use --dry-run to preview.");
                return ExitCodes.ConfirmationRequired;
            }

            var selected = scan.Items.Where(i => i.IsSelected).ToList();
            if (selected.Count > 0)
            {
                deletion = await services.Deletion.DeleteAsync(program.Name, selected, args.Has("dry-run"), ct).ConfigureAwait(false);
            }
        }

        if (args.Has("json"))
        {
            Output.Json(new
            {
                program = Output.ProgramDto(program),
                usedTrace = scan.UsedTrace,
                traceId = scan.TraceId,
                durationMs = (long)scan.Duration.TotalMilliseconds,
                truncated = scan.Truncated,
                ignoredCount = scan.IgnoredCount,
                ignoredAdded,
                diff = diff is null ? null : new
                {
                    hasPrevious = diff.HasPrevious,
                    summary = diff.Summary,
                    newItems = diff.NewItems,
                    resolvedItems = diff.ResolvedItems,
                    persistentItems = diff.PersistentItems
                },
                warnings = scan.Warnings,
                items = scan.Items.Select(Output.LeftoverDto),
                deletion = deletion is null
                    ? null
                    : new
                    {
                        dryRun = deletion.DryRun,
                        backupId = deletion.BackupId,
                        backupDirectory = deletion.BackupDirectory,
                        deleted = deletion.DeletedCount,
                        failed = deletion.FailedCount,
                        items = deletion.Items
                    }
            });
            return ExitCodes.Success;
        }

        Console.WriteLine($"Leftover scan for {program.Name}");
        Console.WriteLine(scan.UsedTrace
            ? $"Evidence: install trace {scan.TraceId}"
            : "Evidence: heuristic only (no install trace available)");
        Console.WriteLine($"Duration: {scan.Duration.TotalSeconds:0.0}s");
        foreach (var warning in scan.Warnings)
        {
            Console.WriteLine($"  ! {warning}");
        }

        if (diff is not null)
        {
            Console.WriteLine($"Changes: {diff.Summary}");
        }

        if (ignoredAdded > 0)
        {
            Console.WriteLine($"Ignored: {ignoredAdded} item(s) will be hidden from future scans.");
        }

        var rows = scan.Items
            .Select(i => new[]
            {
                i.IsSelected ? "x" : " ",
                i.KindLabel,
                Output.Shorten(i.DisplayPath, 80),
                i.ConfidenceLabel,
                i.IsProtected ? "protected" : i.Reason
            })
            .ToList();
        Output.Table(rows, ["Sel", "Kind", "Path", "Evidence", "Reason"]);
        Console.WriteLine();
        Console.WriteLine($"{scan.Items.Count} candidate(s), {scan.PreSelectedCount} preselected.");

        if (deletion is not null)
        {
            Console.WriteLine($"Deleted: {deletion.DeletedCount}  Failed: {deletion.FailedCount}  Backup: {deletion.BackupDirectory ?? "(none)"}");
        }

        return ExitCodes.Success;
    }

    // ------------------------------------------------------------ trace-install

    private static async Task<int> TraceInstallAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("trace-install");
        var installer = args.Value("installer") ?? args.FirstPositional;
        if (string.IsNullOrWhiteSpace(installer))
        {
            throw new UsageException("--installer <path> is required.");
        }

        if (!File.Exists(installer))
        {
            Console.Error.WriteLine($"Installer not found: {installer}");
            return ExitCodes.NotFound;
        }

        var name = args.Value("name") ?? Path.GetFileNameWithoutExtension(installer);
        var minutes = args.IntValue("timeout", 30);
        var progress = args.Has("json") ? null : new ConsoleProgress();
        var trace = await services.TraceInstaller.TraceAsync(
            installer,
            name,
            args.Value("publisher"),
            minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null,
            progress,
            ct).ConfigureAwait(false);

        if (args.Has("json"))
        {
            Output.Json(new
            {
                id = trace.Id,
                programName = trace.ProgramName,
                createdAt = trace.CreatedAt,
                installLocation = trace.InstallLocation,
                registryKeys = trace.KeyCount,
                files = trace.FileCount,
                registryTruncated = trace.RegistryTruncated,
                filesTruncated = trace.FilesTruncated,
                installerExitCode = trace.InstallerExitCode,
                path = Path.Combine(services.Paths.TracesDirectory, trace.Id + ".json")
            });
            return ExitCodes.Success;
        }

        Console.WriteLine($"Trace saved: {trace.Id}");
        Console.WriteLine($"Program:      {trace.ProgramName}");
        Console.WriteLine($"Keys/files:   {trace.KeyCount} / {trace.FileCount}");
        if (trace.InstallLocation is not null)
        {
            Console.WriteLine($"Location:     {trace.InstallLocation}");
        }

        if (trace.RegistryTruncated || trace.FilesTruncated)
        {
            Console.WriteLine("Warning: the trace was truncated; some locations were not indexed.");
        }

        return ExitCodes.Success;
    }

    // ------------------------------------------------------------------ traces

    private static Task<int> TracesAsync(ArgumentSet args)
    {
        var services = AppServices.Create("traces");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "list";
        var rest = args.Positionals.Skip(1).ToList();

        if (sub == "list")
        {
            var traces = services.Traces.List();
            if (args.Has("json"))
            {
                Output.Json(traces.Select(t => new
                {
                    id = t.Id,
                    programName = t.ProgramName,
                    createdAt = t.CreatedAt,
                    registryKeys = t.KeyCount,
                    files = t.FileCount,
                    installLocation = t.InstallLocation,
                    truncated = t.RegistryTruncated || t.FilesTruncated
                }));
                return Task.FromResult(ExitCodes.Success);
            }

            var rows = traces
                .Select(t => new[]
                {
                    t.Id,
                    Output.Shorten(t.ProgramName, 34),
                    t.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    t.KeyCount.ToString(),
                    t.FileCount.ToString()
                })
                .ToList();
            Output.Table(rows, ["Id", "Program", "Created", "Keys", "Files"]);
            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "export")
        {
            var id = rest.ElementAtOrDefault(0);
            var destination = rest.ElementAtOrDefault(1);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(destination))
            {
                throw new UsageException("Usage: oucli traces export <id> <destination.zip>");
            }

            var result = services.TraceBundles.Export(id, destination);
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    succeeded = result.Succeeded,
                    traceId = result.TraceId,
                    path = result.Path,
                    sha256 = result.BundleSha256,
                    error = result.Error
                });
                return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
            }

            if (!result.Succeeded)
            {
                Console.Error.WriteLine(result.Error);
                return Task.FromResult(ExitCodes.Error);
            }

            Console.WriteLine($"Trace bundle: {result.Path}");
            Console.WriteLine($"SHA-256:      {result.BundleSha256}");
            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "import")
        {
            var bundle = rest.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(bundle))
            {
                throw new UsageException("Usage: oucli traces import <bundle.zip>");
            }

            var result = services.TraceBundles.Import(bundle);
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    succeeded = result.Succeeded,
                    id = result.Trace?.Id,
                    programName = result.Trace?.ProgramName,
                    registryKeys = result.Trace?.KeyCount,
                    files = result.Trace?.FileCount,
                    warnings = result.Warnings,
                    error = result.Error
                });
                return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
            }

            if (!result.Succeeded || result.Trace is null)
            {
                Console.Error.WriteLine(result.Error ?? "The trace bundle could not be imported.");
                return Task.FromResult(ExitCodes.Error);
            }

            Console.WriteLine($"Imported trace: {result.Trace.Id}");
            Console.WriteLine($"Program:        {result.Trace.ProgramName}");
            Console.WriteLine($"Keys/files:     {result.Trace.KeyCount} / {result.Trace.FileCount}");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"  ! {warning}");
            }

            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "delete")
        {
            var id = rest.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new UsageException("Usage: oucli traces delete <id> --yes");
            }

            if (!args.Has("yes"))
            {
                Console.Error.WriteLine("Deleting a trace requires --yes.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            var deleted = services.Traces.Delete(id);
            Console.WriteLine(deleted ? $"Deleted trace {id}." : $"Trace {id} was not found or could not be deleted.");
            return Task.FromResult(deleted ? ExitCodes.Success : ExitCodes.NotFound);
        }

        throw new UsageException("Unknown traces subcommand. Use list, export, import or delete.");
    }

    // ----------------------------------------------------------------- backups

    private static async Task<int> BackupsAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("backups");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "list";
        var rest = args.Positionals.Skip(1).ToList();

        switch (sub)
        {
            case "list":
            {
                var backups = services.Backups.List();
                if (args.Has("json"))
                {
                    Output.Json(backups.Select(b => new
                    {
                        id = b.Id,
                        programName = b.ProgramName,
                        createdAt = b.CreatedAt,
                        registryEntries = b.RegistryCount,
                        fileEntries = b.FileCount,
                        totalBytes = b.TotalBytes,
                        complete = b.Manifest.Complete,
                        directory = b.Directory
                    }));
                    return ExitCodes.Success;
                }

                var rows = backups
                    .Select(b => new[]
                    {
                        Output.Shorten(b.Id, 50),
                        Output.Shorten(b.ProgramName, 30),
                        b.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        b.RegistryCount.ToString(),
                        b.FileCount.ToString(),
                        InstalledProgram.FormatBytes(b.TotalBytes),
                        b.Manifest.Complete ? "yes" : "partial"
                    })
                    .ToList();
                Output.Table(rows, ["Backup", "Program", "Created", "Keys", "Files", "Size", "Complete"]);
                return ExitCodes.Success;
            }

            case "preview":
            {
                var id = rest.FirstOrDefault() ?? args.Value("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new UsageException("Usage: oucli backups preview <id> [--overwrite] [--json]");
                }

                var preview = services.Backups.PreviewRestore(id, args.Has("overwrite"));
                if (preview.Entries.Count == 0 && preview.Warnings.Count > 0)
                {
                    foreach (var warning in preview.Warnings)
                    {
                        Console.Error.WriteLine(warning);
                    }

                    return ExitCodes.NotFound;
                }

                if (args.Has("json"))
                {
                    Output.Json(new
                    {
                        backupId = preview.BackupId,
                        overwrite = preview.Overwrite,
                        canRestore = preview.CanRestore,
                        summary = preview.Summary,
                        entries = preview.Entries.Select(e => new
                        {
                            target = e.Target,
                            kind = e.Kind,
                            action = e.ActionLabel,
                            detail = e.Detail,
                            bytes = e.Bytes
                        }),
                        warnings = preview.Warnings
                    });
                    return ExitCodes.Success;
                }

                foreach (var entry in preview.Entries)
                {
                    Console.WriteLine($"{entry.ActionLabel,-10} {entry.Kind,-15} {entry.Target}");
                    if (!string.IsNullOrWhiteSpace(entry.Detail))
                    {
                        Console.WriteLine($"           {entry.Detail}");
                    }
                }

                foreach (var warning in preview.Warnings)
                {
                    Console.WriteLine($"  ! {warning}");
                }

                Console.WriteLine(preview.Summary);
                return ExitCodes.Success;
            }

            case "restore":
            {
                var id = rest.FirstOrDefault() ?? args.Value("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new UsageException("Usage: oucli backups restore <id> [--overwrite] --yes");
                }

                if (!args.Has("yes"))
                {
                    Console.Error.WriteLine("Restoring a backup requires --yes.");
                    return ExitCodes.ConfirmationRequired;
                }

                var result = await services.Backups.RestoreAsync(id, args.Has("overwrite"), ct).ConfigureAwait(false);
                if (args.Has("json"))
                {
                    Output.Json(new
                    {
                        backupId = result.BackupId,
                        succeeded = result.Succeeded,
                        restored = result.RestoredCount,
                        failed = result.FailedCount,
                        entries = result.Entries,
                        warnings = result.Warnings,
                        durationMs = (long)result.Duration.TotalMilliseconds
                    });
                    return result.Succeeded ? ExitCodes.Success : ExitCodes.Error;
                }

                foreach (var entry in result.Entries)
                {
                    Console.WriteLine($"{entry.Status,-8} {entry.Target} {entry.Detail}".TrimEnd());
                }

                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"  ! {warning}");
                }

                Console.WriteLine($"Restored {result.RestoredCount} item(s); {result.FailedCount} failed.");
                return result.Succeeded ? ExitCodes.Success : ExitCodes.Error;
            }

            case "prune":
            {
                if (!args.Has("yes"))
                {
                    Console.Error.WriteLine("Pruning backups requires --yes.");
                    return ExitCodes.ConfirmationRequired;
                }

                var keep = args.IntValue("keep", 20) ?? 20;
                var removed = services.Backups.Prune(keep);
                Console.WriteLine($"Removed {removed} backup(s); kept the newest {Math.Max(1, keep)}.");
                return ExitCodes.Success;
            }

            default:
                throw new UsageException("Unknown backups subcommand. Use list, preview, restore or prune.");
        }
    }

    // ----------------------------------------------------------------- autoruns

    private static Task<int> AutorunsAsync(ArgumentSet args)
    {
        var services = AppServices.Create("autoruns");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "list";
        var rest = args.Positionals.Skip(1).ToList();

        if (sub == "list")
        {
            var scan = services.Autoruns.Scan();
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    enabled = scan.EnabledCount,
                    disabled = scan.DisabledCount,
                    warnings = scan.Warnings,
                    entries = scan.Entries.Select(e => new
                    {
                        id = e.Id,
                        name = e.Name,
                        command = e.Command,
                        source = e.SourceLabel,
                        enabled = e.IsEnabled,
                        publisher = e.Publisher,
                        location = e.LocationDisplay
                    })
                });
                return Task.FromResult(ExitCodes.Success);
            }

            var rows = scan.Entries
                .Select(e => new[]
                {
                    e.IsEnabled ? "on" : "off",
                    Output.Shorten(e.Name, 28),
                    Output.Shorten(e.Command, 72),
                    e.SourceLabel,
                    Output.Shorten(e.Publisher, 20)
                })
                .ToList();
            Output.Table(rows, ["State", "Name", "Command", "Source", "Publisher"]);
            Console.WriteLine();
            Console.WriteLine($"{scan.EnabledCount} enabled, {scan.DisabledCount} disabled.");
            foreach (var warning in scan.Warnings)
            {
                Console.WriteLine($"  ! {warning}");
            }

            return Task.FromResult(ExitCodes.Success);
        }

        if (sub is "disable" or "enable")
        {
            var id = rest.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new UsageException($"Usage: oucli autoruns {sub} <id> --yes");
            }

            if (!args.Has("yes"))
            {
                Console.Error.WriteLine($"Changing an autorun entry requires --yes.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            string? error;
            var success = sub == "disable"
                ? services.Autoruns.Disable(id, out error)
                : services.Autoruns.Enable(id, out error);
            if (success)
            {
                Console.WriteLine($"Autorun entry {id} {sub}d.");
                return Task.FromResult(ExitCodes.Success);
            }

            Console.Error.WriteLine(error);
            return Task.FromResult(ExitCodes.Error);
        }

        if (sub == "history")
        {
            var history = services.Autoruns.GetHistory();
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    total = history.Count,
                    entries = history.Select(h => new
                    {
                        id = h.Id,
                        entryId = h.EntryId,
                        name = h.Name,
                        command = h.Command,
                        source = h.SourceLabel,
                        action = h.ActionLabel,
                        timestamp = h.TimestampDisplay,
                        previousState = h.PreviousState,
                        newState = h.NewState,
                        detail = h.Detail,
                        canRollback = h.CanRollback
                    })
                });
                return Task.FromResult(ExitCodes.Success);
            }

            var rows = history
                .Select(h => new[]
                {
                    h.TimestampDisplay,
                    h.ActionLabel,
                    Output.Shorten(h.Name, 26),
                    h.SourceLabel,
                    Output.Shorten(h.Detail ?? h.Command, 36),
                    h.CanRollback ? "yes" : "no"
                })
                .ToList();
            Output.Table(rows, ["Timestamp", "Action", "Name", "Source", "Details", "Rollback"]);
            Console.WriteLine();
            Console.WriteLine($"{history.Count} history records found.");
            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "rollback")
        {
            var targetId = rest.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new UsageException("Usage: oucli autoruns rollback <id|all> --yes");
            }

            if (!args.Has("yes"))
            {
                Console.Error.WriteLine($"Rolling back autorun state requires --yes.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            if (string.Equals(targetId, "all", StringComparison.OrdinalIgnoreCase))
            {
                var res = services.Autoruns.RollbackAll();
                if (args.Has("json"))
                {
                    Output.Json(new
                    {
                        succeeded = res.Succeeded,
                        restoredCount = res.SucceededCount,
                        failedCount = res.FailedCount,
                        messages = res.Messages
                    });
                }
                else
                {
                    Console.WriteLine($"Rollback complete: {res.SucceededCount} restored, {res.FailedCount} failed.");
                    foreach (var msg in res.Messages)
                    {
                        Console.WriteLine($"  {msg}");
                    }
                }
                return Task.FromResult(res.Succeeded ? ExitCodes.Success : ExitCodes.Error);
            }

            var success = services.Autoruns.Rollback(targetId, out var error);
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    id = targetId,
                    succeeded = success,
                    error = error
                });
            }
            else
            {
                if (success)
                {
                    Console.WriteLine($"Autorun entry '{targetId}' successfully rolled back (re-enabled).");
                }
                else
                {
                    Console.Error.WriteLine($"Rollback failed: {error}");
                }
            }
            return Task.FromResult(success ? ExitCodes.Success : ExitCodes.Error);
        }

        throw new UsageException("Unknown autoruns subcommand. Use list, disable, enable, history, or rollback.");
    }

    // --------------------------------------------------------------------- junk

    private static Task<int> JunkAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("junk");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "scan";
        var categoryArgs = args.Positionals.Skip(1).ToList();

        if (sub == "scan")
        {
            var scan = services.Junk.Scan(ct);
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    totalBytes = scan.TotalBytes,
                    totalItems = scan.TotalItems,
                    categories = scan.Categories.Select(c => new
                    {
                        id = c.Id,
                        title = c.Title,
                        bytes = c.Bytes,
                        items = c.ItemCount,
                        canClean = c.CanClean,
                        requiresElevation = c.RequiresElevation,
                        warnings = c.Warnings
                    })
                });
                return Task.FromResult(ExitCodes.Success);
            }

            var rows = scan.Categories
                .Select(c => new[]
                {
                    c.Id,
                    c.Title,
                    c.SizeDisplay,
                    c.ItemCount.ToString(),
                    c.CanClean ? "yes" : "report only"
                })
                .ToList();
            Output.Table(rows, ["Category", "Title", "Size", "Items", "Cleanable"]);
            Console.WriteLine();
            Console.WriteLine($"Total: {InstalledProgram.FormatBytes(scan.TotalBytes)} in {scan.TotalItems} item(s).");
            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "clean")
        {
            var categories = categoryArgs
                .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
            if (categories.Count == 0)
            {
                Console.Error.WriteLine("Specify at least one category (for example user-temp) or use 'junk scan' first.");
                return Task.FromResult(ExitCodes.Error);
            }

            var dryRun = args.Has("dry-run");
            if (!dryRun && !args.Has("yes"))
            {
                Console.Error.WriteLine("Cleaning junk requires --yes. Use --dry-run to preview.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            var result = services.Junk.Clean(categories, dryRun, ct);
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    dryRun = result.DryRun,
                    freedBytes = result.FreedBytes,
                    deleted = result.DeletedItems,
                    skipped = result.SkippedItems,
                    failed = result.FailedItems,
                    succeeded = result.Succeeded,
                    messages = result.Messages,
                    durationMs = (long)result.Duration.TotalMilliseconds
                });
                return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
            }

            Console.WriteLine($"{(dryRun ? "Would free" : "Freed")} {InstalledProgram.FormatBytes(result.FreedBytes)} across {result.DeletedItems} item(s).");
            Console.WriteLine($"Skipped: {result.SkippedItems}  Failed: {result.FailedItems}");
            foreach (var message in result.Messages)
            {
                Console.WriteLine($"  {message}");
            }

            return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
        }

        throw new UsageException("Unknown junk subcommand. Use scan or clean.");
    }

    // ------------------------------------------------------------------ privacy

    // ----------------------------------------------------------------- browser

    private static Task<int> BrowserAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("browser");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "scan";
        if (sub is not ("scan" or "clean"))
        {
            throw new UsageException("Use 'browser scan' or 'browser clean'.");
        }

        var scan = services.Browser.Scan();
        if (sub == "scan")
        {
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    profiles = scan.Profiles.Select(Dto),
                    totalBytes = scan.TotalBytes,
                    totalFiles = scan.TotalFiles,
                    warnings = scan.Warnings
                });
                return Task.FromResult(ExitCodes.Success);
            }

            Output.Table(
                scan.Profiles.Select(p => new[]
                {
                    p.DisplayName,
                    p.Detail,
                    p.SizeDisplay,
                    p.ProfilePath
                }).ToList(),
                ["Profile", "Data", "Size", "Path"]);
            Console.WriteLine();
            Console.WriteLine($"{scan.TotalFiles} file(s), {InstalledProgram.FormatBytes(scan.TotalBytes)}.");
            Console.WriteLine("Only history and cookie databases are listed; passwords, bookmarks and autofill are never touched.");
            return Task.FromResult(ExitCodes.Success);
        }

        var filter = args.Value("browser");
        var profiles = scan.Profiles
            .Where(p => filter is null || p.Browser.Equals(filter, StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (profiles.Count == 0)
        {
            Console.Error.WriteLine("No matching browser profile data was found.");
            return Task.FromResult(ExitCodes.NotFound);
        }

        var dryRun = args.Has("dry-run");
        if (!dryRun && !args.Has("yes"))
        {
            Console.Error.WriteLine(
                "Deleting browser history and cookies is irreversible and requires --yes. Use --dry-run to preview.");
            return Task.FromResult(ExitCodes.ConfirmationRequired);
        }

        ct.ThrowIfCancellationRequested();
        var result = services.Browser.Clean(profiles, dryRun);
        if (args.Has("json"))
        {
            Output.Json(new
            {
                dryRun = result.DryRun,
                deletedFiles = result.DeletedFiles,
                bytes = result.Bytes,
                skipped = result.Skipped,
                messages = result.Messages
            });
            return Task.FromResult(result.Messages.Count == 0 ? ExitCodes.Success : ExitCodes.Error);
        }

        Console.WriteLine(result.Summary);
        foreach (var message in result.Messages)
        {
            Console.WriteLine($"  {message}");
        }

        if (dryRun)
        {
            Console.WriteLine("Dry run: nothing was deleted.");
        }

        return Task.FromResult(result.Messages.Count == 0 ? ExitCodes.Success : ExitCodes.Error);

        static object Dto(BrowserProfile p) => new
        {
            browser = p.Browser,
            profile = p.ProfileName,
            path = p.ProfilePath,
            bytes = p.TotalBytes,
            files = p.Files.Select(f => new
            {
                kind = f.KindLabel,
                path = f.Path,
                bytes = f.Bytes
            })
        };
    }

    // ------------------------------------------------------------------- store

    private static async Task<int> StoreAsync(ArgumentSet args)
    {
        var services = AppServices.Create("store");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "list";

        if (sub == "list")
        {
            var packages = services.Msix.Scan();
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    count = packages.Count,
                    packages = packages.Select(p => new
                    {
                        displayName = p.DisplayName,
                        name = p.Name,
                        version = p.Version,
                        publisher = p.Publisher,
                        architecture = p.Architecture,
                        kind = p.KindDisplay,
                        packageFullName = p.PackageFullName,
                        installLocation = p.InstallLocation
                    })
                });
                return ExitCodes.Success;
            }

            Output.Table(
                packages.Select(p => new[]
                {
                    Output.Shorten(p.DisplayName, 40),
                    p.Version ?? "—",
                    p.Architecture ?? "—",
                    p.KindDisplay,
                    Output.Shorten(p.Publisher ?? "—", 30)
                }).ToList(),
                ["Package", "Version", "Arch", "Kind", "Publisher"]);
            Console.WriteLine();
            Console.WriteLine($"{packages.Count} MSIX package(s).");
            return ExitCodes.Success;
        }

        var target = args.Positionals.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new UsageException($"Usage: oucli store {sub} <packageFullName> [--yes]");
        }

        var targetPkg = services.Msix.Scan().FirstOrDefault(p =>
            p.PackageFullName.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase));

        var fullName = targetPkg?.PackageFullName ?? target;

        if (sub == "terminate")
        {
            Console.WriteLine($"Terminating processes for '{fullName}'…");
            var result = await services.Msix.TerminatePackageAsync(fullName).ConfigureAwait(false);
            if (result.Success)
            {
                Console.WriteLine("Package processes terminated successfully.");
                return ExitCodes.Success;
            }

            Console.Error.WriteLine(result.Error ?? "Failed to terminate package processes.");
            return ExitCodes.Error;
        }

        if (!args.Has("yes"))
        {
            Console.Error.WriteLine($"Action '{sub}' on '{fullName}' requires confirmation (--yes).");
            return ExitCodes.ConfirmationRequired;
        }

        if (sub == "uninstall")
        {
            Console.WriteLine($"Uninstalling MSIX package '{fullName}'…");
            var result = await services.Msix.UninstallPackageAsync(fullName).ConfigureAwait(false);
            if (result.Success)
            {
                Console.WriteLine("Package uninstalled successfully.");
                return ExitCodes.Success;
            }

            Console.Error.WriteLine(result.Error ?? "Failed to uninstall package.");
            return ExitCodes.Error;
        }

        if (sub == "reset")
        {
            Console.WriteLine($"Resetting MSIX package data for '{fullName}'…");
            var result = await services.Msix.ResetPackageAsync(fullName).ConfigureAwait(false);
            if (result.Success)
            {
                Console.WriteLine("Package reset successfully.");
                return ExitCodes.Success;
            }

            Console.Error.WriteLine(result.Error ?? "Failed to reset package.");
            return ExitCodes.Error;
        }

        throw new UsageException("Unknown store subcommand. Use 'list', 'uninstall', 'reset', or 'terminate'.");
    }

    private static Task<int> PrivacyAsync(ArgumentSet args)
    {
        var services = AppServices.Create("privacy");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "scan";
        var rest = args.Positionals.Skip(1).ToList();

        if (sub == "scan")
        {
            var scan = services.Privacy.Scan();
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    totalBytes = scan.TotalBytes,
                    totalItems = scan.TotalItems,
                    categories = scan.Categories.Select(c => new
                    {
                        id = c.Id,
                        title = c.Title,
                        bytes = c.Bytes,
                        items = c.ItemCount,
                        warning = c.Warning
                    })
                });
                return Task.FromResult(ExitCodes.Success);
            }

            Output.Table(
                scan.Categories.Select(c => new[]
                {
                    c.Id,
                    c.Title,
                    c.SizeDisplay,
                    c.ItemCount.ToString()
                }).ToList(),
                ["Category", "Title", "Size", "Items"]);
            Console.WriteLine();
            Console.WriteLine($"Total: {InstalledProgram.FormatBytes(scan.TotalBytes)} in {scan.TotalItems} item(s).");
            Console.WriteLine("Privacy cleanup is irreversible; the app deliberately keeps no backup of these traces.");
            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "history")
        {
            var appHistory = services.Privacy.Scan().Categories.FirstOrDefault(c => c.Id == "app-history");
            if (!args.Has("clear"))
            {
                Console.WriteLine($"Scan history: {appHistory?.ItemCount ?? 0} file(s), " +
                                  $"{InstalledProgram.FormatBytes(appHistory?.Bytes ?? 0)}.");
                Console.WriteLine("Use --clear --yes to remove scan snapshots, ignored ids and operation logs.");
                return Task.FromResult(ExitCodes.Success);
            }

            if (!args.Has("yes"))
            {
                Console.Error.WriteLine("Clearing the scan history requires --yes.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            var cleared = services.Privacy.Clean(["app-history"], dryRun: false);
            Console.WriteLine($"Removed {cleared.ItemsDeleted} file(s), {InstalledProgram.FormatBytes(cleared.Bytes)}.");
            foreach (var message in cleared.Messages)
            {
                Console.WriteLine($"  {message}");
            }

            return Task.FromResult(cleared.Succeeded ? ExitCodes.Success : ExitCodes.Error);
        }

        if (sub == "clean")
        {
            if (rest.Count == 0)
            {
                Console.Error.WriteLine("Specify at least one category, or use 'privacy scan' first.");
                return Task.FromResult(ExitCodes.Error);
            }

            var dryRun = args.Has("dry-run");
            if (!dryRun && !args.Has("yes"))
            {
                Console.Error.WriteLine("Cleaning Windows history requires --yes. Use --dry-run to preview.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            var result = services.Privacy.Clean(rest, dryRun);
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    dryRun = result.DryRun,
                    bytes = result.Bytes,
                    deleted = result.ItemsDeleted,
                    failed = result.FailedItems,
                    succeeded = result.Succeeded,
                    messages = result.Messages,
                    durationMs = (long)result.Duration.TotalMilliseconds
                });
                return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
            }

            Console.WriteLine($"{(dryRun ? "Would remove" : "Removed")} {result.ItemsDeleted} item(s), " +
                              $"{InstalledProgram.FormatBytes(result.Bytes)}.");
            Console.WriteLine($"Failed: {result.FailedItems}");
            foreach (var message in result.Messages)
            {
                Console.WriteLine($"  {message}");
            }

            return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
        }

        throw new UsageException("Unknown privacy subcommand. Use scan, clean or history.");
    }

    // -------------------------------------------------------------------- shred

    private static Task<int> ShredAsync(ArgumentSet args)
    {
        var services = AppServices.Create("shred");
        if (args.Positionals.Count == 0)
        {
            throw new UsageException("Usage: oucli shred <path> [<path>...] [--passes N] [--dry-run] [--yes] [--json]");
        }

        var dryRun = args.Has("dry-run");
        if (!dryRun && !args.Has("yes"))
        {
            Console.Error.WriteLine("Shredding is irreversible and requires --yes. Use --dry-run to preview.");
            return Task.FromResult(ExitCodes.ConfirmationRequired);
        }

        var passes = args.IntValue("passes", 3) ?? 3;
        var result = services.Shredder.Shred(args.Positionals, passes, dryRun);
        if (args.Has("json"))
        {
            Output.Json(new
            {
                dryRun = result.DryRun,
                passes = result.Passes,
                shreddedFiles = result.ShreddedFiles,
                shreddedBytes = result.ShreddedBytes,
                deletedDirectories = result.DeletedDirectories,
                skipped = result.Skipped,
                failed = result.Failed,
                rejected = result.Rejected,
                succeeded = result.Succeeded,
                durationMs = (long)result.Duration.TotalMilliseconds
            });
            return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
        }

        foreach (var rejected in result.Rejected)
        {
            Console.WriteLine($"  rejected: {rejected}");
        }

        foreach (var skipped in result.Skipped)
        {
            Console.WriteLine($"  skipped:  {skipped}");
        }

        foreach (var failed in result.Failed)
        {
            Console.WriteLine($"  failed:   {failed}");
        }

        Console.WriteLine($"{(dryRun ? "Would shred" : "Shredded")} {result.ShreddedFiles} file(s), " +
                          $"{InstalledProgram.FormatBytes(result.ShreddedBytes)} with {result.Passes} pass(es).");
        if (result.DeletedDirectories > 0)
        {
            Console.WriteLine($"Removed {result.DeletedDirectories} empty folder(s).");
        }

        return Task.FromResult(result.Succeeded ? ExitCodes.Success : ExitCodes.Error);
    }

    // --------------------------------------------------------------- maintenance

    private static async Task<int> MaintenanceAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("maintenance");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "run";
        if (sub != "run")
        {
            throw new UsageException("Unknown maintenance subcommand. Use run.");
        }

        var settings = MaintenanceSettings.Load(services.Paths);
        var result = await services.Maintenance.RunOnceAsync(settings, ct).ConfigureAwait(false);
        if (args.Has("json"))
        {
            Output.Json(new
            {
                ranAt = result.RanAt,
                succeeded = result.Succeeded,
                cancelled = result.Cancelled,
                steps = result.Steps,
                warnings = result.Warnings
            });
            return result.Succeeded ? ExitCodes.Success : ExitCodes.Error;
        }

        foreach (var step in result.Steps)
        {
            Console.WriteLine($"  {step}");
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"  ! {warning}");
        }

        Console.WriteLine(result.Cancelled ? "Cancelled." : result.Succeeded ? "Maintenance complete." : "Maintenance finished with warnings.");
        return result.Succeeded ? ExitCodes.Success : ExitCodes.Error;
    }

    // ------------------------------------------------------------------- service

    private static Task<int> ServiceAsync(ArgumentSet args)
    {
        var services = AppServices.Create("service");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "status";

        if (sub == "status")
        {
            var status = services.Background.Query();
            if (args.Has("json"))
            {
                Output.Json(new
                {
                    serviceName = services.Background.ServiceName,
                    installed = status.Installed,
                    running = status.Running,
                    state = status.State,
                    detail = status.Detail,
                    executable = status.ExecutablePath,
                    executableExists = status.ExecutableExists
                });
                return Task.FromResult(status.Installed ? ExitCodes.Success : ExitCodes.NotFound);
            }

            Console.WriteLine($"Service:    {services.Background.ServiceName}");
            Console.WriteLine($"Installed:  {(status.Installed ? "yes" : "no")}");
            Console.WriteLine($"State:      {status.Label}");
            Console.WriteLine($"Executable: {status.ExecutablePath}" +
                              (status.ExecutableExists ? string.Empty : " (missing)"));
            if (!string.IsNullOrWhiteSpace(status.Detail))
            {
                Console.WriteLine(status.Detail);
            }

            return Task.FromResult(status.Installed ? ExitCodes.Success : ExitCodes.NotFound);
        }

        if (sub is "install" or "remove" or "start" or "stop")
        {
            if (!services.Elevation.IsElevated)
            {
                Console.Error.WriteLine("Managing the service requires an elevated shell.");
                return Task.FromResult(ExitCodes.ElevationRequired);
            }

            if (sub is "install" or "remove" && !args.Has("yes"))
            {
                Console.Error.WriteLine($"Service {sub} requires --yes.");
                return Task.FromResult(ExitCodes.ConfirmationRequired);
            }

            string? error;
            var success = sub switch
            {
                "install" => services.Background.Install(out error),
                "remove" => services.Background.Remove(out error),
                "start" => services.Background.Start(out error),
                _ => services.Background.Stop(out error)
            };
            if (!success)
            {
                Console.Error.WriteLine(error);
                return Task.FromResult(ExitCodes.Error);
            }

            Console.WriteLine($"Service {sub}{(sub.EndsWith('e') ? "d" : "ed")}.");
            return Task.FromResult(ExitCodes.Success);
        }

        throw new UsageException("Unknown service subcommand. Use status, install, remove, start or stop.");
    }

    // ------------------------------------------------------------------- hunter

    private static Task<int> HunterAsync(ArgumentSet args)
    {
        var services = AppServices.Create("hunter");
        var programs = services.Scanner.Scan();
        var match = HunterService.MatchCursor(programs);
        if (args.Has("json"))
        {
            Output.Json(new
            {
                matched = match.Matched,
                program = match.Program is null ? null : Output.ProgramDto(match.Program),
                exePath = match.ExePath,
                processName = match.ProcessName,
                processId = match.ProcessId,
                windowTitle = match.WindowTitle,
                score = match.Score,
                reason = match.Reason
            });
            return Task.FromResult(match.Matched ? ExitCodes.Success : ExitCodes.NotFound);
        }

        if (match.ExePath is not null)
        {
            Console.WriteLine($"Window:   {match.WindowTitle}");
            Console.WriteLine($"Process:  {match.ProcessName} (PID {match.ProcessId})");
            Console.WriteLine($"Exe:      {match.ExePath}");
        }

        Console.WriteLine(match.Matched
            ? $"Matched:  {match.Program!.Name}\nReason:   {match.Reason} (score {match.Score})"
            : $"No match: {match.Reason}");
        return Task.FromResult(match.Matched ? ExitCodes.Success : ExitCodes.NotFound);
    }

    // ------------------------------------------------------------ windows-tools

    private static Task<int> WindowsToolsAsync(ArgumentSet args)
    {
        var services = AppServices.Create("windows-tools");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "list";

        if (sub == "list" || args.Positionals.Count == 0)
        {
            var tools = services.WindowsTools.GetAllTools();
            if (args.Has("json"))
            {
                Output.Json(tools.Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    command = t.Command,
                    arguments = t.Arguments,
                    category = t.Category,
                    description = t.Description,
                    requiresElevation = t.RequiresElevation,
                    isCustom = t.IsCustom
                }));
                return Task.FromResult(ExitCodes.Success);
            }

            Output.Table(
                tools.Select(t => new[] { t.Id, t.Name, t.Category, t.Command, t.Description }).ToList(),
                ["ID", "Tool", "Category", "Command", "Description"]);
            return Task.FromResult(ExitCodes.Success);
        }

        if (sub == "launch")
        {
            var targetId = args.Positionals.Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new UsageException("Usage: oucli windows-tools launch <id|name>");
            }

            var tool = services.WindowsTools.GetAllTools().FirstOrDefault(t =>
                t.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase) ||
                t.Name.Equals(targetId, StringComparison.OrdinalIgnoreCase));

            if (tool is null)
            {
                Console.Error.WriteLine($"Tool '{targetId}' not found.");
                return Task.FromResult(ExitCodes.NotFound);
            }

            try
            {
                var startInfo = services.WindowsTools.CreateStartInfo(tool);
                System.Diagnostics.Process.Start(startInfo);
                Console.WriteLine($"Launched {tool.Name}.");
                return Task.FromResult(ExitCodes.Success);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to launch {tool.Name}: {ex.Message}");
                return Task.FromResult(ExitCodes.Error);
            }
        }

        throw new UsageException("Unknown windows-tools subcommand. Use 'list' or 'launch <id>'.");
    }

    // ----------------------------------------------------------- wipe-free-space

    private static async Task<int> WipeFreeSpaceAsync(ArgumentSet args, CancellationToken ct)
    {
        var services = AppServices.Create("wipe-free-space");
        var sub = args.FirstPositional?.ToLowerInvariant() ?? "list";

        if (sub == "list" || args.Positionals.Count == 0)
        {
            var drives = services.EvidenceRemover.GetAvailableDrives();
            if (args.Has("json"))
            {
                Output.Json(drives.Select(d => new
                {
                    drive = d.DriveLetter,
                    name = d.VolumeLabel,
                    type = d.DriveType.ToString(),
                    freeBytes = d.FreeBytes,
                    totalBytes = d.TotalBytes,
                    freeSpace = d.FreeSpaceDisplay,
                    totalSpace = d.TotalSizeDisplay
                }));
                return ExitCodes.Success;
            }

            Output.Table(
                drives.Select(d => new[]
                {
                    d.DriveLetter,
                    d.VolumeLabel,
                    d.DriveType.ToString(),
                    d.FreeSpaceDisplay,
                    d.TotalSizeDisplay
                }).ToList(),
                ["Drive", "Volume", "Type", "Free Space", "Total Size"]);
            Console.WriteLine();
            Console.WriteLine("Use 'oucli wipe-free-space wipe <drive-letter> --yes' to wipe free space.");
            return ExitCodes.Success;
        }

        var driveArg = sub == "wipe" ? args.Positionals.Skip(1).FirstOrDefault() : sub;
        if (string.IsNullOrWhiteSpace(driveArg))
        {
            throw new UsageException("Usage: oucli wipe-free-space <drive-letter> [--chunk-mb 512] [--fill-byte 0] [--yes] [--dry-run]");
        }

        driveArg = driveArg.TrimEnd(':', '\\', '/');
        var available = services.EvidenceRemover.GetAvailableDrives();
        var targetDrive = available.FirstOrDefault(d => d.DriveLetter.StartsWith(driveArg, StringComparison.OrdinalIgnoreCase));
        if (targetDrive is null)
        {
            Console.Error.WriteLine($"Drive '{driveArg}:' was not found or is not ready.");
            return ExitCodes.NotFound;
        }

        if (args.Has("dry-run"))
        {
            Console.WriteLine($"Dry run: Would wipe {targetDrive.FreeSpaceDisplay} of unallocated free space on {targetDrive.DisplayName}.");
            return ExitCodes.Success;
        }

        if (!args.Has("yes"))
        {
            Console.Error.WriteLine($"Wiping drive free space on {targetDrive.DisplayName} is unrecoverable and requires --yes.");
            return ExitCodes.ConfirmationRequired;
        }

        var progress = new Progress<WipeProgress>(p =>
        {
            var eta = p.EstimatedTimeRemaining.HasValue ? p.EstimatedTimeRemaining.Value.ToString(@"mm\:ss") : "--:--";
            Console.Write($"\rWiping free space: {p.PercentComplete:F1}% ({InstalledProgram.FormatBytes(p.BytesWiped)} / {InstalledProgram.FormatBytes(p.TotalBytesToWipe)}) ETA: {eta}   ");
        });

        Console.WriteLine($"Starting free space wipe on {targetDrive.DisplayName} ({targetDrive.FreeSpaceDisplay})...");
        var result = await services.EvidenceRemover.WipeDriveAsync(targetDrive.DriveName, progress, ct).ConfigureAwait(false);
        Console.WriteLine();

        if (args.Has("json"))
        {
            Output.Json(new
            {
                drive = result.DriveLetter,
                bytesWiped = result.BytesWiped,
                duration = result.Duration,
                succeeded = result.Succeeded,
                errorMessage = result.ErrorMessage
            });
        }
        else
        {
            Console.WriteLine(result.Succeeded
                ? $"Successfully wiped {result.BytesWipedDisplay} in {result.Duration:mm\\:ss}."
                : $"Wipe failed: {result.ErrorMessage}");
        }

        return result.Succeeded ? ExitCodes.Success : ExitCodes.Error;
    }

    // -------------------------------------------------------------------- help

    private static ArgumentSet CloneWith(ArgumentSet source, string flag)
    {
        var builder = new List<string>();
        foreach (var positional in source.Positionals)
        {
            builder.Add(positional);
        }

        builder.Add("--" + flag);
        if (source.Has("yes"))
        {
            builder.Add("--yes");
        }

        if (source.Has("json"))
        {
            builder.Add("--json");
        }

        if (source.Has("silent"))
        {
            builder.Add("--silent");
        }

        if (source.Has("dry-run"))
        {
            builder.Add("--dry-run");
        }

        if (source.Has("no-scan"))
        {
            builder.Add("--no-scan");
        }

        if (source.Value("timeout") is { } timeout)
        {
            builder.Add("--timeout=" + timeout);
        }

        return ArgumentSet.Parse([.. builder]);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            OpenUninstaller CLI {AppVersion} — deep uninstall, leftovers, backups and maintenance.

            Usage: oucli <command> [options]

            Commands:
              list [filter]                 List installed applications (--json, --csv, --all)
              doctor                        Check environment, permissions and stores (--json)
              uninstall <name|id>           Run the vendor uninstaller, scan leftovers, delete approved items
                  [--silent] [--yes] [--force] [--no-scan] [--dry-run] [--timeout seconds] [--json]
              force-uninstall <name|id>     Remove the registration (requires --yes)
              scan-leftovers <name|id>      Show leftover candidates (--json, --diff, --ignore,
                                            --delete --yes, --dry-run)
              trace-install                 Record an installer run into a reusable trace
                  --installer <path> [--name <name>] [--publisher <name>] [--timeout minutes] [--json]
              traces list|delete <id>       Manage local traces (--json, --yes)
              traces export <id> <file>     Export a portable trace bundle with a SHA-256 manifest
              traces import <file>          Import a portable trace bundle (--json)
              backups list                  List backups (--json)
              backups preview <id>          Show what a restore would change (--json, --overwrite)
              backups restore <id>          Restore a backup (--yes, --overwrite)
              backups prune                 Keep the newest N backups (--keep N --yes)
              autoruns list                 List autorun entries (--json)
              autoruns disable|enable <id>  Reversibly toggle an autorun entry (--yes)
              autoruns history              Show service/task/autorun modification history (--json)
              autoruns rollback <id|all>    Rollback disabled autoruns to original state (--yes)
              junk scan                     Report temp/cache sizes (--json)
              junk clean <categories...>    Clean allowed categories (--yes, --dry-run, --json)
              privacy scan                  Report Windows history and app scan state (--json)
              privacy clean <categories...> Remove fixed history categories (--yes, --dry-run, --json)
              privacy history               Show app scan history (--clear --yes to remove it)
              browser scan                  List browser profile history and cookie files (--json)
              browser clean                 Remove the listed files when browsers are closed
                  [--browser chrome|edge|brave|vivaldi|firefox] [--yes] [--dry-run] [--json]
              store|msix list               List MSIX/Microsoft Store packages (--json)
              store|msix uninstall <pkg>    Uninstall an MSIX package (--yes)
              store reset <package>         Reset package app data (--yes)
              store terminate <package>     Terminate running package processes
              shred <path...>               Overwrite and delete files/folders irreversibly
                  [--passes N] [--yes] [--dry-run] [--json]
              maintenance run               Run backup retention, log expiry and optional junk cleanup (--json)
              service status                Show the maintenance service (--json)
              service install|remove        Install or remove the maintenance service (--yes, elevated)
              service start|stop            Control the maintenance service (elevated)
              hunter                        Identify the installed app behind the window under the cursor
              windows-tools list|launch     Built-in and custom Windows maintenance tools
              wipe-free-space list|wipe     Evidence remover: securely wipe unallocated drive free space
                  [<drive>] [--chunk-mb 512] [--fill-byte 0] [--yes] [--dry-run] [--json]

            Exit codes:
              0 success · 1 error · 2 confirmation required · 3 ambiguous target
              4 not found · 5 elevation required · 6 cancelled

            Program deletions always run through a backup and are refused without --yes.
            Forensic shredding and privacy history cleanup are irreversible by design and need
            an explicit --yes (or a typed confirmation in the desktop app).
            """);
    }
}
