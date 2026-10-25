using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

public sealed class TraceInstaller
{
    private readonly RegistrySnapshotter _registrySnapshotter;
    private readonly FileSnapshotter _fileSnapshotter;
    private readonly TraceStore _store;
    private readonly IProcessRunner _runner;
    private readonly IClock _clock;
    private readonly AppPaths _paths;
    private readonly IRegistryStore _registry;

    public TraceInstaller(
        RegistrySnapshotter registrySnapshotter,
        FileSnapshotter fileSnapshotter,
        TraceStore store,
        IProcessRunner runner,
        IClock clock,
        AppPaths paths,
        IRegistryStore registry)
    {
        _registrySnapshotter = registrySnapshotter;
        _fileSnapshotter = fileSnapshotter;
        _store = store;
        _runner = runner;
        _clock = clock;
        _paths = paths;
        _registry = registry;
    }

    public IReadOnlyList<RegistryLocation> RegistryScopes { get; } =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"),
        new(RegistryHive.CurrentUser, RegistryView.Registry64, "SOFTWARE"),
        new(RegistryHive.CurrentUser, RegistryView.Registry32, "SOFTWARE")
    ];

    public IReadOnlyList<string> FileRoots =>
    [
        _paths.ProgramFiles,
        _paths.ProgramFilesX86,
        _paths.ProgramData,
        _paths.LocalAppData,
        _paths.AppData,
        _paths.UserDesktop,
        _paths.PublicDesktop,
        _paths.UserStartMenuPrograms,
        _paths.CommonStartMenuPrograms
    ];

    public async Task<TraceDocument> TraceAsync(
        string installerPath,
        string programName,
        string? publisher = null,
        TimeSpan? waitTimeout = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            throw new FileNotFoundException("The installer was not found.", installerPath);
        }

        var canonicalInstaller = Path.GetFullPath(installerPath);
        progress?.Report(new OperationProgress(OperationStages.Preparing, "Indexing registry before install"));
        var beforeRegistry = await Task.Run(
            () => _registrySnapshotter.Capture(RegistryScopes, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new OperationProgress(OperationStages.Preparing, "Indexing files before install"));
        var beforeFiles = await Task.Run(
            () => _fileSnapshotter.Capture(FileRoots, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new OperationProgress(OperationStages.RunningUninstaller, "Running the installer"));
        var run = await _runner.RunAsync(
            canonicalInstaller,
            string.Empty,
            shellExecute: true,
            waitTimeout ?? TimeSpan.FromMinutes(30),
            cancellationToken).ConfigureAwait(false);

        if (!run.Started)
        {
            throw new InvalidOperationException($"The installer did not start: {run.Error ?? "unknown error"}");
        }

        progress?.Report(new OperationProgress(OperationStages.Scanning, "Indexing registry after install"));
        var afterRegistry = await Task.Run(
            () => _registrySnapshotter.Capture(RegistryScopes, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new OperationProgress(OperationStages.Scanning, "Indexing files after install"));
        var afterFiles = await Task.Run(
            () => _fileSnapshotter.Capture(FileRoots, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var registryDiff = RegistrySnapshotter.Diff(beforeRegistry, afterRegistry);
        var fileDiff = FileSnapshotter.Diff(beforeFiles, afterFiles);
        var now = _clock.UtcNow;
        var id = $"{now:yyyyMMdd-HHmmss}-{Hashing.Slug(programName)}-{Hashing.StableId(programName, canonicalInstaller, now.ToString("O"))[..6]}";

        var trace = new TraceDocument
        {
            SchemaVersion = 2,
            Id = id,
            ProgramName = programName.Trim(),
            Publisher = publisher,
            CreatedAt = now,
            InstallerPath = canonicalInstaller,
            InstallerExitCode = run.ExitCode,
            RegistryTruncated = registryDiff.Truncated,
            FilesTruncated = fileDiff.Truncated,
            RegistryRoots = RegistryScopes.Select(s => s.DisplayPath).ToList(),
            FileRoots = FileRoots.ToList(),
            RegistryKeys = registryDiff.AddedKeys,
            RegistryValues = registryDiff.AddedValuesToExistingKeys,
            Files = fileDiff.AddedPaths,
            InstallLocation = DetectInstallLocation(programName)
        };

        _store.Save(trace);
        progress?.Report(new OperationProgress(OperationStages.Completed, $"Trace saved with {trace.KeyCount} keys and {trace.FileCount} files"));
        return trace;
    }

    private string? DetectInstallLocation(string programName)
    {
        var tokens = NameNormalizer.MatchTokens(programName, publisher: null);
        foreach (var source in ProgramScanner.Sources)
        {
            foreach (var subKey in _registry.GetSubKeyNames(source))
            {
                var location = new RegistryLocation(source.Hive, source.View, $"{source.Path}\\{subKey}");
                var displayName = _registry.GetValue(location, "DisplayName")?.AsString();
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var matches = NameNormalizer.Compact(displayName) == NameNormalizer.Compact(programName)
                              || NameNormalizer.CountTokenMatches(displayName, tokens) >= 2;
                if (!matches)
                {
                    continue;
                }

                var installLocation = _registry.GetValue(location, "InstallLocation")?.AsString();
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    return Environment.ExpandEnvironmentVariables(installLocation).TrimEnd('\\');
                }
            }
        }

        return null;
    }
}
