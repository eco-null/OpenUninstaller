using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Evidence Remover (Free Space Wiper): overwrites unallocated clusters on storage volumes so
/// previously deleted files cannot be recovered. Allocates temporary wipe files, streams zero
/// or pseudorandom bytes until the drive has zero remaining unallocated space, flushes the data,
/// and deletes the temporary files.
/// </summary>
public sealed class EvidenceRemoverService
{
    private const int ChunkSize = 4 * 1024 * 1024; // 4 MB chunk
    private const long MaxFileSize = 2L * 1024 * 1024 * 1024; // 2 GB per file
    private const string WipeDirectoryName = "$OpenUninstallerWipe";

    private readonly IOperationLog _log;

    public EvidenceRemoverService(IOperationLog log)
    {
        _log = log;
    }

    public IReadOnlyList<DriveFreeSpaceInfo> GetAvailableDrives() => GetDrives();

    /// <summary>
    /// Returns available fixed and removable drives with free space information.
    /// </summary>
    public IReadOnlyList<DriveFreeSpaceInfo> GetDrives()
    {
        var result = new List<DriveFreeSpaceInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable))
                {
                    continue;
                }

                result.Add(new DriveFreeSpaceInfo
                {
                    DriveName = drive.RootDirectory.FullName,
                    VolumeLabel = drive.VolumeLabel,
                    DriveType = drive.DriveType,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace
                });
            }
            catch (Exception)
            {
            }
        }

        return result.OrderBy(d => d.DriveName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Wipes the unallocated free space on the specified drive.
    /// </summary>
    public Task<WipeResult> WipeDriveAsync(
        string driveName,
        IProgress<WipeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => WipeDriveCore(driveName, progress, cancellationToken), cancellationToken);
    }

    private WipeResult WipeDriveCore(
        string driveName,
        IProgress<WipeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var root = Path.GetPathRoot(driveName);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new WipeResult
            {
                DriveName = driveName,
                TotalBytesWiped = 0,
                Succeeded = false,
                ErrorMessage = $"Drive '{driveName}' is not accessible.",
                Duration = TimeSpan.Zero
            };
        }

        DriveInfo driveInfo;
        try
        {
            driveInfo = new DriveInfo(root);
            if (!driveInfo.IsReady)
            {
                return new WipeResult
                {
                    DriveName = driveName,
                    TotalBytesWiped = 0,
                    Succeeded = false,
                    ErrorMessage = $"Drive '{driveName}' is not ready.",
                    Duration = TimeSpan.Zero
                };
            }
        }
        catch (Exception ex)
        {
            return new WipeResult
            {
                DriveName = driveName,
                TotalBytesWiped = 0,
                Succeeded = false,
                ErrorMessage = ex.Message,
                Duration = TimeSpan.Zero
            };
        }

        var initialFree = driveInfo.AvailableFreeSpace;
        var wipeDir = Path.Combine(root, WipeDirectoryName);
        var createdFiles = new List<string>();
        long totalWiped = 0;

        _log.Write("evidence-remover", "wipe-start", driveName, "started", $"Target free space: {InstalledProgram.FormatBytes(initialFree)}");

        try
        {
            Directory.CreateDirectory(wipeDir);
            try
            {
                File.SetAttributes(wipeDir, FileAttributes.Directory | FileAttributes.Hidden);
            }
            catch
            {
            }

            var buffer = new byte[ChunkSize];
            Array.Clear(buffer, 0, buffer.Length);

            var fileIndex = 0;
            var isDriveFull = false;

            while (!isDriveFull)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentFilePath = Path.Combine(wipeDir, $"wipe_{fileIndex:D4}.tmp");
                createdFiles.Add(currentFilePath);
                fileIndex++;

                long currentFileBytes = 0;

                try
                {
                    using var stream = new FileStream(
                        currentFilePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: ChunkSize,
                        FileOptions.WriteThrough);

                    while (currentFileBytes < MaxFileSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var remainingDriveFree = driveInfo.AvailableFreeSpace;
                        if (remainingDriveFree <= 0)
                        {
                            isDriveFull = true;
                            break;
                        }

                        var toWrite = (int)Math.Min(ChunkSize, remainingDriveFree);
                        if (toWrite <= 0)
                        {
                            isDriveFull = true;
                            break;
                        }

                        stream.Write(buffer, 0, toWrite);
                        currentFileBytes += toWrite;
                        totalWiped += toWrite;

                        ReportProgress(progress, root, initialFree, totalWiped, stopwatch.Elapsed);
                    }

                    stream.Flush(flushToDisk: true);
                }
                catch (IOException ex) when (IsDiskFullException(ex))
                {
                    isDriveFull = true;
                }
            }

            _log.Write("evidence-remover", "wipe-complete", driveName, "success", $"Wiped: {InstalledProgram.FormatBytes(totalWiped)} in {stopwatch.Elapsed.TotalSeconds:F1}s");

            return new WipeResult
            {
                DriveName = root,
                TotalBytesWiped = totalWiped,
                Succeeded = true,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            _log.Write("evidence-remover", "wipe-canceled", driveName, "canceled", $"Canceled after wiping {InstalledProgram.FormatBytes(totalWiped)}");
            throw;
        }
        catch (Exception ex)
        {
            _log.Write("evidence-remover", "wipe-failed", driveName, "failed", ex.Message);
            return new WipeResult
            {
                DriveName = root,
                TotalBytesWiped = totalWiped,
                Succeeded = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
        finally
        {
            foreach (var file in createdFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (Directory.Exists(wipeDir))
                {
                    Directory.Delete(wipeDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void ReportProgress(
        IProgress<WipeProgress>? progress,
        string driveName,
        long totalBytesToWipe,
        long bytesWiped,
        TimeSpan elapsed)
    {
        if (progress is null || totalBytesToWipe <= 0)
        {
            return;
        }

        var percent = Math.Min(100.0, (double)bytesWiped / totalBytesToWipe * 100.0);
        TimeSpan? eta = null;

        if (percent > 1.0 && elapsed.TotalSeconds > 1.0)
        {
            var bytesPerSec = bytesWiped / elapsed.TotalSeconds;
            if (bytesPerSec > 0)
            {
                var remainingBytes = Math.Max(0, totalBytesToWipe - bytesWiped);
                eta = TimeSpan.FromSeconds(remainingBytes / bytesPerSec);
            }
        }

        progress.Report(new WipeProgress
        {
            DriveName = driveName,
            TotalBytesToWipe = totalBytesToWipe,
            BytesWiped = bytesWiped,
            PercentComplete = percent,
            EstimatedTimeRemaining = eta,
            StageMessage = $"Erasing free space on {driveName}... {InstalledProgram.FormatBytes(bytesWiped)} of {InstalledProgram.FormatBytes(totalBytesToWipe)} ({percent:F1}%)"
        });
    }

    private static bool IsDiskFullException(IOException ex)
    {
        const int ERROR_DISK_FULL = 112;
        const int ERROR_HANDLE_DISK_FULL = 39;
        var hr = ex.HResult & 0xFFFF;
        return hr == ERROR_DISK_FULL || hr == ERROR_HANDLE_DISK_FULL;
    }
}
