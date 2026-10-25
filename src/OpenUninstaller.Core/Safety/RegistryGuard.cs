using Microsoft.Win32;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Safety;

/// <summary>Registry equivalent of <see cref="PathGuard"/>.</summary>
public static class RegistryGuard
{
    public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallRoot32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string FirewallRulesPath =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>Containers under <c>Classes</c> that are never treated as one application's ProgID.</summary>
    private static readonly HashSet<string> ClassContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "AppID", "Applications", "App Paths", "Directory", "Drive", "Folder", "Interface",
        "TypeLib", "Installer", "SystemFileAssociations", "MIME", "PackagedCom", "WOW6432Node",
        "Microsoft", "ActivatableClasses", "Extensions", "Local Settings"
    };

    private static readonly string[] ExactProtected =
    [
        @"SOFTWARE",
        @"SYSTEM",
        @"SAM",
        @"SECURITY",
        @"HARDWARE",
        @"BCD00000000"
    ];

    private static readonly string[] DescendantProtected =
    [
        @"SOFTWARE\MICROSOFT",
        @"SOFTWARE\WOW6432NODE\MICROSOFT",
        @"SOFTWARE\WOW6432NODE",
        @"SOFTWARE\CLASSES",
        @"SOFTWARE\WOW6432NODE\CLASSES",
        @"SOFTWARE\OPENUNINSTALLER",
        @"SYSTEM"
    ];

    /// <summary>
    /// Validates a registry key. The optional flags are only set by services that scanned the
    /// matching, narrowly scoped root; they never turn into a general bypass.
    /// </summary>
    public static GuardResult ValidateForDeletion(
        RegistryLocation location,
        bool allowUninstallEntry = false,
        bool allowAppCompatEntry = false,
        bool allowServiceEntry = false,
        bool allowAssociationEntry = false,
        bool allowComEntry = false)
    {
        if (location.Hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser))
        {
            return GuardResult.Reject("Only HKLM and HKCU keys can be removed.");
        }

        var path = (location.Path ?? string.Empty).Trim().Trim('\\');
        if (path.Length == 0)
        {
            return GuardResult.Reject("Registry path is empty.");
        }

        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return GuardResult.Reject("Refusing to delete a top-level registry key.");
        }

        var normalized = string.Join('\\', parts).ToUpperInvariant();
        var normalizedUninstall = UninstallRoot.ToUpperInvariant();
        var normalizedUninstall32 = UninstallRoot32.ToUpperInvariant();

        if (normalized == normalizedUninstall || normalized == normalizedUninstall32)
        {
            return GuardResult.Reject("The uninstall registry root itself is protected.");
        }

        var isUninstallEntry = normalized.StartsWith(normalizedUninstall + "\\", StringComparison.Ordinal)
            || normalized.StartsWith(normalizedUninstall32 + "\\", StringComparison.Ordinal);
        if (isUninstallEntry)
        {
            // Forced uninstall is the only operation allowed to remove a registration entry.
            return allowUninstallEntry
                ? GuardResult.Allow($"{location.HiveName}\\{string.Join('\\', parts)}")
                : GuardResult.Reject("Uninstall registration entries need the explicit forced-uninstall mode.");
        }

        // App Paths and Classes\Applications entries are small, per-executable registration keys.
        // Only a direct child of one of those containers may be removed; the containers themselves
        // and anything below the child stay protected.
        if (allowAppCompatEntry && IsDirectAppCompatChild(parts))
        {
            return GuardResult.Allow($"{location.HiveName}\\{string.Join('\\', parts)}");
        }

        // A service definition is a direct child of Services. Service removal is only offered for
        // orphaned or app-owned services; the caller proves that, the guard enforces depth.
        if (allowServiceEntry
            && parts.Length >= 4
            && string.Equals(parts[^2], "SERVICES", StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Allow($"{location.HiveName}\\{string.Join('\\', parts)}");
        }

        // File associations live in Classes\<ProgID>. Only a direct child of Classes may be
        // removed, never one of the shared containers and never the Classes root itself.
        if (allowAssociationEntry && IsDirectClassChild(parts))
        {
            return GuardResult.Allow($"{location.HiveName}\\{string.Join('\\', parts)}");
        }

        // COM servers live in Classes\CLSID\<guid>. Only a direct child whose name parses as a
        // GUID is accepted, so a malformed name cannot widen the scope.
        if (allowComEntry && IsDirectComChild(parts))
        {
            return GuardResult.Allow($"{location.HiveName}\\{string.Join('\\', parts)}");
        }

        if (ExactProtected.Contains(normalized, StringComparer.Ordinal))
        {
            return GuardResult.Reject("This key is on the protected registry list.");
        }

        foreach (var prefix in DescendantProtected)
        {
            if (normalized == prefix || normalized.StartsWith(prefix + "\\", StringComparison.Ordinal))
            {
                return GuardResult.Reject("This key is on the protected registry list.");
            }
        }

        // Autorun values are toggled by the autorun service, never deleted by a leftover scan.
        if (normalized.Contains(@"\CURRENTVERSION\RUN", StringComparison.Ordinal)
            || normalized.Contains(@"\CURRENTVERSION\RUNONCE", StringComparison.Ordinal))
        {
            return GuardResult.Reject("Autorun entries are managed by the autorun service, not the cleaner.");
        }

        return GuardResult.Allow($"{location.HiveName}\\{string.Join('\\', parts)}");
    }

    /// <summary>
    /// Values are deleted individually. The containing key still has to pass the normal key guard;
    /// a value inside a protected tree (Microsoft, SYSTEM, autoruns) is never offered.
    /// </summary>
    public static GuardResult ValidateValueForDeletion(RegistryLocation location)
        => ValidateForDeletion(location);

    /// <summary>
    /// A firewall rule is one value in the Defender rule store. The key itself is protected; only
    /// the exact, scanner-proven value name is allowed, and each deletion is backed up by exporting
    /// the store first.
    /// </summary>
    public static GuardResult ValidateFirewallRuleValue(
        RegistryLocation location,
        string? valueName,
        RegistryLocation? expectedRoot = null)
    {
        if (location.Hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser))
        {
            return GuardResult.Reject("Only HKLM and HKCU keys can be removed.");
        }

        if (string.IsNullOrWhiteSpace(valueName) || valueName.Length > 512 || valueName.Contains('\0'))
        {
            return GuardResult.Reject("The firewall rule value name is not valid.");
        }

        var path = (location.Path ?? string.Empty).Trim().Trim('\\');
        var expectedPath = (expectedRoot ?? new RegistryLocation(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            FirewallRulesPath)).Path.Trim().Trim('\\');
        if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Reject("Only a value in the Windows Defender Firewall rule store can be removed here.");
        }

        return GuardResult.Allow($"{location.HiveName}\\{path} :: {valueName}");
    }

    /// <summary>
    /// A PATH segment is rewritten, not deleted. The guard pins the exact key/value, rejects shell
    /// metacharacters and traversal, and refuses any segment that resolves inside Windows, Program
    /// Files or a system root so a malformed PATH can never break the machine.
    /// </summary>
    public static GuardResult ValidatePathEntryForDeletion(
        RegistryLocation location,
        string? valueName,
        string? segment,
        AppPaths paths,
        bool machineWide)
    {
        if (location.Hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser))
        {
            return GuardResult.Reject("Only HKLM and HKCU keys can be modified.");
        }

        if (!string.Equals(valueName, "Path", StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Reject("Only the PATH environment value can be changed here.");
        }

        var scopeRoot = machineWide ? paths.MachineEnvironmentRoot : paths.UserEnvironmentRoot;
        var expected = scopeRoot.Path.Trim().Trim('\\');
        var actual = (location.Path ?? string.Empty).Trim().Trim('\\');
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            || location.Hive != scopeRoot.Hive)
        {
            return GuardResult.Reject("The PATH environment key does not match the requested scope.");
        }

        if (string.IsNullOrWhiteSpace(segment) || segment.Length > 30_000)
        {
            return GuardResult.Reject("The PATH segment is empty or too long.");
        }

        var value = segment.Trim();
        if (value.IndexOfAny(['\0', '\r', '\n', ';', '"']) >= 0)
        {
            return GuardResult.Reject("The PATH segment contains separators or quotes.");
        }

        if (value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(p => p == ".."))
        {
            return GuardResult.Reject("The PATH segment contains a parent-directory traversal.");
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return GuardResult.Reject("UNC and device paths are not part of a managed PATH segment.");
        }

        var expanded = Environment.ExpandEnvironmentVariables(value).Trim();
        if (!Path.IsPathRooted(expanded))
        {
            return GuardResult.Reject("The PATH segment must be an absolute directory after expansion.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardResult.Reject("The PATH segment is not a valid path.");
        }

        foreach (var protectedRoot in new[]
                 {
                     paths.WindowsDirectory,
                     paths.ProgramFiles,
                     paths.ProgramFilesX86
                 })
        {
            if (string.IsNullOrWhiteSpace(protectedRoot))
            {
                continue;
            }

            var root = Path.GetFullPath(protectedRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return GuardResult.Reject("System, Windows and Program Files PATH entries are protected.");
            }
        }

        return GuardResult.Allow(full);
    }

    private static bool IsDirectClassChild(string[] parts)
    {
        if (parts.Length < 3 || !string.Equals(parts[^2], "Classes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ClassContainers.Contains(parts[^1]);
    }

    private static bool IsDirectComChild(string[] parts)
    {
        if (parts.Length < 4 || !string.Equals(parts[^2], "CLSID", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParse(parts[^1], out _);
    }

    private static bool IsDirectAppCompatChild(string[] parts)
    {
        if (parts.Length < 2)
        {
            return false;
        }

        if (string.Equals(parts[^2], "App Paths", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return parts.Length >= 3
               && string.Equals(parts[^3], "Classes", StringComparison.OrdinalIgnoreCase)
               && string.Equals(parts[^2], "Applications", StringComparison.OrdinalIgnoreCase);
    }
}
