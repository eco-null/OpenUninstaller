using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Abstractions;

/// <summary>Append-only JSONL log. Logging failures never fail the operation.</summary>
public sealed class FileOperationLog : IOperationLog
{
    private readonly object _gate = new();
    private readonly List<string> _snapshot = [];
    private bool _disabled;

    public FileOperationLog(string directory, string operationName)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var safeName = Hashing.Slug(operationName);
            FilePath = Path.Combine(directory, $"op-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeName}.jsonl");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _disabled = true;
        }
    }

    public string? FilePath { get; }

    public static IOperationLog Null { get; } = new NullOperationLog();

    public void Write(string operation, string action, string target, string outcome, string? detail = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            operation,
            action,
            target,
            outcome,
            detail
        });

        lock (_gate)
        {
            _snapshot.Add(line);
            if (_disabled || FilePath is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _snapshot.ToArray();
        }
    }

    private sealed class NullOperationLog : IOperationLog
    {
        public string? FilePath => null;
        public void Write(string operation, string action, string target, string outcome, string? detail = null) { }
        public IReadOnlyList<string> Snapshot() => [];
    }
}

public sealed class WindowsElevationService : IElevationService
{
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public bool TryRelaunchElevated(string arguments = "")
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the UAC prompt.
            return false;
        }
    }
}
