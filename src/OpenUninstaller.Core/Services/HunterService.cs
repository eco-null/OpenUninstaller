using System.Runtime.InteropServices;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Maps a window under the cursor to its process and owning installed application.</summary>
public static class HunterService
{
    public static InstalledProgram CreateCleanupTarget(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        return new InstalledProgram
        {
            Id = "custom_target_" + Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? full : name,
            CleanupTargetPath = full,
            DisplayIcon = File.Exists(full) ? full : null,
            Location = new RegistryLocation(Microsoft.Win32.RegistryHive.CurrentUser,
                Microsoft.Win32.RegistryView.Default, @"Software\OpenUninstaller\CustomTargets")
        };
    }

    public static HunterMatch MatchCursor(IReadOnlyList<InstalledProgram> programs)
        => MatchWindow(WindowAtCursor(), programs);

    private static nint WindowAtCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return nint.Zero;
        }

        return WindowFromPoint(point);
    }

    public static HunterMatch MatchWindow(nint window, IReadOnlyList<InstalledProgram> programs)
    {
        if (window == nint.Zero)
        {
            return new HunterMatch { Reason = "No window is under the cursor." };
        }

        var threadId = GetWindowThreadProcessId(window, out var processId);
        _ = threadId;
        var title = GetWindowTitle(window);
        if (processId == 0)
        {
            return new HunterMatch { WindowTitle = title, Reason = "Could not resolve the window's process." };
        }

        var exePath = GetExecutablePath((int)processId);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new HunterMatch
            {
                ProcessId = (int)processId,
                WindowTitle = title,
                ProcessName = null,
                Reason = "The process executable could not be read (it may be protected)."
            };
        }

        return MatchExecutable(exePath, title, (int)processId, programs);
    }

    /// <summary>Pure matching logic, separated so it can be tested without a live window.</summary>
    public static HunterMatch MatchExecutable(
        string exePath,
        string? windowTitle,
        int processId,
        IReadOnlyList<InstalledProgram> programs)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        var normalizedExe = Normalize(exePath) ?? exePath;
        InstalledProgram? best = null;
        var bestScore = 0;
        var bestLocationLength = 0;
        var ambiguous = false;
        var reason = "No installed application matched this window.";

        foreach (var program in programs)
        {
            var score = 0;
            var locationLength = 0;
            string? candidateReason = null;
            var installLocation = Normalize(program.InstallLocation);

            // Only path evidence establishes ownership. Name/publisher fragments can be
            // shared by unrelated apps (for example OpenCode and OpenUninstaller).
            if (IconPath(program.DisplayIcon) is { } icon
                && PathsEqual(icon, normalizedExe))
            {
                score = 120;
                candidateReason = "The executable matches the application's display icon.";
            }
            else if (ExecutableFromCommand(program.UninstallCommand) is { } uninstaller
                     && PathsEqual(uninstaller, normalizedExe))
            {
                score = 110;
                candidateReason = "The executable is the application's uninstaller.";
            }
            else if (installLocation is not null
                     && !PathsEqual(installLocation, Path.GetPathRoot(installLocation)!)
                     && normalizedExe.StartsWith(installLocation.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
                locationLength = installLocation.TrimEnd('\\').Length;
                candidateReason = "The executable is inside the application's install location.";
            }

            if (score > bestScore || (score == bestScore && locationLength > bestLocationLength))
            {
                bestScore = score;
                bestLocationLength = locationLength;
                best = program;
                ambiguous = false;
                reason = candidateReason ?? reason;
            }
            else if (score > 0 && score == bestScore && locationLength == bestLocationLength
                     && best is not null)
            {
                if (!SameOwner(program, best))
                {
                    // Never let inventory order choose which application's uninstall flow opens.
                    ambiguous = true;
                }
                else if (program.Location.View == Microsoft.Win32.RegistryView.Registry64)
                {
                    best = program;
                }
            }
        }

        if (ambiguous)
        {
            best = null;
            bestScore = 0;
            reason = "The executable matches multiple installed applications equally; ownership is ambiguous.";
        }

        return new HunterMatch
        {
            ExePath = normalizedExe,
            ProcessName = processName,
            ProcessId = processId,
            WindowTitle = windowTitle,
            Program = best,
            Score = bestScore,
            Reason = reason
        };
    }

    private static string? GetWindowTitle(nint window)
    {
        var buffer = new char[512];
        var length = GetWindowTextW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private static bool SameOwner(InstalledProgram left, InstalledProgram right)
    {
        if (left.Id == right.Id)
        {
            return true;
        }

        // HKCU uninstall keys can be shared by both registry views. Only collapse
        // identical metadata for the very same user key, not separate registrations.
        return left.Location.Hive == Microsoft.Win32.RegistryHive.CurrentUser
               && right.Location.Hive == left.Location.Hive
               && string.Equals(left.Location.Path, right.Location.Path, StringComparison.OrdinalIgnoreCase)
               && left with { Id = right.Id, Location = right.Location, Architecture = right.Architecture } == right;
    }

    private static string? GetExecutablePath(int processId)
    {
        const int processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return null;
            }

            return new string(buffer, 0, size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string right)
        => Normalize(left) is { } normalized
           && string.Equals(normalized, right, StringComparison.OrdinalIgnoreCase);

    private static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var path = displayIcon.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 2 && int.TryParse(path[(comma + 1)..], out _))
        {
            path = path[..comma];
        }

        var normalized = Normalize(path);
        return File.Exists(normalized) ? normalized : null;
    }

    private static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var tokens = CommandLineSplitter.Split(command);
        if (tokens.Count == 0)
        {
            return null;
        }

        var path = Normalize(tokens[0]);
        return File.Exists(path) ? path : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, [Out] char[] text, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint process, int flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
