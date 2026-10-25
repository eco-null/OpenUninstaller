using System.Text.Json;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Models;

/// <summary>Schedule and retention for the optional Windows maintenance service.</summary>
public sealed class MaintenanceSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 1440;
    public int KeepBackups { get; set; } = 20;
    public int LogRetentionDays { get; set; } = 30;
    public int ScanHistoryRetentionDays { get; set; } = 90;
    public bool CleanJunk { get; set; }
    public List<string> JunkCategories { get; set; } = ["user-temp"];

    public static MaintenanceSettings Load(AppPaths paths)
    {
        var file = Path.Combine(paths.DataDirectory, "maintenance.json");
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<MaintenanceSettings>(File.ReadAllText(file), SettingsJson.Options);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new MaintenanceSettings();
    }

    public void Save(AppPaths paths)
    {
        Normalize();
        Directory.CreateDirectory(paths.DataDirectory);
        var file = Path.Combine(paths.DataDirectory, "maintenance.json");
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SettingsJson.Options));
        File.Move(temp, file, overwrite: true);
    }

    public void Normalize()
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, 15, 43_200);
        KeepBackups = Math.Clamp(KeepBackups, 1, 500);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 3650);
        ScanHistoryRetentionDays = Math.Clamp(ScanHistoryRetentionDays, 1, 3650);
        JunkCategories = JunkCategories
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
