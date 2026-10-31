using Microsoft.Win32;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Support;

/// <summary>
/// Resolves every location OpenUninstaller reads or writes. Properties are init-settable so tests
/// can point the engine at fixture directories instead of touching the machine.
/// </summary>
public sealed class AppPaths
{
    public string ProgramData { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string UserProfile { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string AppData { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalAppData { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string AppDataLocalLow { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    public string WindowsDirectory { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string SystemDrive { get; init; } =
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

    public string ProgramFiles { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string ProgramFilesX86 { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public string Public { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) is var commonDesktop
            ? Path.GetDirectoryName(commonDesktop) ?? @"C:\Users\Public"
            : @"C:\Users\Public";

    public string UserDesktop { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string PublicDesktop { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public string UserStartMenuPrograms { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");

    public string CommonStartMenuPrograms { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");

    public string Temp { get; init; } = Path.GetTempPath();

    public string AppBaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Root for persistent data (traces, logs, backups).</summary>
    public string DataDirectory => Path.Combine(ProgramData, "OpenUninstaller");

    public string BackupsDirectory => Path.Combine(DataDirectory, "Backups");

    public string TracesDirectory => Path.Combine(DataDirectory, "Traces");

    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    public string LocalDataDirectory => Path.Combine(LocalAppData, "OpenUninstaller");

    public string DisabledStartupDirectory => Path.Combine(LocalDataDirectory, "DisabledStartup");

    public string AutorunsHistoryPath => Path.Combine(LocalDataDirectory, "autoruns-history.json");

    public string DisabledAutorunsRegistryPath { get; init; } = @"Software\OpenUninstaller\DisabledAutoruns";

    public string AutorunRegistryPath { get; init; } = @"Software\Microsoft\Windows\CurrentVersion";

    public string StartupApprovedRegistryPath { get; init; } =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public RegistryLocation PackagedStartupStateRoot { get; init; } = new(
        RegistryHive.CurrentUser, RegistryView.Registry64,
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData");

    /// <summary>Container for per-executable App Paths registrations (machine or user hive).</summary>
    public string AppPathsRegistryPath { get; init; } = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    /// <summary>Container for Classes\Applications registrations.</summary>
    public string ClassesApplicationsRegistryPath { get; init; } = @"SOFTWARE\Classes\Applications";

    /// <summary>Root of the merged COM and file-association registration store.</summary>
    public string ClassesRegistryPath { get; init; } = @"SOFTWARE\Classes";

    /// <summary>Windows Defender Firewall rule values; each value is one rule.</summary>
    public RegistryLocation FirewallRulesRoot { get; init; } = new(
        RegistryHive.LocalMachine,
        RegistryView.Registry64,
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");

    /// <summary>Machine-wide environment block, including the machine PATH value.</summary>
    public RegistryLocation MachineEnvironmentRoot { get; init; } = new(
        RegistryHive.LocalMachine,
        RegistryView.Registry64,
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");

    /// <summary>Per-user environment block, including the user PATH value.</summary>
    public RegistryLocation UserEnvironmentRoot { get; init; } = new(
        RegistryHive.CurrentUser,
        RegistryView.Registry64,
        @"Environment");

    /// <summary>Installed MSIX/Store package registrations. Read-only inventory source.</summary>
    public RegistryLocation MsixPackagesRoot { get; init; } = new(
        RegistryHive.CurrentUser,
        RegistryView.Registry64,
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");

    /// <summary>Set false in tests so environment refreshes are not broadcast to the desktop.</summary>
    public bool BroadcastEnvironmentChanges { get; init; } = true;

    /// <summary>Service definitions; overridable so tests never read the real service database.</summary>
    public RegistryLocation ServicesRoot { get; init; } =
        new(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services");

    /// <summary>Task Scheduler's XML task store; overridable for tests.</summary>
    public string ScheduledTasksDirectory { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "Tasks");

    public string AppVersion { get; init; } = typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static AppPaths CreateDefault() => new();

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(TracesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(DisabledStartupDirectory);
    }

    public string CreateLog(string operationName)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            return Path.Combine(
                LogsDirectory,
                $"op-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Support.Hashing.Slug(operationName)}.jsonl");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Path.GetTempPath(), $"openuninstaller-{Guid.NewGuid():N}.jsonl");
        }
    }
}
