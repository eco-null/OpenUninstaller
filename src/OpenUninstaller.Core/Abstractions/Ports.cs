using System.Diagnostics;
using Microsoft.Win32;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Abstractions;

public interface IRegistryStore
{
    bool KeyExists(RegistryLocation location);
    RegistryValue? GetValue(RegistryLocation location, string name);
    IReadOnlyList<string> GetValueNames(RegistryLocation location);
    IReadOnlyList<string> GetSubKeyNames(RegistryLocation location);
    void CreateSubKey(RegistryLocation location);
    void SetValue(RegistryLocation location, string name, RegistryValueKind kind, object data);
    void DeleteValue(RegistryLocation location, string name);
    void DeleteKey(RegistryLocation location, bool recursive);
}

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    long GetFileSize(string path);
    FileAttributes GetAttributes(string path);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
    void CopyFile(string source, string destination, bool overwrite);
    void MoveFile(string source, string destination);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    IEnumerable<string> EnumerateFiles(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
}

public sealed record ProcessResult
{
    public bool Started { get; init; }
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool StillRunning { get; init; }
    public int? ProcessId { get; init; }
    public string? Error { get; init; }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        bool shellExecute,
        TimeSpan? timeout,
        CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IOperationLog
{
    string? FilePath { get; }
    void Write(string operation, string action, string target, string outcome, string? detail = null);
    IReadOnlyList<string> Snapshot();
}

public interface IElevationService
{
    bool IsElevated { get; }
    bool TryRelaunchElevated(string arguments = "");
}

public static class ProcessRunnerExtensions
{
    /// <summary>Runs a process without a timeout or shell execution.</summary>
    public static Task<ProcessResult> RunAsync(
        this IProcessRunner runner,
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(fileName, arguments, shellExecute: false, timeout: null, cancellationToken);
}

