using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Provides live filesystem and process monitoring bounded strictly to the active session.
/// Automatically starts when Hunter Mode opens and terminates completely upon closing.
/// </summary>
public sealed class LiveActivityTracker : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentBag<LiveTrackedFileEvent> _fileEvents = [];
    private readonly ConcurrentBag<LiveTrackedProcess> _spawnedProcesses = [];
    private readonly HashSet<int> _initialProcessIds = [];
    private Timer? _processPollTimer;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public event Action<LiveTrackedFileEvent>? FileActivityDetected;
    public event Action<LiveTrackedProcess>? ProcessActivityDetected;

    public IReadOnlyList<LiveTrackedFileEvent> FileEvents => _fileEvents.OrderByDescending(e => e.Timestamp).ToList();
    public IReadOnlyList<LiveTrackedProcess> SpawnedProcesses => _spawnedProcesses.OrderByDescending(p => p.StartTime).ToList();

    public void Start()
    {
        if (IsRunning || _disposed)
        {
            return;
        }

        SnapshotInitialProcesses();
        InitializeWatchers();

        _processPollTimer = new Timer(PollProcesses, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5));
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _processPollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _processPollTimer?.Dispose();
        _processPollTimer = null;

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception)
            {
            }
        }
        _watchers.Clear();

        IsRunning = false;
    }

    public void Clear()
    {
        _fileEvents.Clear();
        _spawnedProcesses.Clear();
        SnapshotInitialProcesses();
    }

    private void SnapshotInitialProcesses()
    {
        _initialProcessIds.Clear();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                _initialProcessIds.Add(proc.Id);
            }
        }
        catch (Exception)
        {
        }
    }

    private void InitializeWatchers()
    {
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddPath(targetPaths, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));

        foreach (var dir in targetPaths)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };

                watcher.Created += (_, e) => RecordFileChange(e.FullPath, null, LiveChangeKind.Created);
                watcher.Changed += (_, e) => RecordFileChange(e.FullPath, null, LiveChangeKind.Changed);
                watcher.Deleted += (_, e) => RecordFileChange(e.FullPath, null, LiveChangeKind.Deleted);
                watcher.Renamed += (_, e) => RecordFileChange(e.FullPath, e.OldFullPath, LiveChangeKind.Renamed);

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void AddPath(HashSet<string> set, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            set.Add(path);
        }
    }

    private void RecordFileChange(string fullPath, string? oldPath, LiveChangeKind kind)
    {
        if (ShouldFilterPath(fullPath))
        {
            return;
        }

        long size = 0;
        try
        {
            if (File.Exists(fullPath))
            {
                size = new FileInfo(fullPath).Length;
            }
        }
        catch (Exception)
        {
        }

        var entry = new LiveTrackedFileEvent
        {
            Path = fullPath,
            OldPath = oldPath,
            Kind = kind,
            Timestamp = DateTime.UtcNow,
            Size = size
        };

        _fileEvents.Add(entry);
        FileActivityDetected?.Invoke(entry);
    }

    private static bool ShouldFilterPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        // Filter noisy transient Windows files
        var name = System.IO.Path.GetFileName(path);
        if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void PollProcesses(object? state)
    {
        if (!IsRunning || _disposed)
        {
            return;
        }

        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if (_initialProcessIds.Add(proc.Id))
                    {
                        string? exePath = null;
                        try
                        {
                            exePath = proc.MainModule?.FileName;
                        }
                        catch (Exception)
                        {
                        }

                        DateTime startTime = DateTime.UtcNow;
                        try
                        {
                            startTime = proc.StartTime.ToUniversalTime();
                        }
                        catch (Exception)
                        {
                        }

                        var item = new LiveTrackedProcess
                        {
                            ProcessId = proc.Id,
                            ProcessName = proc.ProcessName,
                            ExecutablePath = exePath,
                            StartTime = startTime
                        };

                        _spawnedProcesses.Add(item);
                        ProcessActivityDetected?.Invoke(item);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
