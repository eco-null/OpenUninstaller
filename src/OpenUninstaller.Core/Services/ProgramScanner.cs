using System.Globalization;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Reads installed applications from the Windows uninstall registry.</summary>
public sealed class ProgramScanner
{
    public static readonly RegistryLocation[] Sources =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, RegistryGuard.UninstallRoot),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, RegistryGuard.UninstallRoot),
        new(RegistryHive.CurrentUser, RegistryView.Registry64, RegistryGuard.UninstallRoot),
        new(RegistryHive.CurrentUser, RegistryView.Registry32, RegistryGuard.UninstallRoot)
    ];

    private readonly IRegistryStore _registry;
    private readonly IReadOnlyList<RegistryLocation> _sources;

    public ProgramScanner(IRegistryStore registry, IReadOnlyList<RegistryLocation>? sources = null)
    {
        _registry = registry;
        _sources = sources ?? Sources;
    }

    public IReadOnlyList<InstalledProgram> Scan(bool includeSystemComponents = false)
    {
        var programs = new List<InstalledProgram>();
        foreach (var source in _sources)
        {
            foreach (var subKey in _registry.GetSubKeyNames(source))
            {
                var location = new RegistryLocation(source.Hive, source.View, $"{source.Path}\\{subKey}");
                var program = Read(location, includeSystemComponents);
                if (program is not null)
                {
                    programs.Add(program);
                }
            }
        }

        return programs
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    public InstalledProgram? FindById(string id, bool includeSystemComponents = true)
        => Scan(includeSystemComponents).FirstOrDefault(p => p.Id == id);

    public IReadOnlyList<InstalledProgram> FindAllByTarget(string target, bool includeSystemComponents = true)
    {
        var programs = Scan(includeSystemComponents);
        var byId = programs.Where(p => string.Equals(p.Id, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count > 0)
        {
            return byId;
        }

        var exact = programs
            .Where(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return programs
            .Where(p => p.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public InstalledProgram? Read(RegistryLocation location, bool includeSystemComponents)
    {
        string? Get(string name) => _registry.GetValue(location, name)?.AsString();

        var name = Get("DisplayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var systemComponent = Get("SystemComponent") is { } component
            && int.TryParse(component, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flag)
            && flag == 1;
        if (systemComponent && !includeSystemComponents)
        {
            return null;
        }

        if (!includeSystemComponents)
        {
            if (!string.IsNullOrWhiteSpace(Get("ParentKeyName")))
            {
                return null;
            }

            var releaseType = Get("ReleaseType");
            if (!string.IsNullOrWhiteSpace(releaseType)
                && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                    || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
                    || releaseType.Contains("Service Pack", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            if (IsKnowledgeBaseUpdate(name))
            {
                return null;
            }
        }

        var estimatedKb = _registry.GetValue(location, "EstimatedSize")?.AsInt32();
        var installDate = ParseInstallDate(Get("InstallDate"));
        var windowsInstaller = Get("WindowsInstaller") == "1"
            || string.Equals(Path.GetFileName(Get("UninstallString") ?? string.Empty), "msiexec.exe", StringComparison.OrdinalIgnoreCase);

        return new InstalledProgram
        {
            Id = Hashing.StableId(location.HiveName, location.View.ToString(), location.Path),
            Name = name.Trim(),
            Version = Clean(Get("DisplayVersion")),
            Publisher = Clean(Get("Publisher")),
            InstallLocation = Clean(Environment.ExpandEnvironmentVariables(Get("InstallLocation") ?? string.Empty)),
            UninstallCommand = Clean(Environment.ExpandEnvironmentVariables(Get("UninstallString") ?? string.Empty)),
            QuietUninstallCommand = Clean(Environment.ExpandEnvironmentVariables(Get("QuietUninstallString") ?? string.Empty)),
            DisplayIcon = Clean(Get("DisplayIcon")),
            EstimatedSizeBytes = estimatedKb is > 0 ? estimatedKb.Value * 1024L : null,
            InstallDate = installDate,
            Location = location,
            Architecture = location.View == RegistryView.Registry32
                ? ProgramArchitecture.X86
                : ProgramArchitecture.X64,
            IsWindowsInstaller = windowsInstaller,
            IsSystemComponent = systemComponent,
            UrlInfoAbout = Clean(Get("URLInfoAbout")),
            Comments = Clean(Get("Comments"))
        };
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsKnowledgeBaseUpdate(string name)
    {
        var trimmed = name.TrimStart();
        if (!trimmed.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = 0;
        foreach (var ch in trimmed.AsSpan(2))
        {
            if (!char.IsAsciiDigit(ch))
            {
                break;
            }

            digits++;
        }

        return digits >= 5;
    }

    private static DateOnly? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
