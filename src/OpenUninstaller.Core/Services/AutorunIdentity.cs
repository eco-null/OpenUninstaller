using System.Diagnostics;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Presentation classification, not a trust or deletion decision. Unknown owners stay visible.</summary>
public static class AutorunIdentity
{
    private static readonly string[] WindowsProgramFolders =
    ["Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Media Player", "Windows NT", "Windows Mail", "Internet Explorer"];
    public static string? ExecutablePath(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded[12..]);
        var tokens = CommandLineSplitter.Split(expanded);
        if (tokens.Count == 0) return null;
        var path = File.Exists(expanded.Trim('"')) ? expanded.Trim('"') : tokens[0];
        // Service ImagePath values commonly omit quotes around a path containing spaces.
        if (!expanded.StartsWith('"'))
        {
            for (var start = 0; start < expanded.Length;)
            {
                var end = expanded.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
                if (end < 0) break;
                if (end + 4 == expanded.Length || char.IsWhiteSpace(expanded[end + 4]))
                { path = expanded[..(end + 4)]; break; }
                start = end + 4;
            }
        }
        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            path = Path.GetFullPath(path);
            return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ShortcutResolver.TryResolve(path) ?? path : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? Publisher(string? path)
    {
        try { return path is not null && File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static bool IsWindowsComponent(AutorunEntry entry, AppPaths paths)
    {
        if (entry.Source == AutorunSourceKind.PackagedStartupTask) return false;
        if (entry.Source is AutorunSourceKind.ScheduledTask or AutorunSourceKind.DisabledScheduledTask)
            return entry.TaskName?.TrimStart('\\').StartsWith(@"Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) == true;

        var executable = entry.ExecutablePath ?? ExecutablePath(entry.Command);
        if (executable is null) return false;
        var windowsLocation = IsInside(executable, paths.WindowsDirectory)
            || IsInside(executable, Path.Combine(paths.ProgramData, "Microsoft", "Windows Defender"))
            || new[] { paths.ProgramFiles, paths.ProgramFilesX86 }.Any(root =>
                WindowsProgramFolders.Any(component => IsInside(executable, Path.Combine(root, component))));
        if (!windowsLocation) return false;
        // A Windows script host can start a user script. Its publisher does not own the script.
        if (entry.Source is not (AutorunSourceKind.MachineService or AutorunSourceKind.DisabledService)
            && Path.GetFileName(executable).ToLowerInvariant() is "powershell.exe" or "pwsh.exe" or "wscript.exe"
                or "cscript.exe" or "cmd.exe" or "rundll32.exe") return false;
        return entry.Publisher?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsInside(string path, string directory)
    {
        try { return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}
