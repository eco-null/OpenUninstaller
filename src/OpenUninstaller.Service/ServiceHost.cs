using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenUninstaller.Service;

/// <summary>
/// Minimal Service Control Manager host. It avoids a hosted-service dependency so the shipped
/// executable stays small and fully self-contained.
/// </summary>
internal static class ServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;

    private const uint StateStopped = 0x00000001;
    private const uint StateStartPending = 0x00000002;
    private const uint StateStopPending = 0x00000003;
    private const uint StateRunning = 0x00000004;

    private const uint ControlStop = 0x00000001;
    private const uint ControlInterrogate = 0x00000004;
    private const uint ControlShutdown = 0x00000005;

    private const int ErrorFailedServiceControllerConnect = 1063;

    private static IntPtr _statusHandle;
    private static CancellationTokenSource? _cts;
    private static ServiceMainDelegate? _serviceMain;
    private static HandlerExDelegate? _handler;

    public static bool TryRun(string serviceName, Func<CancellationToken, Task> work)
    {
        _serviceMain = (_, _) => ServiceMain(serviceName, work);
        var namePointer = Marshal.StringToHGlobalUni(serviceName);
        var handlerPointer = Marshal.GetFunctionPointerForDelegate(_serviceMain);
        var entrySize = Marshal.SizeOf<ServiceTableEntry>();
        var table = Marshal.AllocHGlobal(entrySize * 2);
        try
        {
            Marshal.StructureToPtr(
                new ServiceTableEntry { Name = namePointer, Procedure = handlerPointer },
                table,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                default(ServiceTableEntry),
                table + entrySize,
                fDeleteOld: false);

            if (StartServiceCtrlDispatcher(table))
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorFailedServiceControllerConnect)
            {
                Console.Error.WriteLine($"start-service-dispatcher failed: {new Win32Exception(error).Message}");
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private static void ServiceMain(string serviceName, Func<CancellationToken, Task> work)
    {
        _handler = HandleControl;
        _statusHandle = RegisterServiceCtrlHandlerEx(serviceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            LogCrash(new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        _cts = new CancellationTokenSource();
        SetStatus(StateStartPending, waitHint: 5_000);

        _ = Task.Run(async () =>
        {
            uint exitCode = 0;
            try
            {
                SetStatus(StateRunning);
                await work(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A normal stop request.
            }
            catch (Exception ex)
            {
                exitCode = 1;
                LogCrash(ex);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetStatus(StateStopped, exitCode);
            }
        });
    }

    private static uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                SetStatus(StateStopPending, waitHint: 10_000);
                _cts?.Cancel();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return 0;
        }
    }

    private static void SetStatus(uint state, uint exitCode = 0, uint waitHint = 0)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == StateRunning
                ? ServiceAcceptStop | ServiceAcceptShutdown
                : 0,
            Win32ExitCode = exitCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = 0,
            WaitHint = waitHint
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OpenUninstaller",
                "Logs");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "service-crash.log"),
                $"{DateTimeOffset.UtcNow:O} {exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing else can be done from a failing service host.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Procedure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(IntPtr serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        HandlerExDelegate handler,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);
}
