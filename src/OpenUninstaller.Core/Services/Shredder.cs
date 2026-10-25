using System.Diagnostics;
using System.Security.Cryptography;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Best-effort irreversible deletion: every file is overwritten with random bytes for the requested
/// number of passes, flushed to disk, truncated, renamed and removed. Filesystem journals, SSD
/// wear-levelling and backups may still retain copies, so the result is described as best effort.
/// Every target passes <see cref="PathGuard"/> first; reparse points and protected roots are refused.
/// </summary>
public sealed class Shredder
{
    private const int BufferSize = 1 << 20;

    private readonly PathGuard _guard;
    private readonly IOperationLog _log;

    public Shredder(PathGuard guard, IOperationLog log)
    {
        _guard = guard;
        _log = log;
    }

    public ShredPlan Plan(IEnumerable<string> paths)
    {
        var plan = new ShredPlan();
        var accepted = new List<(string FullPath, bool IsDirectory)>();

        foreach (var raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existsAsFile = File.Exists(raw);
            var existsAsDirectory = Directory.Exists(raw);
            if (!existsAsFile && !existsAsDirectory)
            {
                plan.Rejected.Add($"{raw}: the path does not exist.");
                continue;
            }

            var guard = _guard.ValidateForDeletion(raw, recursive: existsAsDirectory);
            if (!guard.Allowed || guard.NormalizedPath is null)
            {
                plan.Rejected.Add($"{raw}: {guard.Reason}");
                continue;
            }

            accepted.Add((guard.NormalizedPath, existsAsDirectory));
        }

        // A selected folder already covers everything beneath it; shredding a child twice would
        // only produce noise in the report.
        var kept = new List<string>();
        foreach (var candidate in accepted.OrderBy(a => a.FullPath.Length))
        {
            if (kept.Any(parent => PathGuard.IsUnder(candidate.FullPath, parent)))
            {
                continue;
            }

            kept.Add(candidate.FullPath);
            plan.Targets.Add(Measure(candidate.FullPath, candidate.IsDirectory, plan.Rejected));
        }

        return plan;
    }

    public ShredResult Shred(IEnumerable<string> paths, int passes, bool dryRun, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = Plan(paths);
        var result = new ShredResult
        {
            DryRun = dryRun,
            Passes = Math.Clamp(passes, 1, 7)
        };
        result.Rejected.AddRange(plan.Rejected);

        if (dryRun)
        {
            result.ShreddedFiles = plan.TotalFiles;
            result.ShreddedBytes = plan.TotalBytes;
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        var buffer = new byte[BufferSize];
        foreach (var target in plan.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.IsDirectory)
            {
                ShredFile(target.Path, result.Passes, buffer, result, cancellationToken);
                continue;
            }

            ShredDirectory(target.Path, result.Passes, buffer, result, cancellationToken);
        }

        result.Duration = stopwatch.Elapsed;
        _log.Write(
            "shred",
            "shred",
            string.Join("; ", plan.Targets.Select(t => t.Path)),
            result.Succeeded ? "completed" : "partial",
            $"{result.ShreddedFiles} file(s), {result.ShreddedBytes} byte(s), {result.Passes} pass(es)");
        return result;
    }

    private void ShredDirectory(
        string root,
        int passes,
        byte[] buffer,
        ShredResult result,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        List<string> files;
        List<string> directories;
        try
        {
            files = Directory.EnumerateFiles(root, "*", options).ToList();
            directories = Directory.EnumerateDirectories(root, "*", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failed.Add($"{root}: {ex.Message}");
            return;
        }

        foreach (var file in files.OrderByDescending(f => f.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShredFile(file, passes, buffer, result, cancellationToken);
        }

        foreach (var directory in directories
                     .OrderByDescending(d => d.Length)
                     .Append(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    continue;
                }

                Directory.Delete(directory, recursive: false);
                result.DeletedDirectories++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                {
                    result.Failed.Add($"{directory}: {ex.Message}");
                }
            }
        }
    }

    private bool ShredFile(
        string path,
        int passes,
        byte[] buffer,
        ShredResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long length;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.Skipped.Add($"{path}: reparse points are never shredded.");
                return false;
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failed.Add($"{path}: {ex.Message}");
            return false;
        }

        try
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None,
                       BufferSize,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                for (var pass = 0; pass < passes; pass++)
                {
                    stream.Position = 0;
                    var remaining = length;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var chunk = (int)Math.Min(buffer.Length, remaining);
                        RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
                        stream.Write(buffer, 0, chunk);
                        remaining -= chunk;
                    }

                    stream.Flush(flushToDisk: true);
                }

                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
            }

            var directory = Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? string.Empty;
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".ou-shred");
            File.Move(path, temporary);
            File.Delete(temporary);

            result.ShreddedFiles++;
            result.ShreddedBytes += length;
            _log.Write("shred", "overwrite", path, "completed", $"{passes} pass(es)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Failed.Add($"{path}: {ex.Message}");
            _log.Write("shred", "overwrite", path, "failed", ex.Message);
            return false;
        }
    }

    private static ShredTarget Measure(string path, bool isDirectory, List<string> rejected)
    {
        if (!isDirectory)
        {
            try
            {
                return new ShredTarget
                {
                    Path = path,
                    IsDirectory = false,
                    FileCount = 1,
                    Bytes = new FileInfo(path).Length
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejected.Add($"{path}: {ex.Message}");
                return new ShredTarget { Path = path, IsDirectory = false };
            }
        }

        try
        {
            var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToList();
            long bytes = 0;
            foreach (var file in files)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A size is cosmetic; the shred itself reports per-file failures.
                }
            }

            return new ShredTarget
            {
                Path = path,
                IsDirectory = true,
                FileCount = files.Count,
                Bytes = bytes
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rejected.Add($"{path}: {ex.Message}");
            return new ShredTarget { Path = path, IsDirectory = true };
        }
    }
}
