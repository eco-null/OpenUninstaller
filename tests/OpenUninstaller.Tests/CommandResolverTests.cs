using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class CommandResolverTests
{
    private const string ProductCode = "{90160000-008C-0000-1000-0000000FF1CE}";

    [Fact]
    public void Quoted_path_and_arguments_are_preserved()
    {
        var program = TestPrograms.Create(
            "Example",
            uninstallCommand: "\"C:\\Program Files\\Example\\uninst.exe\" /S /Remove");

        var command = UninstallCommandResolver.Resolve(program, preferSilent: false);

        Assert.Equal("C:\\Program Files\\Example\\uninst.exe", command.Executable);
        Assert.Equal("/S /Remove", command.Arguments);
        Assert.Contains("\"C:\\Program Files\\Example\\uninst.exe\" /S /Remove", command.DisplayCommand);
    }

    [Fact]
    public void Quiet_command_is_preferred_only_in_silent_mode()
    {
        var program = TestPrograms.Create(
            "Example",
            uninstallCommand: "\"C:\\App\\uninst.exe\"",
            location: new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, "SOFTWARE\\Vendor\\App"));
        program = program with
        {
            QuietUninstallCommand = "\"C:\\App\\uninst.exe\" /quiet"
        };

        var interactive = UninstallCommandResolver.Resolve(program, preferSilent: false);
        var silent = UninstallCommandResolver.Resolve(program, preferSilent: true);

        Assert.Equal(string.Empty, interactive.Arguments);
        Assert.Equal("/quiet", silent.Arguments);
    }

    [Theory]
    [InlineData(false, $"/x {ProductCode}")]
    [InlineData(true, $"/x {ProductCode} /qn /norestart")]
    public void Msi_install_command_is_rewritten_to_uninstall(bool silent, string expectedArguments)
    {
        var program = TestPrograms.Create(
            "MSI App",
            uninstallCommand: $"MsiExec.exe /I{ProductCode}");
        program = program with { IsWindowsInstaller = true };

        var command = UninstallCommandResolver.Resolve(program, silent);

        Assert.True(command.IsUsable);
        Assert.Equal("msiexec.exe", command.Executable);
        Assert.Equal(expectedArguments, command.Arguments);
        Assert.Equal(ProductCode, command.ProductCode);
    }

    [Fact]
    public void Msi_entry_without_command_uses_key_guid()
    {
        var location = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{ProductCode}");
        var program = TestPrograms.Create("MSI App", location: location) with { IsWindowsInstaller = true };

        var command = UninstallCommandResolver.Resolve(program, preferSilent: true);

        Assert.True(command.IsUsable);
        Assert.Contains($"/x {ProductCode} /qn", command.Arguments);
    }

    [Fact]
    public void Missing_executable_is_reported_and_not_usable()
    {
        var program = TestPrograms.Create(
            "Broken",
            uninstallCommand: "\"C:\\Does\\Not\\Exist\\uninst.exe\" /S");

        var command = UninstallCommandResolver.Resolve(program, preferSilent: false);

        Assert.False(command.IsUsable);
        Assert.Contains("not found", command.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_command_is_reported()
    {
        var program = TestPrograms.Create("No uninstaller");

        var command = UninstallCommandResolver.Resolve(program, preferSilent: false);

        Assert.False(command.IsUsable);
        Assert.Contains("no registered uninstall", command.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProductCode, true)]
    [InlineData("{not-a-guid}", false)]
    [InlineData("", false)]
    public void Product_code_detection_is_strict(string candidate, bool expected)
        => Assert.Equal(expected, UninstallCommandResolver.IsProductCode(candidate));
}
