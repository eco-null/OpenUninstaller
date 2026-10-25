using System.Runtime.InteropServices;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Maps a window under the cursor to its process and owning installed application.</summary>
public static class HunterService
{
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
        var reason = "No installed application matched this window.";

        foreach (var program in programs)
        {
            var score = 0;
            string? candidateReason = null;
            var installLocation = Normalize(program.InstallLocation);

            if (installLocation is not null
                && normalizedExe.StartsWith(installLocation.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
                candidateReason = "The executable is inside the application's install location.";
            }
            else if (IconPath(program.DisplayIcon) is { } icon
                     && PathsEqual(icon, normalizedExe))
            {
                score = 95;
                candidateReason = "The executable matches the application's display icon.";
            }
            else if (ExecutableFromCommand(program.UninstallCommand) is { } uninstaller
                     && PathsEqual(uninstaller, normalizedExe))
            {
                score = 90;
                candidateReason = "The executable is the application's uninstaller.";
            }
            else if (NameNormalizer.CountTokenMatches(processName, NameNormalizer.MatchTokens(program.Name, program.Publisher)) >= 2)
            {
                score = 70;
                candidateReason = "The process name matches the application name.";
            }
            else if (NameNormalizer.Tokens(program.Publisher).Any(token => processName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score = 30;
                candidateReason = "The process name matches the publisher.";
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = program;
                reason = candidateReason ?? reason;
            }
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
            return Path.GetFullPath(path.Trim().Trim('"'));
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

        path = path.Trim().Trim('"');
        return File.Exists(path) ? path : null;
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

        var path = Environment.ExpandEnvironmentVariables(tokens[0]);
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
