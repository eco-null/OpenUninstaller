using System.Diagnostics;
using System.Xml.Linq;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Inventory and lifecycle management for MSIX/Store packages.
/// Provides safe inventory scanning, uninstallation via Remove-AppxPackage, and package reset via Reset-AppxPackage.
/// </summary>
public sealed class MsixInventoryService
{
    private readonly IRegistryStore _registry;
    private readonly AppPaths _paths;
    private readonly IProcessRunner _runner;

    public MsixInventoryService(IRegistryStore registry, AppPaths paths, IProcessRunner? runner = null)
    {
        _registry = registry;
        _paths = paths;
        _runner = runner ?? new ProcessRunner();
    }

    public IReadOnlyList<MsixPackage> Scan()
    {
        if (!_registry.KeyExists(_paths.MsixPackagesRoot))
        {
            return [];
        }

        IReadOnlyList<string> packages;
        try
        {
            packages = _registry.GetSubKeyNames(_paths.MsixPackagesRoot);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }

        var result = new List<MsixPackage>();
        foreach (var packageFullName in packages.Take(5000))
        {
            var location = new RegistryLocation(
                _paths.MsixPackagesRoot.Hive,
                _paths.MsixPackagesRoot.View,
                $"{_paths.MsixPackagesRoot.Path}\\{packageFullName}");
            string? rootFolder;
            try
            {
                rootFolder = _registry.GetValue(location, "PackageRootFolder")?.AsString();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                continue;
            }

            var manifestPath = Path.Combine(rootFolder, "AppxManifest.xml");
            var manifest = TryReadManifest(manifestPath);
            result.Add(new MsixPackage
            {
                PackageFullName = packageFullName,
                Name = manifest.Name ?? packageFullName.Split('_')[0],
                DisplayName = manifest.DisplayName ?? manifest.Name ?? packageFullName.Split('_')[0],
                Publisher = manifest.Publisher,
                Version = manifest.Version,
                Architecture = manifest.Architecture,
                InstallLocation = rootFolder,
                IsFramework = manifest.IsFramework,
                IsResourcePackage = manifest.IsResource
            });
        }

        return result
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Uninstalls an MSIX/Store package for the current user using Remove-AppxPackage.
    /// </summary>
    public async Task<(bool Success, string? Error)> UninstallPackageAsync(
        string packageFullName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageFullName) || !IsValidPackageFullName(packageFullName))
        {
            return (false, "Invalid package full name.");
        }

        var escapedName = packageFullName.Replace("'", "''");
        var script = $"Remove-AppxPackage -Package '{escapedName}'";

        var result = await _runner.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            shellExecute: false,
            timeout: TimeSpan.FromMinutes(3),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return (false, result.Error ?? "Could not launch PowerShell.");
        }

        if (result.ExitCode != 0)
        {
            return (false, $"Package removal failed with exit code {result.ExitCode}.");
        }

        return (true, null);
    }

    /// <summary>
    /// Resets an MSIX/Store package for the current user using Reset-AppxPackage.
    /// </summary>
    public async Task<(bool Success, string? Error)> ResetPackageAsync(
        string packageFullName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageFullName) || !IsValidPackageFullName(packageFullName))
        {
            return (false, "Invalid package full name.");
        }

        var escapedName = packageFullName.Replace("'", "''");
        var script = $"Reset-AppxPackage -Package '{escapedName}'";

        var result = await _runner.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            shellExecute: false,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return (false, result.Error ?? "Could not launch PowerShell.");
        }

        if (result.ExitCode != 0)
        {
            return (false, $"Package reset failed with exit code {result.ExitCode}.");
        }

        return (true, null);
    }

    /// <summary>
    /// Terminates running processes associated with the package.
    /// </summary>
    public async Task<(bool Success, string? Error)> TerminatePackageAsync(
        string packageFullName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageFullName) || !IsValidPackageFullName(packageFullName))
        {
            return (false, "Invalid package full name.");
        }

        var escapedName = packageFullName.Replace("'", "''");
        var script = $"Get-Process | Where-Object {{ try {{ $_.Path -like '*{escapedName}*' }} catch {{ $false }} }} | Stop-Process -Force";

        var result = await _runner.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            shellExecute: false,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return (false, result.Error ?? "Could not launch PowerShell.");
        }

        return (true, null);
    }

    /// <summary>
    /// Opens the installation location of the package in File Explorer.
    /// </summary>
    public static bool OpenPackageLocation(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installLocation,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidPackageFullName(string name)
        => !string.IsNullOrWhiteSpace(name) &&
           name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    private static ManifestInfo TryReadManifest(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ManifestInfo();
            }

            var info = new FileInfo(path);
            if (info.Length > 4_000_000)
            {
                return new ManifestInfo();
            }

            var document = XDocument.Load(path);
            var root = document.Root;
            if (root is null)
            {
                return new ManifestInfo();
            }

            var identity = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
            var properties = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var displayName = ChildValue(properties, "DisplayName");
            var name = identity?.Attribute("Name")?.Value;
            var publisher = ChildValue(properties, "PublisherDisplayName")
                            ?? identity?.Attribute("Publisher")?.Value;

            return new ManifestInfo
            {
                Name = name,
                DisplayName = Humanize(displayName),
                Publisher = Humanize(publisher),
                Version = identity?.Attribute("Version")?.Value,
                Architecture = identity?.Attribute("ProcessorArchitecture")?.Value,
                IsFramework = string.Equals(
                    identity?.Attribute("IsFramework")?.Value, "true", StringComparison.OrdinalIgnoreCase),
                IsResource = identity?.Elements().Any(e =>
                    e.Name.LocalName == "ResourcePackage"
                    && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) == true
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Xml.XmlException)
        {
            return new ManifestInfo();
        }
    }

    private static string? ChildValue(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    /// <summary>Manifest display names may be indirect resource references; show the package name then.</summary>
    private static string? Humanize(string? value)
        => string.IsNullOrWhiteSpace(value) || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private sealed class ManifestInfo
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? Publisher { get; init; }
        public string? Version { get; init; }
        public string? Architecture { get; init; }
        public bool IsFramework { get; init; }
        public bool IsResource { get; init; }
    }
}
