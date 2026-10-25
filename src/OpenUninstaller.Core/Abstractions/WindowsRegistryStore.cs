using Microsoft.Win32;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Abstractions;

/// <summary>Registry access through hive + view qualified locations.</summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    public bool KeyExists(RegistryLocation location)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        return key is not null;
    }

    public RegistryValue? GetValue(RegistryLocation location, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        if (key is null)
        {
            return null;
        }

        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return null;
        }

        return new RegistryValue(name, key.GetValueKind(name), value);
    }

    public IReadOnlyList<string> GetValueNames(RegistryLocation location)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        return key?.GetValueNames() ?? [];
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryLocation location)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    public void CreateSubKey(RegistryLocation location)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.CreateSubKey(location.Path, writable: true);
        key?.Flush();
    }

    public void SetValue(RegistryLocation location, string name, RegistryValueKind kind, object data)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.CreateSubKey(location.Path, writable: true)
            ?? throw new UnauthorizedAccessException($"Cannot open {location.DisplayPath} for writing.");
        key.SetValue(name, data, kind);
    }

    public void DeleteValue(RegistryLocation location, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKey(RegistryLocation location, bool recursive)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        if (recursive)
        {
            baseKey.DeleteSubKeyTree(location.Path, throwOnMissingSubKey: false);
        }
        else
        {
            baseKey.DeleteSubKey(location.Path, throwOnMissingSubKey: false);
        }
    }
}
