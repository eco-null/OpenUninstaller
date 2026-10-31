using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

/// <summary>
/// Provides an isolated machine model: a private temp tree and a private HKCU subtree. No test ever
/// writes to a real uninstall key, a real Startup folder or a user data directory.
/// </summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "ou-tests", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        RegistryBase = $"Software\\OUTests\\{id}";

        Paths = new AppPaths
        {
            ProgramData = Path.Combine(Root, "ProgramData"),
            LocalAppData = Path.Combine(Root, "LocalAppData"),
            AppDataLocalLow = Path.Combine(Root, "LocalLow"),
            AppData = Path.Combine(Root, "Roaming"),
            UserProfile = Path.Combine(Root, "User"),
            WindowsDirectory = Path.Combine(Root, "Windows"),
            SystemDrive = Root,
            ProgramFiles = Path.Combine(Root, "Program Files"),
            ProgramFilesX86 = Path.Combine(Root, "Program Files (x86)"),
            Public = Path.Combine(Root, "Public"),
            UserDesktop = Path.Combine(Root, "User", "Desktop"),
            PublicDesktop = Path.Combine(Root, "Public", "Desktop"),
            UserStartMenuPrograms = Path.Combine(Root, "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
            CommonStartMenuPrograms = Path.Combine(Root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs"),
            Temp = Path.Combine(Root, "Temp"),
            AppBaseDirectory = Path.Combine(Root, "App"),
            DisabledAutorunsRegistryPath = $"Software\\OpenUninstaller\\Tests\\{id}\\DisabledAutoruns",
            AutorunRegistryPath = $"{RegistryBase}\\Autorun",
            StartupApprovedRegistryPath = $"{RegistryBase}\\StartupApproved",
            PackagedStartupStateRoot = new RegistryLocation(RegistryHive.CurrentUser,
                RegistryView.Registry64, $"{RegistryBase}\\PackagedStartup"),
            AppPathsRegistryPath = $"{RegistryBase}\\App Paths",
            ClassesApplicationsRegistryPath = $"{RegistryBase}\\Classes\\Applications",
            ClassesRegistryPath = $"{RegistryBase}\\Classes",
            FirewallRulesRoot = new RegistryLocation(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                $"{RegistryBase}\\FirewallRules"),
            MachineEnvironmentRoot = new RegistryLocation(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                $"{RegistryBase}\\MachineEnvironment"),
            UserEnvironmentRoot = new RegistryLocation(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                $"{RegistryBase}\\UserEnvironment"),
            MsixPackagesRoot = new RegistryLocation(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                $"{RegistryBase}\\AppxPackages"),
            BroadcastEnvironmentChanges = false,
            ServicesRoot = new RegistryLocation(
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                $"{RegistryBase}\\Services"),
            ScheduledTasksDirectory = Path.Combine(Root, "Tasks"),
            AppVersion = "test"
        };

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Paths.Temp);
        Directory.CreateDirectory(Paths.ProgramData);
        Directory.CreateDirectory(Paths.LocalAppData);
        Directory.CreateDirectory(Paths.AppData);
        Directory.CreateDirectory(Paths.WindowsDirectory);
        Directory.CreateDirectory(Paths.ProgramFiles);
        Directory.CreateDirectory(Paths.ProgramFilesX86);
        Directory.CreateDirectory(Paths.Public);
        Directory.CreateDirectory(Paths.UserDesktop);
        Directory.CreateDirectory(Paths.PublicDesktop);
        Directory.CreateDirectory(Paths.UserStartMenuPrograms);
        Directory.CreateDirectory(Paths.CommonStartMenuPrograms);
        Directory.CreateDirectory(Paths.AppBaseDirectory);
        Directory.CreateDirectory(Paths.ScheduledTasksDirectory);

        UninstallRoot = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{RegistryBase}\\Uninstall");
        Registry.CreateSubKey(UninstallRoot);
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public IRegistryStore Registry { get; } = new WindowsRegistryStore();
    public string RegistryBase { get; }
    public RegistryLocation UninstallRoot { get; }

    public RegistryLocation AddRegistryKey(string relativePath, params (string Name, object Value, RegistryValueKind Kind)[] values)
    {
        var location = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{RegistryBase}\\{relativePath}");
        Registry.CreateSubKey(location);
        foreach (var (name, value, kind) in values)
        {
            Registry.SetValue(location, name, kind, value);
        }

        return location;
    }

    public void AddProgramEntry(
        string name,
        string subKey = "TestApp",
        string? publisher = null,
        string? version = "1.0.0",
        string? installLocation = null,
        string? uninstallCommand = null,
        string? quietUninstallCommand = null,
        int? estimatedKb = null,
        string? installDate = null,
        bool systemComponent = false,
        string? parentKeyName = null,
        string? releaseType = null,
        bool windowsInstaller = false)
    {
        var location = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"{UninstallRoot.Path}\\{subKey}");
        Registry.CreateSubKey(location);
        Registry.SetValue(location, "DisplayName", RegistryValueKind.String, name);
        if (version is not null)
        {
            Registry.SetValue(location, "DisplayVersion", RegistryValueKind.String, version);
        }

        if (publisher is not null)
        {
            Registry.SetValue(location, "Publisher", RegistryValueKind.String, publisher);
        }

        if (installLocation is not null)
        {
            Registry.SetValue(location, "InstallLocation", RegistryValueKind.String, installLocation);
        }

        if (uninstallCommand is not null)
        {
            Registry.SetValue(location, "UninstallString", RegistryValueKind.String, uninstallCommand);
        }

        if (quietUninstallCommand is not null)
        {
            Registry.SetValue(location, "QuietUninstallString", RegistryValueKind.String, quietUninstallCommand);
        }

        if (estimatedKb is not null)
        {
            Registry.SetValue(location, "EstimatedSize", RegistryValueKind.DWord, estimatedKb.Value);
        }

        if (installDate is not null)
        {
            Registry.SetValue(location, "InstallDate", RegistryValueKind.String, installDate);
        }

        if (systemComponent)
        {
            Registry.SetValue(location, "SystemComponent", RegistryValueKind.DWord, 1);
        }

        if (parentKeyName is not null)
        {
            Registry.SetValue(location, "ParentKeyName", RegistryValueKind.String, parentKeyName);
        }

        if (releaseType is not null)
        {
            Registry.SetValue(location, "ReleaseType", RegistryValueKind.String, releaseType);
        }

        if (windowsInstaller)
        {
            Registry.SetValue(location, "WindowsInstaller", RegistryValueKind.DWord, 1);
        }
    }

    public string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string content = "fixture", DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (lastWriteUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Registry.DeleteKey(
                new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, RegistryBase),
                recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            Registry.DeleteKey(
                new RegistryLocation(
                    RegistryHive.CurrentUser,
                    RegistryView.Registry64,
                    Paths.DisabledAutorunsRegistryPath),
                recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}

internal static class TestPrograms
{
    public static InstalledProgram Create(
        string name,
        string? publisher = null,
        string? installLocation = null,
        string? uninstallCommand = null,
        RegistryLocation? location = null,
        string? displayIcon = null) => new()
    {
        Id = "test-" + Math.Abs(name.GetHashCode()),
        Name = name,
        Publisher = publisher,
        InstallLocation = installLocation,
        UninstallCommand = uninstallCommand,
        DisplayIcon = displayIcon,
        Location = location ?? new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            "SOFTWARE\\OUTests\\Synthetic\\Uninstall\\App"),
        Architecture = ProgramArchitecture.X64
    };
}
