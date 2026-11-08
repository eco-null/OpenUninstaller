using System.Security;
using Microsoft.Win32;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Abstractions;

/// <summary>
/// Registry access through hive + view qualified locations. The .NET registry API reports missing
/// rights as <see cref="SecurityException"/> (for example opening an HKLM key for writing without
/// elevation); this port reports every access denial as <see cref="UnauthorizedAccessException"/>
/// so callers handle registry and filesystem denials with the same catch.
/// </summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    public bool KeyExists(RegistryLocation location) => Guard(location, () =>
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        return key is not null;
    });

    public RegistryValue? GetValue(RegistryLocation location, string name) => Guard(location, () =>
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
    });

    public IReadOnlyList<string> GetValueNames(RegistryLocation location) => Guard(location, () =>
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        return (IReadOnlyList<string>?)key?.GetValueNames() ?? [];
    });

    public IReadOnlyList<string> GetSubKeyNames(RegistryLocation location) => Guard(location, () =>
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: false);
        return (IReadOnlyList<string>?)key?.GetSubKeyNames() ?? [];
    });

    public void CreateSubKey(RegistryLocation location) => Guard(location, () =>
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.CreateSubKey(location.Path, writable: true);
        key?.Flush();
        return true;
    });

    public void SetValue(RegistryLocation location, string name, RegistryValueKind kind, object data) => Guard(location, () =>
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.CreateSubKey(location.Path, writable: true)
            ?? throw new UnauthorizedAccessException($"Cannot open {location.DisplayPath} for writing.");
        key.SetValue(name, data, kind);
        return true;
    });

    public void DeleteValue(RegistryLocation location, string name) => Guard(location, () =>
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
        return true;
    });

    public void DeleteKey(RegistryLocation location, bool recursive) => Guard(location, () =>
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

        return true;
    });

    private static T Guard<T>(RegistryLocation location, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SecurityException ex)
        {
            throw new UnauthorizedAccessException($"Access to {location.DisplayPath} was denied.", ex);
        }
    }
}
