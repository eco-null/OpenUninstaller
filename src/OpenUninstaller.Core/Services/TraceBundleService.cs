using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Exchanges install traces between machines without copying machine-specific absolute paths.
/// Known roots become tokens such as <c>%ProgramFiles%</c> and are expanded against the importing
/// machine. The bundle carries a SHA-256 hash of its trace document; a mismatch is rejected.
/// </summary>
public sealed class TraceBundleService
{
    private const long MaxTraceBytes = 32 * 1024 * 1024;
    private const string TraceEntry = "trace.json";
    private const string ManifestEntry = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly TraceStore _store;
    private readonly IClock _clock;

    public TraceBundleService(AppPaths paths, TraceStore store, IClock clock)
    {
        _paths = paths;
        _store = store;
        _clock = clock;
    }

    public TraceBundleExportResult Export(string traceId, string destinationPath)
    {
        var trace = _store.Get(traceId);
        if (trace is null)
        {
            return new TraceBundleExportResult
            {
                Succeeded = false,
                TraceId = traceId,
                Path = destinationPath,
                Error = "The trace was not found."
            };
        }

        try
        {
            var destination = ResolveDestination(destinationPath, trace);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var portable = trace with
            {
                InstallLocation = trace.InstallLocation is null ? null : NormalizePath(trace.InstallLocation),
                Files = trace.Files
                    .Select(file => file with { Path = NormalizePath(file.Path) })
                    .ToList()
            };

            var traceJson = JsonSerializer.Serialize(portable, JsonOptions);
            var manifest = new TraceBundleManifest
            {
                TraceSchemaVersion = trace.SchemaVersion,
                TraceId = trace.Id,
                ProgramName = trace.ProgramName,
                ExportedAt = _clock.UtcNow,
                AppVersion = _paths.AppVersion,
                TraceSha256 = Hashing.Sha256Hex(traceJson),
                RegistryKeys = trace.KeyCount,
                RegistryValues = trace.ValueCount,
                Files = trace.FileCount
            };

            var temp = destination + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, TraceEntry, traceJson);
                WriteEntry(archive, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions));
            }

            File.Move(temp, destination, overwrite: true);
            return new TraceBundleExportResult
            {
                Succeeded = true,
                TraceId = trace.Id,
                Path = destination,
                BundleSha256 = Hashing.Sha256File(destination)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TraceBundleExportResult
            {
                Succeeded = false,
                TraceId = traceId,
                Path = destinationPath,
                Error = ex.Message
            };
        }
    }

    public TraceBundleImportResult Import(string bundlePath)
    {
        if (!File.Exists(bundlePath))
        {
            return Failure("The trace bundle was not found.");
        }

        try
        {
            using var stream = File.OpenRead(bundlePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var traceEntry = FindEntry(archive, TraceEntry);
            var manifestEntry = FindEntry(archive, ManifestEntry);
            if (traceEntry is null || manifestEntry is null)
            {
                return Failure("The bundle is missing trace.json or manifest.json.");
            }

            if (traceEntry.Length > MaxTraceBytes)
            {
                return Failure("The trace document is larger than the supported limit.");
            }

            var traceBytes = ReadEntry(traceEntry);
            var manifestBytes = ReadEntry(manifestEntry);
            var manifest = JsonSerializer.Deserialize<TraceBundleManifest>(manifestBytes, JsonOptions);
            if (manifest is null || manifest.BundleSchemaVersion != 1)
            {
                return Failure("The bundle manifest is missing or uses an unsupported schema.");
            }

            var traceHash = Hashing.Sha256Hex(traceBytes);
            if (!string.Equals(traceHash, manifest.TraceSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("The trace hash does not match the bundle manifest. The file may be damaged.");
            }

            var portable = JsonSerializer.Deserialize<TraceDocument>(traceBytes, JsonOptions);
            if (portable is null || string.IsNullOrWhiteSpace(portable.ProgramName))
            {
                return Failure("The trace document is unreadable.");
            }

            if (!string.Equals(portable.Id, manifest.TraceId, StringComparison.Ordinal)
                || !string.Equals(portable.ProgramName, manifest.ProgramName, StringComparison.Ordinal))
            {
                return Failure("The trace identity does not match the bundle manifest.");
            }

            var warnings = new List<string>();
            var files = new List<TracedFile>();
            foreach (var file in portable.Files)
            {
                if (!TryDenormalizePath(file.Path, out var expanded, out var error))
                {
                    return Failure($"The bundle contains an unsupported path: {file.Path} ({error}).");
                }

                files.Add(file with { Path = expanded });
            }

            string? installLocation = null;
            if (!string.IsNullOrWhiteSpace(portable.InstallLocation))
            {
                if (!TryDenormalizePath(portable.InstallLocation, out var expanded, out var error))
                {
                    return Failure($"The bundle contains an unsupported install location: {portable.InstallLocation} ({error}).");
                }

                installLocation = expanded;
            }

            var now = _clock.UtcNow;
            var trace = portable with
            {
                Id = CreateImportedId(now, portable.ProgramName),
                CreatedAt = portable.CreatedAt,
                InstallLocation = installLocation,
                Files = files
            };

            if (portable.SchemaVersion < 2)
            {
                warnings.Add("This is a schema 1 trace; its registry keys remain protected during review.");
            }

            _store.Save(trace);
            return new TraceBundleImportResult
            {
                Succeeded = true,
                Trace = trace,
                Warnings = warnings
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Failure(ex.Message);
        }
    }

    private static TraceBundleImportResult Failure(string message)
        => new() { Succeeded = false, Error = message };

    private string ResolveDestination(string destinationPath, TraceDocument trace)
    {
        var full = Path.GetFullPath(destinationPath);
        if (Directory.Exists(full) || destinationPath.EndsWith(Path.DirectorySeparatorChar))
        {
            return Path.Combine(full, $"OpenUninstaller-trace-{Hashing.Slug(trace.ProgramName)}.outrace.zip");
        }

        return full;
    }

    private string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var (token, root) in RootTokens())
        {
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }

            if (full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return token + full[trimmed.Length..].Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        return full.Replace(Path.DirectorySeparatorChar, '/');
    }

    private bool TryDenormalizePath(string path, out string expanded, out string? error)
    {
        expanded = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "empty path";
            return false;
        }

        var value = path.Replace('/', Path.DirectorySeparatorChar);
        if (value.StartsWith('%'))
        {
            var end = value.IndexOf('%', 1);
            if (end < 0)
            {
                error = "unterminated path token";
                return false;
            }

            var token = value[..(end + 1)];
            var root = RootTokens()
                .FirstOrDefault(pair => string.Equals(pair.Token, token, StringComparison.OrdinalIgnoreCase))
                .Root;
            if (string.IsNullOrEmpty(root))
            {
                error = "unknown path token";
                return false;
            }

            value = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + value[(end + 1)..];
        }

        if (!Path.IsPathFullyQualified(value))
        {
            error = "path is not absolute";
            return false;
        }

        var full = Path.GetFullPath(value);
        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment == ".."))
        {
            error = "path traversal";
            return false;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal)
            || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.Contains(@"\\.\", StringComparison.Ordinal))
        {
            error = "UNC or device paths are not supported";
            return false;
        }

        expanded = full;
        return true;
    }

    private IEnumerable<(string Token, string Root)> RootTokens()
    {
        yield return ("%ProgramFiles%", _paths.ProgramFiles);
        yield return ("%ProgramFiles(x86)%", _paths.ProgramFilesX86);
        yield return ("%ProgramData%", _paths.ProgramData);
        yield return ("%LOCALAPPDATA%", _paths.LocalAppData);
        yield return ("%APPDATA%", _paths.AppData);
        yield return ("%USERPROFILE%", _paths.UserProfile);
        yield return ("%PUBLIC%", _paths.Public);
        yield return ("%WINDIR%", _paths.WindowsDirectory);
        yield return ("%TEMP%", _paths.Temp);
        yield return ("%SYSTEMDRIVE%", _paths.SystemDrive);
    }

    private string CreateImportedId(DateTimeOffset now, string programName)
    {
        var baseId = $"{now:yyyyMMdd-HHmmss}-imported-{Hashing.Slug(programName)}";
        var id = baseId;
        var suffix = 2;
        while (_store.Get(id) is not null)
        {
            id = $"{baseId}-{suffix++}";
        }

        return id;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
        => archive.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, name, StringComparison.Ordinal));

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
