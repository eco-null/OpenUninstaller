using Microsoft.Win32;
using OpenUninstaller.Cli;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class UninstallAndCliTests
{
    [Fact]
    public async Task Uninstall_runs_the_registered_command_and_verifies_removal()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Uninstall\\Removable", ("DisplayName", "Removable", RegistryValueKind.String));
        var regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var program = TestPrograms.Create(
            "Removable",
            location: location,
            uninstallCommand: $"\"{regExe}\" delete \"HKCU\\{location.Path}\" /f");
        var service = new UninstallService(env.Registry, new ProcessRunner(), FileOperationLog.Null);

        var result = await service.RunAsync(
            program,
            new UninstallOptions { WaitTimeout = TimeSpan.FromSeconds(30) });

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Messages));
        Assert.False(result.StillRegistered);
        Assert.False(env.Registry.KeyExists(location));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Self_relaunching_uninstaller_is_awaited_until_its_child_removes_the_registration()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Uninstall\\Relaunching", ("DisplayName", "Relaunching", RegistryValueKind.String));
        var regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        // NSIS-style: the registered uninstaller starts a copy of itself and exits immediately.
        var secondPhase = env.CreateFile(
            "Program Files/Relaunching/second.cmd",
            $"@echo off\r\nping -n 3 127.0.0.1 >nul\r\n\"{regExe}\" delete \"HKCU\\{location.Path}\" /f\r\n");
        var launcher = env.CreateFile(
            "Program Files/Relaunching/uninst.cmd",
            $"@echo off\r\nstart \"\" /b cmd /c \"{secondPhase}\"\r\n");
        var program = TestPrograms.Create("Relaunching", location: location, uninstallCommand: $"\"{launcher}\"");
        var service = new UninstallService(env.Registry, new ProcessRunner(), FileOperationLog.Null);

        var result = await service.RunAsync(program, new UninstallOptions { WaitTimeout = TimeSpan.FromSeconds(30) });

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Messages));
        Assert.False(result.StillRegistered);
    }

    [Fact]
    public async Task Lingering_child_does_not_block_a_completed_uninstall()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Uninstall\\Survey", ("DisplayName", "Survey", RegistryValueKind.String));
        var regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        // The uninstaller finishes, then leaves a long-lived child such as a feedback page.
        var launcher = env.CreateFile(
            "Program Files/Survey/uninst.cmd",
            $"@echo off\r\n\"{regExe}\" delete \"HKCU\\{location.Path}\" /f\r\nstart \"\" /b ping -n 15 127.0.0.1\r\n");
        var program = TestPrograms.Create("Survey", location: location, uninstallCommand: $"\"{launcher}\"");
        var service = new UninstallService(env.Registry, new ProcessRunner(), FileOperationLog.Null);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.RunAsync(program, new UninstallOptions { WaitTimeout = TimeSpan.FromSeconds(60) });

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Messages));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Waited {stopwatch.Elapsed} for a finished uninstall.");
    }

    [Fact]
    public async Task Forced_uninstall_removes_the_registration_entry()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Uninstall\\Broken", ("DisplayName", "Broken", RegistryValueKind.String));
        var program = TestPrograms.Create("Broken", location: location);
        var service = new UninstallService(env.Registry, new ProcessRunner(), FileOperationLog.Null);

        var result = await service.RunAsync(program, new UninstallOptions { Force = true });

        Assert.True(result.Succeeded);
        Assert.True(result.Forced);
        Assert.False(env.Registry.KeyExists(location));
    }

    [Fact]
    public async Task Uninstall_without_a_command_fails_and_keeps_the_entry()
    {
        using var env = new TestEnvironment();
        var location = env.AddRegistryKey("Uninstall\\NoCommand", ("DisplayName", "NoCommand", RegistryValueKind.String));
        var program = TestPrograms.Create("NoCommand", location: location);
        var service = new UninstallService(env.Registry, new ProcessRunner(), FileOperationLog.Null);

        var result = await service.RunAsync(program, new UninstallOptions());

        Assert.False(result.Succeeded);
        Assert.True(result.StillRegistered);
        Assert.True(env.Registry.KeyExists(location));
    }

    [Fact]
    public void Argument_parser_handles_flags_and_values()
    {
        var args = ArgumentSet.Parse(["--json", "--timeout=30", "--name", "Contoso Widget", "scan-target"]);

        Assert.True(args.Has("json"));
        Assert.Equal("30", args.Value("timeout"));
        Assert.Equal("Contoso Widget", args.Value("name"));
        Assert.Equal("scan-target", args.FirstPositional);
        Assert.Throws<ArgumentException>(() => ArgumentSet.Parse(["--json", "--json"]));
    }

    [Fact]
    public void Argument_parser_does_not_consume_positionals_for_flags()
    {
        var args = ArgumentSet.Parse(["--yes", "uninstall", "My App"]);

        Assert.True(args.Has("yes"));
        Assert.Equal(["uninstall", "My App"], args.Positionals);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("with\"quote", "\"with\"\"quote\"")]
    public void Csv_escaping_is_correct(string value, string expected)
    {
        Assert.Equal(expected, Output.Csv(value));
    }

    [Fact]
    public async Task Cli_msix_alias_executes_successfully()
    {
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            var code = await Program.Main(["msix", "list", "--json"]);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
