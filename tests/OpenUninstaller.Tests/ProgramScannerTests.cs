using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class ProgramScannerTests
{
    [Fact]
    public void Scan_reads_values_and_parses_metadata()
    {
        using var env = new TestEnvironment();
        env.AddProgramEntry(
            "Contoso Widget",
            publisher: "Contoso",
            version: "3.2.1",
            installLocation: env.CreateDirectory("Program Files", "Contoso", "Widget"),
            uninstallCommand: "\"C:\\Program Files\\Contoso\\Widget\\uninst.exe\" /S",
            quietUninstallCommand: "\"C:\\Program Files\\Contoso\\Widget\\uninst.exe\" /S /quiet",
            estimatedKb: 2048,
            installDate: "20250131");
        var scanner = new ProgramScanner(env.Registry, [env.UninstallRoot]);

        var programs = scanner.Scan();

        var program = Assert.Single(programs);
        Assert.Equal("Contoso Widget", program.Name);
        Assert.Equal("Contoso", program.Publisher);
        Assert.Equal("3.2.1", program.Version);
        Assert.Equal(2048L * 1024, program.EstimatedSizeBytes);
        Assert.Equal(new DateOnly(2025, 1, 31), program.InstallDate);
        Assert.Equal(ProgramArchitecture.X64, program.Architecture);
        Assert.Contains("uninst.exe", program.UninstallCommand);
        Assert.Equal(16, program.Id.Length);
    }

    [Theory]
    [InlineData("System component", true, null, null)]
    [InlineData("Patch of another product", false, "Parent", null)]
    [InlineData("Security update", false, null, "Security Update")]
    [InlineData("Update rollup", false, null, "Update Rollup")]
    public void Scan_filters_updates_and_components(string label, bool systemComponent, string? parent, string? releaseType)
    {
        using var env = new TestEnvironment();
        env.AddProgramEntry(
            label,
            subKey: "Filtered",
            systemComponent: systemComponent,
            parentKeyName: parent,
            releaseType: releaseType);
        var scanner = new ProgramScanner(env.Registry, [env.UninstallRoot]);

        Assert.Empty(scanner.Scan());
        Assert.Single(scanner.Scan(includeSystemComponents: true));
    }

    [Fact]
    public void Scan_skips_entries_without_display_name()
    {
        using var env = new TestEnvironment();
        env.AddRegistryKey("Uninstall\\NoName", ("DisplayVersion", "1.0", Microsoft.Win32.RegistryValueKind.String));
        var scanner = new ProgramScanner(env.Registry, [env.UninstallRoot]);

        Assert.Empty(scanner.Scan(includeSystemComponents: true));
    }

    [Fact]
    public void FindAllByTarget_prefers_exact_name_then_substring()
    {
        using var env = new TestEnvironment();
        env.AddProgramEntry("Contoso Widget", "Widget");
        env.AddProgramEntry("Contoso Widget Helper", "Helper");
        env.AddProgramEntry("Fabrikam", "Fab");
        var scanner = new ProgramScanner(env.Registry, [env.UninstallRoot]);

        var exact = scanner.FindAllByTarget("Contoso Widget");
        Assert.Single(exact);
        Assert.Equal("Contoso Widget", exact[0].Name);

        var partial = scanner.FindAllByTarget("Contoso");
        Assert.Equal(2, partial.Count);
    }
}
