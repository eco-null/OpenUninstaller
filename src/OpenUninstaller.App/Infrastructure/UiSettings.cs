using System.Text.Json;
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

    private string _path;

    private UiSettings(string path)
    {
        _path = path;
    }

    public string Theme { get; set; } = "Dark";
    public bool PreferSilentUninstall { get; set; }
    public int KeepBackups { get; set; } = 20;
    public bool OverwriteOnRestore { get; set; }

    public static UiSettings Load(AppPaths paths)
    {
        var path = Path.Combine(paths.DataDirectory, "ui-settings.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded._path = path;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new UiSettings(path);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
