using System.Globalization;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

public sealed record SnapshotValue(string Name, string Fingerprint);

public sealed record SnapshotKey(
    RegistryHive Hive,
    RegistryView View,
    string Path,
    Dictionary<string, SnapshotValue> Values)
{
    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        _ => Hive.ToString()
    };
}

public sealed class RegistrySnapshot
{
    public Dictionary<string, SnapshotKey> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Truncated { get; set; }

    public static string KeyId(RegistryHive hive, RegistryView view, string path)
        => $"{hive}|{view}|{path.Trim('\\')}";
}

/// <summary>
/// Captures a bounded tree of registry keys and value fingerprints. Data is hashed, never logged.
/// </summary>
public sealed class RegistrySnapshotter
{
    private readonly IRegistryStore _registry;

    public RegistrySnapshotter(IRegistryStore registry)
    {
        _registry = registry;
    }

    public RegistrySnapshot Capture(
        IReadOnlyList<RegistryLocation> scopes,
        int maxKeys = 100_000,
        int maxDepth = 12,
        CancellationToken cancellationToken = default)
    {
        var snapshot = new RegistrySnapshot();
        foreach (var scope in scopes)
        {
            var stack = new Stack<(RegistryLocation Location, int Depth)>();
            stack.Push((scope, 0));
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Keys.Count >= maxKeys)
                {
                    snapshot.Truncated = true;
                    return snapshot;
                }

                var (location, depth) = stack.Pop();
                var normalizedPath = location.Path.Trim('\\');
                if (IsExcluded(normalizedPath))
                {
                    continue;
                }

                IReadOnlyList<string> valueNames;
                try
                {
                    valueNames = _registry.GetValueNames(location);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                var values = new Dictionary<string, SnapshotValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var valueName in valueNames)
                {
                    var value = _registry.GetValue(location, valueName);
                    if (value is null)
                    {
                        continue;
                    }

                    values[valueName] = new SnapshotValue(valueName, Fingerprint(value));
                }

                var id = RegistrySnapshot.KeyId(location.Hive, location.View, normalizedPath);
                snapshot.Keys[id] = new SnapshotKey(
                    location.Hive,
                    location.View,
                    normalizedPath,
                    values);

                if (depth >= maxDepth)
                {
                    snapshot.Truncated = true;
                    continue;
                }

                IReadOnlyList<string> children;
                try
                {
                    children = _registry.GetSubKeyNames(location);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    stack.Push((new RegistryLocation(location.Hive, location.View, $"{normalizedPath}\\{child}"), depth + 1));
                }
            }
        }

        return snapshot;
    }

    public static RegistrySnapshotDiff Diff(RegistrySnapshot before, RegistrySnapshot after, int maxEntries = 5000)
    {
        var addedKeys = new List<TracedRegistryKey>();
        var addedValues = new List<TracedRegistryKey>();
        var truncated = before.Truncated || after.Truncated;

        foreach (var (id, afterKey) in after.Keys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (addedKeys.Count + addedValues.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            if (!before.Keys.TryGetValue(id, out var beforeKey))
            {
                addedKeys.Add(new TracedRegistryKey
                {
                    Hive = afterKey.Hive,
                    View = afterKey.View,
                    Path = afterKey.Path,
                    ValueNames = afterKey.Values.Keys.ToList()
                });
                continue;
            }

            var newValues = afterKey.Values.Keys
                .Where(name => !beforeKey.Values.ContainsKey(name))
                .ToList();
            if (newValues.Count > 0)
            {
                addedValues.Add(new TracedRegistryKey
                {
                    Hive = afterKey.Hive,
                    View = afterKey.View,
                    Path = afterKey.Path,
                    ValueNames = newValues
                });
            }
        }

        var shallowest = DropKeysWithAddedAncestor(addedKeys);
        return new RegistrySnapshotDiff
        {
            AddedKeys = shallowest,
            AddedValuesToExistingKeys = addedValues,
            Truncated = truncated
        };
    }

    private static List<TracedRegistryKey> DropKeysWithAddedAncestor(List<TracedRegistryKey> keys)
    {
        var paths = keys
            .Select(k => $"{k.Hive}|{k.View}|{k.Path}".ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<TracedRegistryKey>();
        foreach (var key in keys)
        {
            var path = key.Path;
            var hasAncestor = false;
            while (path.Contains('\\', StringComparison.Ordinal))
            {
                path = path[..path.LastIndexOf('\\')];
                var id = $"{key.Hive}|{key.View}|{path}".ToUpperInvariant();
                if (paths.Contains(id))
                {
                    hasAncestor = true;
                    break;
                }
            }

            if (!hasAncestor)
            {
                result.Add(key);
            }
        }

        return result
            .OrderBy(k => k.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static readonly string[] ExcludedKeyPatterns =
    [
        @"OpenUninstaller",
        @"Microsoft\Windows\Shell",
        @"Microsoft\Windows\ShellNoRoam",
        @"Microsoft\Windows\CurrentVersion\Explorer\MountPoints2",
        @"Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
        @"Microsoft\Windows NT\CurrentVersion\SystemRestore",
        @"Microsoft\Windows NT\CurrentVersion\SPP",
        @"Microsoft\Windows NT\CurrentVersion\ClipSVC",
        @"Microsoft\RestartManager",
        @"Microsoft\Windows\CurrentVersion\Internet Settings\Cache",
        @"Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap",
        @"Microsoft\Rpc",
        @"Microsoft\Windows\CurrentVersion\SideBySide",
        @"Classes\Local Settings\MuiCache"
    ];

    private static bool IsExcluded(string path)
    {
        foreach (var pattern in ExcludedKeyPatterns)
        {
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Fingerprint(RegistryValue value)
    {
        var data = value.Data switch
        {
            null => "<null>",
            byte[] bytes => Hashing.Sha256Hex(bytes),
            string[] many => string.Join("\u001f", many),
            _ => Convert.ToString(value.Data, CultureInfo.InvariantCulture) ?? string.Empty
        };

        return $"{value.Kind}|{Hashing.Sha256Hex(data)}";
    }
}
