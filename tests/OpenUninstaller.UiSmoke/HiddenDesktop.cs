using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenUninstaller.UiSmoke;

/// <summary>
/// Re-runs the smoke runner on a private Windows desktop that is never displayed, so the app under test
/// cannot take focus, cover other windows, open popups over them or receive the user's input. UI Automation
/// only sees the desktop of the calling thread, so the runner and the app it launches both live there.
/// The child tree runs at below-normal priority inside a kill-on-close job: stopping the runner never leaves
/// an invisible app behind. Pass <c>--visible</c> to watch a run on the normal desktop.
/// </summary>
internal static class HiddenDesktop
{
    private const string MarkerVariable = "OPENUNINSTALLER_UISMOKE_HIDDEN_DESKTOP";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    public static bool IsCurrent => Environment.GetEnvironmentVariable(MarkerVariable) == "1";

    public static int Run(string[] args)
    {
        var name = $"OpenUninstallerUiSmoke-{Environment.ProcessId}";
        var desktop = CreateDesktopW(name, null, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the hidden test desktop.");
        var job = IntPtr.Zero;
        var process = new ProcessInformation();
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the test job object.");
            var limits = new ExtendedLimitInformation { Basic = { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot configure the test job object.");

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                // No "WinSta0\" prefix: the desktop belongs to this process's window station, which is not
                // WinSta0 when the runner itself is a service (some CI agents).
                lpDesktop = name,
                dwFlags = UseStdHandles,
                hStdInput = InheritableStdHandle(-10),
                hStdOutput = InheritableStdHandle(-11),
                hStdError = InheritableStdHandle(-12)
            };
            Environment.SetEnvironmentVariable(MarkerVariable, "1");
            var commandLine = new StringBuilder(CommandLine(args));
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | BelowNormalPriority,
                    IntPtr.Zero, null, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the smoke runner on the hidden desktop.");
            if (!AssignProcessToJobObject(job, process.hProcess))
            {
                TerminateProcess(process.hProcess, 1);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot place the smoke runner in its job object.");
            }
            ResumeThread(process.hThread);

            if (WaitForSingleObject(process.hProcess, (uint)RunTimeout.TotalMilliseconds) != 0)
            {
                Console.Error.WriteLine($"UI smoke did not finish within {RunTimeout.TotalMinutes:0} minutes on the hidden desktop; " +
                    "rerun with --visible to see where it stopped (for example a modal dialog).");
                return 1;
            }
            return GetExitCodeProcess(process.hProcess, out var exitCode) ? (int)exitCode : 1;
        }
        finally
        {
            if (process.hThread != IntPtr.Zero) CloseHandle(process.hThread);
            if (process.hProcess != IntPtr.Zero) CloseHandle(process.hProcess);
            // Closing the last job handle terminates anything still running from the run, including the app.
            if (job != IntPtr.Zero) CloseHandle(job);
            CloseDesktop(desktop);
        }
    }

    private static IntPtr InheritableStdHandle(int id)
    {
        var handle = GetStdHandle(id);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
        return handle;
    }

    private static string CommandLine(IEnumerable<string> args)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the smoke runner executable.");
        var parts = new List<string> { host };
        // `dotnet OpenUninstaller.UiSmoke.dll` runs through the shared host; the apphost runs directly.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            parts.Add(typeof(HiddenDesktop).Assembly.Location);
        parts.AddRange(args);
        return string.Join(' ', parts.Select(Quote));
    }

    // Windows argv quoting: backslashes are literal unless they precede a quote or the closing quote.
    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private const uint DesktopAllAccess = 0x01FF | 0x000F0000;
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformationClass = 9;
    private const int UseStdHandles = 0x100;
    private const uint HandleFlagInherit = 1;
    private const uint CreateSuspended = 0x4;
    private const uint BelowNormalPriority = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string name, string? device, IntPtr devmode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
