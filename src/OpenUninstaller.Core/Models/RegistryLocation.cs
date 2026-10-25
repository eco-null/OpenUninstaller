using Microsoft.Win32;

namespace OpenUninstaller.Core.Models;

/// <summary>
/// A registry path qualified by hive and bitness view. All registry access goes through this type
/// so that 32-bit and 64-bit uninstall entries are never conflated.
/// </summary>
public readonly record struct RegistryLocation(RegistryHive Hive, RegistryView View, string Path)
{
    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => Hive.ToString()
    };

    public bool IsMachine => Hive == RegistryHive.LocalMachine;

    public bool Is32BitView => View == RegistryView.Registry32;

    /// <summary>Human readable path, e.g. <c>HKCU\Software\Vendor\App (32-bit)</c>.</summary>
    public string DisplayPath =>
        Is32BitView ? $"{HiveName}\\{Path} (32-bit)" : $"{HiveName}\\{Path}";

    /// <summary>
    /// Path usable with <c>reg.exe</c>. The 32-bit view of HKLM and HKCU lives under the
    /// <c>WOW6432Node</c> redirector, which reg.exe addresses explicitly.
    /// </summary>
    public string RegExePath
    {
        get
        {
            if (!Is32BitView)
            {
                return $"{HiveName}\\{Path}";
            }

            const string software = "SOFTWARE\\";
            var body = Path.StartsWith(software, StringComparison.OrdinalIgnoreCase)
                ? Path[software.Length..]
                : Path;
            return $"{HiveName}\\SOFTWARE\\WOW6432Node\\{body}";
        }
    }

    public override string ToString() => DisplayPath;
}

/// <summary>A registry value read through <see cref="Abstractions.IRegistryStore"/>.</summary>
public sealed record RegistryValue(string Name, RegistryValueKind Kind, object? Data)
{
    public string? AsString()
    {
        return Data switch
        {
            null => null,
            string s => s,
            string[] many => string.Join("; ", many),
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => Data.ToString()
        };
    }

    public int? AsInt32()
    {
        try
        {
            return Data switch
            {
                int i => i,
                long l => checked((int)l),
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => null
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
