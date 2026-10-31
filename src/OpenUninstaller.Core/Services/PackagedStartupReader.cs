using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Reads declared startup tasks, not every installed package or unrelated AppModel state key.</summary>
internal sealed class PackagedStartupReader(IRegistryStore registry, AppPaths paths)
{
    public void AddEntries(AutorunScanResult result)
    {
        foreach (var package in new MsixInventoryService(registry, paths).Scan()
                     .OrderByDescending(p => Version.TryParse(p.Version, out var version) ? version : new Version()))
        {
            if (package.InstallLocation is not { } directory || package.IsFramework || package.IsResourcePackage) continue;
            try
            {
                var manifestFile = Path.Combine(directory, "AppxManifest.xml");
                using var reader = XmlReader.Create(manifestFile, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
                var document = XDocument.Load(reader);
                var parts = package.PackageFullName.Split('_');
                if (parts.Length < 5) continue;
                var family = parts[0] + "_" + parts[^1];
                foreach (var extension in document.Descendants().Where(e => e.Name.LocalName == "Extension"
                             && (string?)e.Attribute("Category") == "windows.startupTask"))
                {
                    foreach (var task in extension.Elements().Where(e => e.Name.LocalName == "StartupTask"))
                    {
                        var taskId = (string?)task.Attribute("TaskId");
                        if (string.IsNullOrWhiteSpace(taskId) || taskId.IndexOfAny(['\\', '/']) >= 0) continue;
                        var stateRoot = paths.PackagedStartupStateRoot;
                        var stateLocation = stateRoot with { Path = $"{stateRoot.Path}\\{family}\\{taskId}" };
                        var state = registry.GetValue(stateLocation, "State")?.AsInt32();
                        var manifestEnabled = string.Equals((string?)task.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase);
                        var enabled = state is null ? manifestEnabled : state is 2 or 4;
                        var application = extension.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Application");
                        var relativeExe = (string?)extension.Attribute("Executable") ?? (string?)application?.Attribute("Executable");
                        string? executable = null;
                        if (!string.IsNullOrWhiteSpace(relativeExe))
                        {
                            var full = Path.GetFullPath(Path.Combine(directory, relativeExe));
                            if (full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase)) executable = full;
                        }
                        var name = ResolveName((string?)task.Attribute("DisplayName"), package)
                                   ?? ResolveName(package.DisplayName, package) ?? package.Name;
                        var publisher = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
                        result.Entries.Add(new AutorunEntry
                        {
                            Id = Hashing.StableId(nameof(AutorunSourceKind.PackagedStartupTask), family, taskId),
                            Name = name, Command = executable ?? $"{package.Name} ({taskId})", ExecutablePath = executable,
                            Source = AutorunSourceKind.PackagedStartupTask, IsEnabled = enabled,
                            RegistryLocation = stateLocation, LocationDisplay = $"Windows app: {family} / {taskId}",
                            Publisher = ResolveName(publisher, package) ?? package.Publisher
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                          or XmlException or ArgumentException or NotSupportedException)
            {
                result.Warnings.Add($"Windows startup apps: {package.Name}: {ex.Message}");
            }
        }
    }

    private static string? ResolveName(string? value, MsixPackage package)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
        var suffix = value[12..];
        var candidates = suffix.StartsWith("//", StringComparison.Ordinal)
            ? new[] { value }
            : new[] { $"ms-resource://{package.Name}/resources/{suffix.TrimStart('/')}", $"ms-resource://{package.Name}/{suffix.TrimStart('/')}" };
        foreach (var resource in candidates)
        {
            var buffer = new char[1024];
            if (SHLoadIndirectString($"@{{{package.PackageFullName}?{resource}}}", buffer, (uint)buffer.Length, nint.Zero) == 0)
            {
                var length = Array.IndexOf(buffer, '\0');
                if (length > 0) return new string(buffer, 0, length);
            }
        }
        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, [Out] char[] output, uint count, nint reserved);
}
