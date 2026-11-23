using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.App.Infrastructure;

public sealed class UiSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private string _path = null!;

    [JsonConstructor]
    private UiSettings() { }

    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "english";
    public bool EnableContextMenu { get; set; }
    public bool PreferSilentUninstall { get; set; }
    public int KeepBackups { get; set; } = 20;
    public bool OverwriteOnRestore { get; set; }

    public static UiSettings Load(AppPaths paths)
    {
        var path = Path.Combine(paths.LocalDataDirectory, "ui-settings.json");
        var legacyPath = Path.Combine(paths.DataDirectory, "ui-settings.json");
        var source = File.Exists(path) ? path : legacyPath;
        UiSettings? settings = null;
        if (File.Exists(source))
        {
            try
            {
                settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(source), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }

        settings ??= new UiSettings();
        settings._path = path;
        return settings;
    }

    public bool Save()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var pending = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(pending, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(pending, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
