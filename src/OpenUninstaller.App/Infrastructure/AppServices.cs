using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.App.Infrastructure;

/// <summary>Composition root for the desktop app. One instance per process.</summary>
public sealed class AppServices
{
    private AppServices()
    {
        Paths = AppPaths.CreateDefault();
        Registry = new WindowsRegistryStore();
        Files = new PhysicalFileSystem();
        Runner = new ProcessRunner();
        Clock = new SystemClock();
        Elevation = new WindowsElevationService();
        Log = new FileOperationLog(Paths.LogsDirectory, "desktop");
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
        RegistrySnapshots = new RegistrySnapshotter(Registry);
        FileSnapshots = new FileSnapshotter(Paths);
        TraceInstaller = new TraceInstaller(RegistrySnapshots, FileSnapshots, Traces, Runner, Clock, Paths, Registry);
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
        Settings = UiSettings.Load(Paths);
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
    public RegistrySnapshotter RegistrySnapshots { get; }
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
    public UiSettings Settings { get; }

    public string Version => Paths.AppVersion;
    public bool IsElevated => Elevation.IsElevated;

    /// <summary>Raised after a page changes settings that other pages display.</summary>
    public event EventHandler? SettingsChanged;

    public void NotifySettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public static AppServices Create() => new();

    public IReadOnlyList<InstalledProgram> ScanPrograms() => Scanner.Scan();

    public string FormatAdminState => IsElevated ? "Administrator" : "Standard user";
}
