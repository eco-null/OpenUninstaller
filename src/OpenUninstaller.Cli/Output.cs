using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.Cli;

internal static class Output
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Json(object value)
        => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static object ProgramDto(InstalledProgram program) => new
    {
        id = program.Id,
        name = program.Name,
        version = program.Version,
        publisher = program.Publisher,
        installLocation = program.InstallLocation,
        uninstallCommand = program.UninstallCommand,
        quietUninstallCommand = program.QuietUninstallCommand,
        estimatedSizeBytes = program.EstimatedSizeBytes,
        installDate = program.InstallDate?.ToString("yyyy-MM-dd"),
        architecture = program.Architecture.ToString().ToLowerInvariant(),
        hive = program.Location.HiveName,
        view = program.Location.Is32BitView ? "32" : "64",
        registryPath = program.RegistryPath,
        isWindowsInstaller = program.IsWindowsInstaller,
        isSystemComponent = program.IsSystemComponent,
        urlInfoAbout = program.UrlInfoAbout
    };

    public static object LeftoverDto(LeftoverItem item) => new
    {
        id = item.Id,
        kind = item.Kind.ToString().ToLowerInvariant(),
        path = item.DisplayPath,
        confidence = item.ConfidenceLabel,
        score = (int)item.Confidence,
        reason = item.Reason,
        fromTrace = item.IsFromTrace,
        selected = item.IsSelected,
        isProtected = item.IsProtected,
        protectedReason = item.ProtectedReason,
        valueName = item.ValueName,
        taskName = item.TaskName,
        isAppCompatEntry = item.IsAppCompatEntry,
        sizeBytes = item.SizeBytes
    };

    public static void Table(IReadOnlyList<string[]> rows, string[] headers)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("(none)");
            return;
        }

        var columns = headers.Length;
        var widths = new int[columns];
        for (var i = 0; i < columns; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < columns && i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        Console.WriteLine(FormatRow(headers, widths));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            Console.WriteLine(FormatRow(row, widths));
        }
    }

    private static string FormatRow(IReadOnlyList<string> values, int[] widths)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < widths.Length; i++)
        {
            var value = i < values.Count ? values[i] : string.Empty;
            if (i > 0)
            {
                builder.Append("  ");
            }

            builder.Append(value.Length > widths[i] ? value[..Math.Max(0, widths[i] - 1)] + "…" : value.PadRight(widths[i]));
        }

        return builder.ToString().TrimEnd();
    }

    public static string Shorten(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }

    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains('"') || value.Contains(',') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
