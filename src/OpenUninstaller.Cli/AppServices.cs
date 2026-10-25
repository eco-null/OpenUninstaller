using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Cli;

internal sealed class AppServices
{
    private AppServices(string operation)
    {
        Paths = AppPaths.CreateDefault();
        Registry = new WindowsRegistryStore();
        Files = new PhysicalFileSystem();
        Runner = new ProcessRunner();
        Clock = new SystemClock();
        Elevation = new WindowsElevationService();
        Log = new FileOperationLog(Paths.LogsDirectory, operation);
        Guard = new PathGuard(Paths);
        Scanner = new ProgramScanner(Registry);
        Traces = new TraceStore(Paths);
        TraceBundles = new TraceBundleService(Paths, Traces, Clock);
        Ignored = new IgnoredLeftoverStore(Paths);
        History = new LeftoverHistoryStore(Paths);
        Backups = new BackupService(Paths, Registry, Runner, Clock, Log, Guard);
        Deletion = new LeftoverDeletionService(Registry, Guard, Backups, Log, Runner, Paths);
        Uninstall = new UninstallService(Registry, Runner, Log);
        Leftovers = new LeftoverScanner(Registry, Traces, Paths, Guard, Ignored);
        RegisterySnapshots = new RegistrySnapshotter(Registry);
        FileSnapshots = new FileSnapshotter(Paths);
        TraceInstaller = new TraceInstaller(RegisterySnapshots, FileSnapshots, Traces, Runner, Clock, Paths, Registry);
        Autoruns = new AutorunService(Registry, Paths, Log, Runner);
        Junk = new JunkService(Paths, Guard, Log);
        Privacy = new PrivacyService(Paths, Registry, Log);
        Browser = new BrowserDataService(Paths, Guard);
        Msix = new MsixInventoryService(Registry, Paths, Runner);
        Shredder = new Shredder(Guard, Log);
        EvidenceRemover = new EvidenceRemoverService(Log);
        WindowsTools = new WindowsToolsService(Paths);
        Maintenance = new MaintenanceService(Paths, Backups, Junk, Clock, Log);
        Background = new BackgroundServiceManager(Paths);
        Doctor = new DoctorService(Paths, Scanner, Traces, Backups, Elevation);
    }

    public AppPaths Paths { get; }
    public IRegistryStore Registry { get; }
    public IFileSystem Files { get; }
    public IProcessRunner Runner { get; }
    public IClock Clock { get; }
    public IElevationService Elevation { get; }
    public IOperationLog Log { get; }
    public PathGuard Guard { get; }
    public ProgramScanner Scanner { get; }
    public TraceStore Traces { get; }
    public TraceBundleService TraceBundles { get; }
    public IgnoredLeftoverStore Ignored { get; }
    public LeftoverHistoryStore History { get; }
    public BackupService Backups { get; }
    public LeftoverDeletionService Deletion { get; }
    public UninstallService Uninstall { get; }
    public LeftoverScanner Leftovers { get; }
    public RegistrySnapshotter RegisterySnapshots { get; }
    public FileSnapshotter FileSnapshots { get; }
    public TraceInstaller TraceInstaller { get; }
    public AutorunService Autoruns { get; }
    public JunkService Junk { get; }
    public PrivacyService Privacy { get; }
    public BrowserDataService Browser { get; }
    public MsixInventoryService Msix { get; }
    public Shredder Shredder { get; }
    public EvidenceRemoverService EvidenceRemover { get; }
    public WindowsToolsService WindowsTools { get; }
    public MaintenanceService Maintenance { get; }
    public BackgroundServiceManager Background { get; }
    public DoctorService Doctor { get; }

    public string? LogPath => Log.FilePath;

    public static AppServices Create(string operation) => new(operation);

    public static string RequireTarget(ArgumentSet args)
    {
        var target = args.FirstPositional;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new UsageException("A program name or id is required.");
        }

        return target;
    }

    public (InstalledProgram? Program, int ExitCode) ResolveProgram(string target)
    {
        var matches = Scanner.FindAllByTarget(target);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"No installed application matched '{target}'.");
            return (null, ExitCodes.NotFound);
        }

        if (matches.Count == 1)
        {
            return (matches[0], ExitCodes.Success);
        }

        var exact = matches
            .Where(p => string.Equals(p.Id, target, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1)
        {
            return (exact[0], ExitCodes.Success);
        }

        Console.Error.WriteLine($"'{target}' is ambiguous. Matches:");
        foreach (var match in matches.Take(25))
        {
            Console.Error.WriteLine($"  {match.Name}  [{match.Id}]  {match.RegistryPath}");
        }

        Console.Error.WriteLine("Re-run with the exact name or id.");
        return (null, ExitCodes.Ambiguous);
    }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int ConfirmationRequired = 2;
    public const int Ambiguous = 3;
    public const int NotFound = 4;
    public const int ElevationRequired = 5;
    public const int Cancelled = 6;
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class ConsoleProgress : IProgress<OperationProgress>
{
    public void Report(OperationProgress value)
        => Console.Error.WriteLine($"[{value.Stage}] {value.Message}");
}
