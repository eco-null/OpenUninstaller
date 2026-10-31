using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.Tests;

public sealed class HunterMatchingTests
{
    [Theory]
    [InlineData("OpenUninstaller", "OpenCode 1.18.32", "OpenCode")]
    [InlineData("MicrosoftNotes", "Microsoft Paint", "Microsoft")]
    [InlineData("AcmeHelper", "Unrelated Product", "Acme")]
    [InlineData("ContosoWidget", "Contoso Widget Pro", "Other Vendor")]
    public void Name_or_publisher_similarity_does_not_establish_ownership(
        string processName, string productName, string publisher)
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile($"Workspace/{processName}.exe", "stub");
        var program = TestPrograms.Create(productName, publisher);

        var match = HunterService.MatchExecutable(exe, processName, 123, [program]);

        Assert.False(match.Matched);
        Assert.Equal(0, match.Score);
        Assert.Equal(processName, match.ProcessName);
        Assert.Equal(exe, match.ExePath);
    }

    [Fact]
    public void Development_copy_does_not_resolve_to_an_unrelated_or_installed_copy()
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Workspace/OpenUninstaller/bin/OpenUninstaller.exe", "stub");
        var openCode = TestPrograms.Create("OpenCode 1.18.32", "OpenCode",
            displayIcon: env.CreateFile("Programs/OpenCode/OpenCode.exe", "stub"));
        var installedCopy = TestPrograms.Create("OpenUninstaller 1.0.3", "OpenUninstaller contributors",
            installLocation: env.CreateDirectory("Programs", "OpenUninstaller"));

        var match = HunterService.MatchExecutable(exe, "OpenUninstaller", 123, [openCode, installedCopy]);

        Assert.False(match.Matched);
        Assert.Equal("OpenUninstaller", match.ProcessName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Most_specific_install_location_wins_independently_of_inventory_order(bool reverse)
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Vendor/Widget/bin/helper.exe", "stub");
        var broad = TestPrograms.Create("Vendor Suite", installLocation: env.CreateDirectory("Programs", "Vendor"));
        var specific = TestPrograms.Create("Widget", installLocation: env.CreateDirectory("Programs", "Vendor", "Widget"));

        var match = HunterService.MatchExecutable(exe, "Widget", 123,
            reverse ? [specific, broad] : [broad, specific]);

        Assert.Equal(specific.Id, match.Program?.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_icon_path_beats_install_directory_and_uninstaller(bool reverse)
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Shared/widget.exe", "stub");
        var directory = TestPrograms.Create("Suite", installLocation: Path.GetDirectoryName(exe));
        var uninstaller = TestPrograms.Create("Uninstaller", uninstallCommand: $"\"{exe}\" /remove");
        var exact = TestPrograms.Create("Widget", displayIcon: $"\"{exe}\",-2");

        var match = HunterService.MatchExecutable(exe, "Widget", 123,
            reverse ? [exact, uninstaller, directory] : [directory, uninstaller, exact]);

        Assert.Equal(exact.Id, match.Program?.Id);
    }

    [Fact]
    public void Exact_uninstaller_path_beats_install_directory()
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Shared/remove.exe", "stub");
        var directory = TestPrograms.Create("Suite", installLocation: Path.GetDirectoryName(exe));
        var exact = TestPrograms.Create("Widget", uninstallCommand: $"\"{exe}\" /remove");

        var match = HunterService.MatchExecutable(exe, null, 123, [directory, exact]);

        Assert.Equal(exact.Id, match.Program?.Id);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("icon")]
    [InlineData("uninstaller")]
    public void Equally_strong_owners_remain_unmatched_in_both_orders(string evidence)
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Shared/host.exe", "stub");
        var first = TestPrograms.Create("First Product",
            installLocation: evidence == "directory" ? Path.GetDirectoryName(exe) : null,
            displayIcon: evidence == "icon" ? exe : null,
            uninstallCommand: evidence == "uninstaller" ? $"\"{exe}\" /first" : null);
        var second = first with { Id = "second", Name = "Second Product" };

        foreach (var programs in new[] { new[] { first, second }, new[] { second, first } })
        {
            var match = HunterService.MatchExecutable(exe, "First Product", 123, programs);

            Assert.False(match.Matched);
            Assert.Equal(0, match.Score);
            Assert.Contains("multiple", match.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Duplicate_inventory_entries_for_the_same_identity_are_not_ambiguous()
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Widget/widget.exe", "stub");
        var program = TestPrograms.Create("Widget", installLocation: Path.GetDirectoryName(exe));

        var match = HunterService.MatchExecutable(exe, null, 123, [program, program]);

        Assert.Equal(program.Id, match.Program?.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identical_user_registration_in_both_registry_views_has_one_stable_owner(bool reverse)
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Widget/widget.exe", "stub");
        var native = TestPrograms.Create("Widget", displayIcon: exe);
        var mirrored = native with
        {
            Id = "mirrored",
            Location = native.Location with { View = RegistryView.Registry32 },
            Architecture = ProgramArchitecture.X86
        };

        var match = HunterService.MatchExecutable(exe, null, 123,
            reverse ? [mirrored, native] : [native, mirrored]);

        Assert.Equal(native.Id, match.Program?.Id);
    }

    [Theory]
    [InlineData("hive")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("command")]
    public void Distinct_registrations_are_not_collapsed_as_mirrored_user_keys(string difference)
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/Widget/widget.exe", "stub");
        var first = TestPrograms.Create("Widget", displayIcon: exe);
        var second = first with
        {
            Id = "second",
            Location = first.Location with { View = RegistryView.Registry32 }
        };
        second = difference switch
        {
            "hive" => second with { Location = second.Location with { Hive = RegistryHive.LocalMachine } },
            "key" => second with { Location = second.Location with { Path = first.Location.Path + "Other" } },
            "version" => second with { Version = "2.0" },
            _ => second with { UninstallCommand = "different.exe /remove" }
        };

        foreach (var programs in new[] { new[] { first, second }, new[] { second, first } })
            Assert.False(HunterService.MatchExecutable(exe, null, 123, programs).Matched);
    }

    [Fact]
    public void Directory_prefix_must_end_at_a_path_separator()
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Programs/WidgetOther/helper.exe", "stub");
        var program = TestPrograms.Create("Widget", installLocation: env.CreateDirectory("Programs", "Widget"));

        Assert.False(HunterService.MatchExecutable(exe, null, 123, [program]).Matched);
    }

    [Fact]
    public void Relative_install_locations_do_not_claim_files_under_the_working_directory()
    {
        var exe = Path.Combine(Environment.CurrentDirectory, "relative-app", "helper.exe");
        var program = TestPrograms.Create("Widget", installLocation: "relative-app");

        Assert.False(HunterService.MatchExecutable(exe, null, 123, [program]).Matched);
    }

    [Fact]
    public void Drive_root_install_location_does_not_claim_every_application()
    {
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Workspace/Widget.exe", "stub");
        var program = TestPrograms.Create("Other App", installLocation: Path.GetPathRoot(exe));

        Assert.False(HunterService.MatchExecutable(exe, null, 123, [program]).Matched);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("icon")]
    [InlineData("uninstaller")]
    public void Registry_paths_expand_environment_variables_and_ignore_case(string evidence)
    {
        // TEMP is supplied by Windows; no process-wide environment mutation is needed.
        using var env = new TestEnvironment();
        var exe = env.CreateFile("Widget/widget.exe", "stub");
        var relative = Path.GetRelativePath(Path.GetTempPath(), exe);
        var variablePath = Path.Combine("%TEMP%", relative);
        var program = TestPrograms.Create("Widget",
            installLocation: evidence == "directory" ? $"\"{Path.GetDirectoryName(variablePath)}\\\"" : null,
            displayIcon: evidence == "icon" ? $"\"{variablePath}\",0" : null,
            uninstallCommand: evidence == "uninstaller" ? $"\"{variablePath}\" /remove" : null);

        var match = HunterService.MatchExecutable(exe.ToUpperInvariant(), null, 123, [program]);

        Assert.Equal(program.Id, match.Program?.Id);
    }
}
