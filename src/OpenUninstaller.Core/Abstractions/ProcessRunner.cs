using System.ComponentModel;
using System.Diagnostics;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Abstractions;

/// <summary>
/// Runs external programs. A timeout or cancellation stops waiting but never kills the process:
/// vendor uninstallers may already be committing changes, so termination is the user's decision.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        bool shellExecute,
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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult { Started = false, Error = "Process did not start." };
            }

            var processId = process.Id;

            if (timeout is null)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new ProcessResult { Started = true, ExitCode = process.ExitCode, ProcessId = processId };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout.Value);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return new ProcessResult { Started = true, ExitCode = process.ExitCode, ProcessId = processId };
            }
            catch (OperationCanceledException)
            {
                return new ProcessResult
                {
                    Started = true,
                    TimedOut = !cancellationToken.IsCancellationRequested,
                    StillRunning = !process.HasExited,
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
            process?.Dispose();
        }
    }
}
