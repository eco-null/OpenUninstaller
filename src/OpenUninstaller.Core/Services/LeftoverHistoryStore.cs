using System.Text.Json;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Keeps the most recent leftover scan per application so the review screen and CLI can explain
/// what changed. The snapshot contains evidence ids only; it is not a deletion queue.
/// </summary>
public sealed class LeftoverHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;

    public LeftoverHistoryStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryPath => Path.Combine(_paths.DataDirectory, "ScanHistory");

    /// <summary>Last write failure, so the caller can warn without failing the scan.</summary>
    public string? LastError { get; private set; }

    public LeftoverScanSnapshot? Get(InstalledProgram program) => GetSnapshot(program.Name);

    public LeftoverScanDiff CompareAndSave(LeftoverScanResult scan, DateTimeOffset now)
    {
        LastError = null;
        var previous = GetSnapshot(scan.ProgramName);

        var current = new LeftoverScanSnapshot
        {
            ProgramId = scan.ProgramId,
            ProgramName = scan.ProgramName,
            ScannedAt = now,
            UsedTrace = scan.UsedTrace,
            TraceId = scan.TraceId,
            Items = scan.Items.Select(LeftoverSnapshotItem.FromItem).ToList()
        };

        try
        {
            Save(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }

        if (previous is null)
        {
            return new LeftoverScanDiff { Previous = null };
        }

        var before = previous.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var after = current.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        return new LeftoverScanDiff
        {
            Previous = previous,
            NewItems = current.Items.Where(i => !before.ContainsKey(i.Id)).ToList(),
            ResolvedItems = previous.Items.Where(i => !after.ContainsKey(i.Id)).ToList(),
            PersistentItems = current.Items.Where(i => before.ContainsKey(i.Id)).ToList()
        };
    }

    /// <summary>Removes every stored scan snapshot. Returns the number of files deleted.</summary>
    public int Clear()
    {
        LastError = null;
        if (!Directory.Exists(DirectoryPath))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                File.Delete(file);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }

        return removed;
    }

    public bool Delete(InstalledProgram program)
    {
        try
        {
            var path = SnapshotPath(program.Name);
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private LeftoverScanSnapshot? GetSnapshot(string programName)
    {
        var path = SnapshotPath(programName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LeftoverScanSnapshot>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(LeftoverScanSnapshot snapshot)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = SnapshotPath(snapshot.ProgramName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private string SnapshotPath(string programName)
        => Path.Combine(DirectoryPath, Hashing.Slug(programName) + ".json");
}
