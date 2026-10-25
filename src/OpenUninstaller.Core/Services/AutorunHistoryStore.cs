using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Persists the history of state changes (disables, enables, rollbacks) made to startup entries,
/// Windows services and scheduled tasks. The file is human-readable JSON scoped per user.
/// </summary>
public sealed class AutorunHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const int MaxEntries = 500;
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public AutorunHistoryStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string FilePath => _paths.AutorunsHistoryPath;

    public string? LastError { get; private set; }

    public IReadOnlyList<AutorunHistoryRecord> GetAll()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var entries = JsonSerializer.Deserialize<List<AutorunHistoryRecord>>(json, JsonOptions);
                return entries ?? [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LastError = ex.Message;
                return [];
            }
        }
    }

    public void Record(AutorunHistoryRecord record)
    {
        LastError = null;
        lock (_gate)
        {
            var entries = GetAll().ToList();
            entries.Insert(0, record);

            if (entries.Count > MaxEntries)
            {
                entries = entries.Take(MaxEntries).ToList();
            }

            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(entries, JsonOptions);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex.Message;
            }
        }
    }

    public void Clear()
    {
        LastError = null;
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex.Message;
            }
        }
    }
}
