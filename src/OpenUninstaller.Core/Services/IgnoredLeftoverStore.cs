using System.Text.Json;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Persists ignored leftover ids under the application data directory. The file is deliberately
/// small, human-readable and scoped per application; a corrupt file is treated as empty so a scan
/// is never blocked by state.
/// </summary>
public sealed class IgnoredLeftoverStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public IgnoredLeftoverStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string FilePath => Path.Combine(_paths.DataDirectory, "ignored-leftovers.json");

    /// <summary>Last write failure, so callers can report it instead of silently losing state.</summary>
    public string? LastError { get; private set; }

    public IReadOnlySet<string> GetIgnoredItemIds(InstalledProgram program)
    {
        var nameKey = NameNormalizer.Compact(program.Name);
        var entries = Load();
        return entries
            .Where(e => string.Equals(e.ProgramId, program.Id, StringComparison.OrdinalIgnoreCase)
                        || NameNormalizer.Compact(e.ProgramName) == nameKey)
            .Select(e => e.ItemId)
            .ToHashSet(StringComparer.Ordinal);
    }

    public int CountFor(InstalledProgram program)
        => GetIgnoredItemIds(program).Count;

    public int Add(string programId, string programName, IEnumerable<LeftoverItem> items, DateTimeOffset now)
    {
        LastError = null;
        var additions = items
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (additions.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var entries = Load().ToList();
            var added = 0;
            foreach (var item in additions)
            {
                var exists = entries.Any(e =>
                    string.Equals(e.ProgramId, programId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.ItemId, item.Id, StringComparison.Ordinal));
                if (exists)
                {
                    continue;
                }

                entries.Add(new IgnoredLeftoverEntry
                {
                    ProgramId = programId,
                    ProgramName = programName,
                    ItemId = item.Id,
                    DisplayPath = item.DisplayPath,
                    IgnoredAt = now
                });
                added++;
            }

            if (added > 0)
            {
                try
                {
                    Save(entries);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LastError = ex.Message;
                    return 0;
                }
            }

            return added;
        }
    }

    /// <summary>Removes every ignored leftover entry. Returns the number of entries removed.</summary>
    public int ClearAll()
    {
        LastError = null;
        lock (_gate)
        {
            var entries = Load();
            if (entries.Count == 0)
            {
                return 0;
            }

            try
            {
                Save([]);
                return entries.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex.Message;
                return 0;
            }
        }
    }

    public int Clear(string programId, string programName)
    {
        LastError = null;
        var nameKey = NameNormalizer.Compact(programName);
        lock (_gate)
        {
            var entries = Load().ToList();
            var removed = entries.RemoveAll(e =>
                string.Equals(e.ProgramId, programId, StringComparison.OrdinalIgnoreCase)
                || NameNormalizer.Compact(e.ProgramName) == nameKey);
            if (removed > 0)
            {
                try
                {
                    Save(entries);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LastError = ex.Message;
                    return 0;
                }
            }

            return removed;
        }
    }

    private List<IgnoredLeftoverEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<IgnoredLeftoverDocument>(File.ReadAllText(FilePath), JsonOptions);
            return document?.Entries ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(IReadOnlyList<IgnoredLeftoverEntry> entries)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var document = new IgnoredLeftoverDocument { Entries = [.. entries] };
        var text = JsonSerializer.Serialize(document, JsonOptions);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, FilePath, overwrite: true);
    }
}
