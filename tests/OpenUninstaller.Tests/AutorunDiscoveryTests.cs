using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class AutorunDiscoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Packaged_startup_tasks_use_the_declared_task_and_current_user_state(int state, bool enabled)
    {
        using var env = new TestEnvironment();
        RegisterPackage(env, "1.0.0.0", """
            <Extension Category="windows.startupTask" Executable="bin\widget.exe">
              <StartupTask TaskId="StartWidget" DisplayName="Widget" Enabled="true" />
            </Extension>
            """);
        var location = env.Paths.PackagedStartupStateRoot with
        { Path = env.Paths.PackagedStartupStateRoot.Path + @"\Contoso.Widget_publisher\StartWidget" };
        env.Registry.SetValue(location, "State", RegistryValueKind.DWord, state);
        // Unrelated state keys are not startup declarations and must not create phantom entries.
        env.Registry.SetValue(location with { Path = location.Path + "Old" }, "State", RegistryValueKind.DWord, 2);

        var service = Service(env);
        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Source == AutorunSourceKind.PackagedStartupTask));

        Assert.Equal("Widget", entry.Name);
        Assert.Equal("Contoso", entry.Publisher);
        Assert.Equal(enabled, entry.IsEnabled);
        Assert.EndsWith(@"bin\widget.exe", entry.ExecutablePath);
        Assert.False(entry.IsWindowsComponent);
        Assert.Equal("Manage", entry.ToggleLabel);
        Assert.False(enabled ? service.Disable(entry.Id, out _) : service.Enable(entry.Id, out _));
        Assert.Equal(state, env.Registry.GetValue(location, "State")?.AsInt32());
    }

    [Fact]
    public void Packaged_manifest_defaults_and_application_executable_are_used_without_a_state_override()
    {
        using var env = new TestEnvironment();
        RegisterPackage(env, "1.0.0.0", """
            <Extension Category="windows.startupTask"><StartupTask TaskId="StartWidget" Enabled="true" /></Extension>
            <Extension Category="windows.appExecutionAlias"><StartupTask TaskId="NotStartup" /></Extension>
            """);

        var entry = Assert.Single(Service(env).Scan().Entries.Where(e => e.Source == AutorunSourceKind.PackagedStartupTask));

        Assert.True(entry.IsEnabled);
        Assert.Equal("Widget App", entry.Name);
        Assert.EndsWith(@"widget.exe", entry.ExecutablePath);
    }

    [Fact]
    public void A_package_with_no_startup_extension_is_not_an_autorun()
    {
        using var env = new TestEnvironment();
        RegisterPackage(env, "1.0.0.0", "<Extension Category=\"windows.appExecutionAlias\" />");

        Assert.DoesNotContain(Service(env).Scan().Entries, e => e.Source == AutorunSourceKind.PackagedStartupTask);
    }

    [Fact]
    public void Package_versions_have_one_stable_task_identity_and_use_the_newest_version()
    {
        using var env = new TestEnvironment();
        RegisterPackage(env, "1.0.0.0", "<Extension Category=\"windows.startupTask\"><StartupTask TaskId=\"Start\" DisplayName=\"Old\" /></Extension>");
        RegisterPackage(env, "2.0.0.0", "<Extension Category=\"windows.startupTask\"><StartupTask TaskId=\"Start\" DisplayName=\"Current\" /></Extension>");

        var entry = Assert.Single(Service(env).Scan().Entries.Where(e => e.Source == AutorunSourceKind.PackagedStartupTask));

        Assert.Equal("Current", entry.Name);
        Assert.Contains("2.0.0.0", entry.ExecutablePath);
    }

    [Fact]
    public void Package_executable_cannot_escape_its_install_folder()
    {
        using var env = new TestEnvironment();
        RegisterPackage(env, "1.0.0.0", "<Extension Category=\"windows.startupTask\" Executable=\"..\\outside.exe\"><StartupTask TaskId=\"Start\" /></Extension>");

        var entry = Assert.Single(Service(env).Scan().Entries.Where(e => e.Source == AutorunSourceKind.PackagedStartupTask));

        Assert.Null(entry.ExecutablePath);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void Windows_startup_approval_is_respected_for_registry_entries(int state, bool enabled)
    {
        using var env = new TestEnvironment();
        var run = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, env.Paths.AutorunRegistryPath + @"\Run");
        var approval = run with { Path = env.Paths.StartupApprovedRegistryPath + @"\Run" };
        env.Registry.SetValue(run, "Widget", RegistryValueKind.String, @"C:\Applications\Widget\widget.exe");
        var bytes = new byte[12];
        bytes[0] = (byte)state;
        env.Registry.SetValue(approval, "Widget", RegistryValueKind.Binary, bytes);
        var service = Service(env);

        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Name == "Widget"));

        Assert.Equal(enabled, entry.IsEnabled);
        if (!enabled)
        {
            Assert.True(service.Enable(entry.Id, out var error), error);
            Assert.True(Assert.Single(service.Scan().Entries.Where(e => e.Id == entry.Id)).IsEnabled);
            Assert.Equal(@"C:\Applications\Widget\widget.exe", env.Registry.GetValue(run, "Widget")?.AsString());
        }
    }

    [Fact]
    public void Startup_folder_approval_and_shortcut_scope_are_preserved()
    {
        using var env = new TestEnvironment();
        var startup = Path.Combine(env.Paths.AppData, @"Microsoft\Windows\Start Menu\Programs\Startup");
        Directory.CreateDirectory(startup);
        File.WriteAllText(Path.Combine(startup, "widget.cmd"), "echo fixture");
        File.WriteAllText(Path.Combine(startup, "desktop.ini"), "fixture");
        var approval = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64,
            env.Paths.StartupApprovedRegistryPath + @"\StartupFolder");
        env.Registry.SetValue(approval, "widget.cmd", RegistryValueKind.Binary, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var service = Service(env);

        var entry = Assert.Single(service.Scan().Entries.Where(e => e.Source == AutorunSourceKind.UserStartupFolder));

        Assert.False(entry.IsEnabled);
        Assert.True(service.Enable(entry.Id, out var error), error);
        Assert.True(File.Exists(entry.FilePath));
        Assert.True(Assert.Single(service.Scan().Entries.Where(e => e.Id == entry.Id)).IsEnabled);
    }

    [Theory]
    [InlineData("System32/svchost.exe", "Microsoft Corporation", true)]
    [InlineData("System32/DriverStore/nvidia.exe", "NVIDIA Corporation", false)]
    [InlineData("System32/vmnat.exe", "VMware, Inc.", false)]
    [InlineData("System32/unknown.exe", null, false)]
    public void Basic_hides_only_identified_Windows_binaries_not_everything_in_the_Windows_folder(string relative, string? publisher, bool system)
    {
        using var env = new TestEnvironment();
        var entry = Entry(AutorunSourceKind.MachineService, Path.Combine(env.Paths.WindowsDirectory, relative), publisher);

        Assert.Equal(system, AutorunIdentity.IsWindowsComponent(entry, env.Paths));
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("wscript.exe")]
    [InlineData("cmd.exe")]
    public void User_scripts_are_not_hidden_by_the_Microsoft_host_publisher(string host)
    {
        using var env = new TestEnvironment();
        var entry = Entry(AutorunSourceKind.UserStartupFolder, Path.Combine(env.Paths.WindowsDirectory, "System32", host), "Microsoft Corporation");

        Assert.False(AutorunIdentity.IsWindowsComponent(entry, env.Paths));
    }

    [Theory]
    [InlineData(@"Microsoft\Windows\UpdateOrchestrator\Schedule", true)]
    [InlineData(@"Microsoft\Office\Update", false)]
    [InlineData(@"MicrosoftEdgeUpdateTaskMachineCore", false)]
    [InlineData(@"ControlCenter", false)]
    public void Basic_distinguishes_system_tasks_from_application_and_user_tasks(string task, bool system)
    {
        using var env = new TestEnvironment();
        var entry = Entry(AutorunSourceKind.ScheduledTask, @"C:\Windows\System32\powershell.exe", "Microsoft Corporation") with { TaskName = task };

        Assert.Equal(system, AutorunIdentity.IsWindowsComponent(entry, env.Paths));
    }

    [Fact]
    public void Microsoft_user_applications_stay_in_Basic()
    {
        using var env = new TestEnvironment();
        var powerToys = Entry(AutorunSourceKind.UserRun, Path.Combine(env.Paths.ProgramFiles, "PowerToys", "PowerToys.exe"), "Microsoft Corporation");
        var terminal = Entry(AutorunSourceKind.PackagedStartupTask, @"C:\Program Files\WindowsApps\Terminal.exe", "Microsoft Corporation");

        Assert.False(AutorunIdentity.IsWindowsComponent(powerToys, env.Paths));
        Assert.False(AutorunIdentity.IsWindowsComponent(terminal, env.Paths));
    }

    [Theory]
    [InlineData(2, 16, true)]
    [InlineData(4, 32, true)]
    [InlineData(3, 16, false)]
    [InlineData(3, 1, false)]
    [InlineData(2, 1, false)]
    public void Basic_service_candidates_are_automatic_or_disabled_user_mode_services(int start, int type, bool basicCandidate)
    {
        using var env = new TestEnvironment();
        var location = env.Paths.ServicesRoot with { Path = env.Paths.ServicesRoot.Path + @"\Widget" };
        env.Registry.SetValue(location, "ImagePath", RegistryValueKind.String, @"C:\Apps\Widget\widget.exe");
        env.Registry.SetValue(location, "DisplayName", RegistryValueKind.String, "Widget Service");
        env.Registry.SetValue(location, "Start", RegistryValueKind.DWord, start);
        env.Registry.SetValue(location, "Type", RegistryValueKind.DWord, type);

        var entry = Assert.Single(Service(env).Scan().Entries.Where(e => e.Name == "Widget Service"));

        Assert.Equal(basicCandidate, entry.IsApplicationStartup);
    }

    [Fact]
    public void Windows_Defender_platform_services_are_system_entries_without_hiding_Microsoft_user_apps()
    {
        using var env = new TestEnvironment();
        var entry = Entry(AutorunSourceKind.MachineService,
            Path.Combine(env.Paths.ProgramData, "Microsoft", "Windows Defender", "Platform", "MsMpEng.exe"), "Microsoft Corporation");

        Assert.True(AutorunIdentity.IsWindowsComponent(entry, env.Paths));
    }

    [Fact]
    public void Inaccessible_service_parameters_do_not_abort_the_inventory()
    {
        using var env = new TestEnvironment();
        var location = env.Paths.ServicesRoot with { Path = env.Paths.ServicesRoot.Path + @"\Widget" };
        env.Registry.SetValue(location, "ImagePath", RegistryValueKind.String, Path.Combine(env.Paths.WindowsDirectory, "System32", "svchost.exe"));
        env.Registry.SetValue(location, "Start", RegistryValueKind.DWord, 2);
        var service = new AutorunService(new ProtectedParameters(env.Registry), env.Paths, FileOperationLog.Null, new ProcessRunner());

        var result = service.Scan();

        Assert.Contains(result.Entries, e => e.Name == "Widget");
        Assert.Contains(result.Warnings, warning => warning.Contains("Service identity", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_task_triggers_do_not_change_the_tasks_enabled_setting()
    {
        using var env = new TestEnvironment();
        env.CreateFile("Tasks/Widget", """
            <Task><Triggers><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>
            <Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>C:\Apps\widget.exe</Command></Exec></Actions></Task>
            """);

        var entry = Assert.Single(Service(env).Scan().Entries.Where(e => e.Source == AutorunSourceKind.ScheduledTask));

        Assert.True(entry.IsEnabled);
    }

    private sealed class ProtectedParameters(IRegistryStore inner) : IRegistryStore
    {
        public bool KeyExists(RegistryLocation location) => inner.KeyExists(location);
        public RegistryValue? GetValue(RegistryLocation location, string name) => location.Path.EndsWith(@"\Parameters", StringComparison.Ordinal)
            ? throw new System.Security.SecurityException("Fixture access denied.") : inner.GetValue(location, name);
        public IReadOnlyList<string> GetValueNames(RegistryLocation location) => inner.GetValueNames(location);
        public IReadOnlyList<string> GetSubKeyNames(RegistryLocation location) => inner.GetSubKeyNames(location);
        public void CreateSubKey(RegistryLocation location) => inner.CreateSubKey(location);
        public void SetValue(RegistryLocation location, string name, RegistryValueKind kind, object data) => inner.SetValue(location, name, kind, data);
        public void DeleteValue(RegistryLocation location, string name) => inner.DeleteValue(location, name);
        public void DeleteKey(RegistryLocation location, bool recursive) => inner.DeleteKey(location, recursive);
    }

    private static AutorunService Service(TestEnvironment env) => new(env.Registry, env.Paths, FileOperationLog.Null, new ProcessRunner());

    private static AutorunEntry Entry(AutorunSourceKind source, string executable, string? publisher) => new()
    { Id = "fixture", Name = "Fixture", Command = executable, ExecutablePath = executable, Source = source, Publisher = publisher, LocationDisplay = "Fixture" };

    private static void RegisterPackage(TestEnvironment env, string version, string extensions)
    {
        var fullName = $"Contoso.Widget_{version}_x64__publisher";
        var folder = env.CreateDirectory("WindowsApps", fullName);
        File.WriteAllText(Path.Combine(folder, "AppxManifest.xml"), $"""
            <Package><Identity Name="Contoso.Widget" Version="{version}" ProcessorArchitecture="x64" Publisher="CN=Contoso" />
            <Properties><DisplayName>Widget App</DisplayName><PublisherDisplayName>Contoso</PublisherDisplayName></Properties>
            <Applications><Application Id="Widget" Executable="widget.exe"><Extensions>{extensions}</Extensions></Application></Applications></Package>
            """);
        var root = env.Paths.MsixPackagesRoot;
        env.Registry.SetValue(root with { Path = root.Path + "\\" + fullName }, "PackageRootFolder", RegistryValueKind.String, folder);
    }
}
