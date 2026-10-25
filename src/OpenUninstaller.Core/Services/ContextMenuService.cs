using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Manages Windows Explorer context menu integration under HKCU\Software\Classes.
/// Adds "Uninstall with OpenUninstaller" for files, directories, and shortcut links.
/// </summary>
public sealed class ContextMenuService
{
    private const string ShellKeyName = "OpenUninstaller";
    private static readonly string[] ShellTargets =
    [
        @"Software\Classes\*\shell",
        @"Software\Classes\Directory\shell",
        @"Software\Classes\lnkfile\shell"
    ];

    public string DefaultMenuText { get; set; } = "Uninstall with OpenUninstaller";

    public bool IsEnabled()
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(ShellTargets[0] + @"\" + ShellKeyName);
            if (baseKey is null)
            {
                return false;
            }

            using var cmdKey = baseKey.OpenSubKey("command");
            return cmdKey is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Register(string? appExecutablePath = null, string? customLabel = null)
    {
        try
        {
            var exePath = appExecutablePath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            var label = !string.IsNullOrWhiteSpace(customLabel) ? customLabel : DefaultMenuText;
            var commandLine = $"\"{exePath}\" --target \"%1\"";

            foreach (var target in ShellTargets)
            {
                using var parentKey = Registry.CurrentUser.CreateSubKey(target);
                if (parentKey is null)
                {
                    continue;
                }

                using var itemKey = parentKey.CreateSubKey(ShellKeyName);
                if (itemKey is null)
                {
                    continue;
                }

                itemKey.SetValue(string.Empty, label, RegistryValueKind.String);
                itemKey.SetValue("Icon", $"\"{exePath}\"", RegistryValueKind.String);

                using var cmdKey = itemKey.CreateSubKey("command");
                cmdKey?.SetValue(string.Empty, commandLine, RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Unregister()
    {
        try
        {
            foreach (var target in ShellTargets)
            {
                using var parentKey = Registry.CurrentUser.OpenSubKey(target, writable: true);
                if (parentKey is null)
                {
                    continue;
                }

                try
                {
                    parentKey.DeleteSubKeyTree(ShellKeyName, throwOnMissingSubKey: false);
                }
                catch (Exception)
                {
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool SetEnabled(bool enable, string? appExecutablePath = null, string? customLabel = null)
    {
        return enable ? Register(appExecutablePath, customLabel) : Unregister();
    }
}
