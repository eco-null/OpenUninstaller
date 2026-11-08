using System.Text;
using System.Text.RegularExpressions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Turns a registry uninstall string into an executable plus argument list. Never invokes a shell;
/// arguments are passed as a single properly quoted Windows command line.
/// </summary>
public static partial class UninstallCommandResolver
{
    public static UninstallCommandInfo Resolve(InstalledProgram program, bool preferSilent)
    {
        var quiet = program.QuietUninstallCommand;
        var raw = preferSilent && !string.IsNullOrWhiteSpace(quiet)
            ? quiet!
            : program.UninstallCommand;

        var keyName = program.Location.Path.Split('\\').Last();
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (program.IsWindowsInstaller && IsProductCode(keyName))
            {
                return BuildMsi(keyName, preferSilent, program);
            }

            return new UninstallCommandInfo
            {
                Executable = string.Empty,
                Arguments = string.Empty,
                DisplayCommand = string.Empty,
                IsWindowsInstaller = program.IsWindowsInstaller,
                Error = "This application has no registered uninstall command."
            };
        }

        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (!TrySplitExecutable(expanded, out var executable, out var arguments))
        {
            return Error("The uninstall command has no executable.");
        }

        var fileName = Path.GetFileName(executable);
        if (fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            var productCode = FindProductCode(expanded)
                ?? (IsProductCode(keyName) ? keyName : null);

            if (productCode is null)
            {
                return Error("The MSI uninstall command has no product code.", windowsInstaller: true);
            }

            var msiExecutable = executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar)
                ? executable
                : "msiexec.exe";
            return BuildMsi(productCode, preferSilent, program, msiExecutable);
        }

        executable = ResolveSystemExecutable(executable);
        var warning = File.Exists(executable)
            ? null
            : $"The uninstaller executable was not found at '{executable}'. Forced uninstall may be required.";

        return new UninstallCommandInfo
        {
            Executable = executable,
            Arguments = arguments,
            DisplayCommand = Format(executable, arguments),
            IsWindowsInstaller = program.IsWindowsInstaller,
            Error = warning
        };
    }

    private static UninstallCommandInfo BuildMsi(
        string productCode,
        bool silent,
        InstalledProgram program,
        string executable = "msiexec.exe")
    {
        var arguments = silent
            ? $"/x {productCode} /qn /norestart"
            : $"/x {productCode}";
        return new UninstallCommandInfo
        {
            Executable = executable,
            Arguments = arguments,
            DisplayCommand = Format(executable, arguments),
            IsWindowsInstaller = true,
            ProductCode = productCode
        };
    }

    private static UninstallCommandInfo Error(string message, bool windowsInstaller = false)
        => new()
        {
            Executable = string.Empty,
            Arguments = string.Empty,
            DisplayCommand = string.Empty,
            IsWindowsInstaller = windowsInstaller,
            Error = message
        };

    public static bool IsProductCode(string? value)
        => value is not null && ProductCodePattern().IsMatch(value.Trim());

    private static string? FindProductCode(string commandLine)
    {
        var match = GuidPattern().Match(commandLine);
        return match.Success ? match.Value : null;
    }

    // Many vendors register unquoted paths such as C:\Program Files\App\unins000.exe /SILENT.
    // Take the longest space-separated prefix that is an existing file; unlike CreateProcess's
    // shortest-first probing, this never picks a planted C:\Program.exe.
    private static bool TrySplitUnquotedExecutable(string commandLine, out string executable, out string remainder)
    {
        var parts = commandLine.Split(' ');
        for (var count = parts.Length; count >= 2; count--)
        {
            var candidate = string.Join(' ', parts[..count]).TrimEnd();
            if (File.Exists(candidate))
            {
                executable = candidate;
                remainder = string.Join(' ', parts[count..]);
                return true;
            }
        }

        executable = string.Empty;
        remainder = string.Empty;
        return false;
    }

    /// <summary>
    /// ClickOnce and InstallShield register bare names such as <c>rundll32.exe</c> or <c>RunDll32</c>.
    /// Resolve those only in the Windows system directories, never the working directory or PATH.
    /// </summary>
    private static string ResolveSystemExecutable(string executable)
    {
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
        {
            return executable;
        }

        var name = Path.HasExtension(executable) ? executable : executable + ".exe";
        foreach (var directory in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return executable;
    }

    /// <summary>
    /// Separates the executable from the vendor's argument string. The arguments are kept verbatim:
    /// uninstallers parse their own command line (rundll32 "dll",Entry, NSIS _?=, InstallShield
    /// -f"path"), and re-quoting split tokens would change what they receive.
    /// </summary>
    private static bool TrySplitExecutable(string commandLine, out string executable, out string arguments)
    {
        executable = string.Empty;
        arguments = string.Empty;
        if (commandLine.Length == 0)
        {
            return false;
        }

        if (commandLine[0] == '"')
        {
            var closing = commandLine.IndexOf('"', 1);
            if (closing < 0)
            {
                executable = commandLine[1..].Trim();
            }
            else
            {
                executable = commandLine[1..closing].Trim();
                arguments = commandLine[(closing + 1)..].Trim();
            }

            return executable.Length > 0;
        }

        if (TrySplitUnquotedExecutable(commandLine, out executable, out var remainder))
        {
            arguments = remainder.Trim();
            return true;
        }

        var separator = commandLine.IndexOfAny([' ', '\t']);
        executable = separator < 0 ? commandLine : commandLine[..separator];
        arguments = separator < 0 ? string.Empty : commandLine[(separator + 1)..].Trim();
        return executable.Length > 0;
    }

    private static string Format(string executable, string arguments)
    {
        var builder = new StringBuilder();
        builder.Append(executable.Contains(' ') ? $"\"{executable}\"" : executable);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            builder.Append(' ').Append(arguments);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodePattern();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}
