using System.Xml.Linq;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Read-only inventory of MSIX/Store packages for the current user. The package repository is a
/// registry list of package roots; each <c>AppxManifest.xml</c> is parsed with local-name matching
/// so manifest namespace revisions do not break the reader. Nothing here can install, update,
/// activate or remove a package.
/// </summary>
public sealed class MsixInventoryService
{
    private readonly IRegistryStore _registry;
    private readonly AppPaths _paths;

    public MsixInventoryService(IRegistryStore registry, AppPaths paths)
    {
        _registry = registry;
        _paths = paths;
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
