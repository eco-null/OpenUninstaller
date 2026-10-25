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
        var tokens = CommandLineSplitter.Split(expanded);
        if (tokens.Count == 0)
        {
            return Error("The uninstall command is empty.");
        }

        var executable = tokens[0].Trim();
        if (executable.Length == 0)
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

        var arguments = JoinArguments(tokens.Skip(1));
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

    private static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (argument.Any(char.IsWhiteSpace) && !(argument.StartsWith('"') && argument.EndsWith('"')))
        {
            return $"\"{argument}\"";
        }

        return argument;
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
