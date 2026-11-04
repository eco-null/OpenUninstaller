using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Abstractions;

/// <summary>
/// Runs external programs. A timeout or cancellation stops waiting but never kills the process:
/// vendor uninstallers may already be committing changes, so termination is the user's decision.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DescendantPollInterval = TimeSpan.FromMilliseconds(250);

    public Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        bool shellExecute,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
        => RunCoreAsync(fileName, arguments, shellExecute, pending: null, timeout, cancellationToken);

    public Task<ProcessResult> RunWithDescendantsAsync(
        string fileName,
        string arguments,
        Func<bool> pending,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
        => RunCoreAsync(fileName, arguments, shellExecute: true, pending, timeout, cancellationToken);

    private static async Task<ProcessResult> RunCoreAsync(
        string fileName,
        string arguments,
        bool shellExecute,
        Func<bool>? pending,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new ProcessResult { Started = false, Error = "No executable specified." };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = shellExecute,
            CreateNoWindow = !shellExecute,
            WindowStyle = shellExecute ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };

        Process? process = null;
        ProcessTreeJob? job = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult { Started = false, Error = "Process did not start." };
            }

            var processId = process.Id;
            job = pending is null ? null : ProcessTreeJob.TryAttach(process);

            if (timeout is null)
            {
                await WaitAsync(process, job, pending, cancellationToken).ConfigureAwait(false);
                return new ProcessResult { Started = true, ExitCode = process.ExitCode, ProcessId = processId };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout.Value);
            try
            {
                await WaitAsync(process, job, pending, cts.Token).ConfigureAwait(false);
                return new ProcessResult { Started = true, ExitCode = process.ExitCode, ProcessId = processId };
            }
            catch (OperationCanceledException)
            {
                return new ProcessResult
                {
                    Started = true,
                    TimedOut = !cancellationToken.IsCancellationRequested,
                    StillRunning = !process.HasExited || job?.HasActiveProcesses == true,
                    ProcessId = processId
                };
            }
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult { Started = false, Error = ex.Message };
        }
        catch (InvalidOperationException ex)
        {
            return new ProcessResult { Started = false, Error = ex.Message };
        }
        catch (FileNotFoundException ex)
        {
            return new ProcessResult { Started = false, Error = ex.Message };
        }
        finally
        {
            job?.Dispose();
            process?.Dispose();
        }
    }

    private static async Task WaitAsync(Process process, ProcessTreeJob? job, Func<bool>? pending, CancellationToken token)
    {
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (job is null || pending is null)
        {
            return;
        }

        // A lingering child that is not doing the work (a feedback page in a browser) must not
        // block the result once the caller's own completion signal is satisfied.
        while (job.HasActiveProcesses && pending())
        {
            await Task.Delay(DescendantPollInterval, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Groups a launched process with every process it starts. The job sets no limits and is not
    /// kill-on-close: closing it never terminates anything. When the process cannot be assigned
    /// (it already exited, or it is elevated beyond this process), only the parent is awaited.
    /// </summary>
    private sealed class ProcessTreeJob : IDisposable
    {
        private const int JobObjectBasicAccountingInformation = 1;
        private IntPtr _handle;

        private ProcessTreeJob(IntPtr handle) => _handle = handle;

        public bool HasActiveProcesses =>
            _handle != IntPtr.Zero
            && QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out var info,
                (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero)
            && info.ActiveProcesses > 0;

        public static ProcessTreeJob? TryAttach(Process process)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (AssignProcessToJobObject(handle, process.Handle))
                {
                    return new ProcessTreeJob(handle);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            CloseHandle(handle);
            return null;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
            out BasicAccountingInformation info, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
