using System.Text.Json;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>Persists install traces as JSON under the machine data directory.</summary>
public sealed class TraceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;

    public TraceStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string DirectoryPath => _paths.TracesDirectory;

    public IReadOnlyList<TraceDocument> List()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var traces = new List<TraceDocument>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var trace = JsonSerializer.Deserialize<TraceDocument>(File.ReadAllText(file), JsonOptions);
                if (trace is not null)
                {
                    traces.Add(trace);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return traces
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
    }

    public TraceDocument? Get(string id)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        var path = Path.Combine(DirectoryPath, id + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TraceDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string Save(TraceDocument trace)
    {
        if (!IsSafeId(trace.Id))
        {
            throw new ArgumentException("Trace id contains invalid characters.", nameof(trace));
        }

        System.IO.Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, trace.Id + ".json");
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(trace, JsonOptions));
        File.Move(temp, target, overwrite: true);
        return target;
    }

    public bool Delete(string id)
    {
        if (!IsSafeId(id))
        {
            return false;
        }

        var path = Path.Combine(DirectoryPath, id + ".json");
        try
        {
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

    /// <summary>
    /// Chooses the best trace for a program. Name equality beats install-location evidence; a
    /// location-only trace is used when the package was installed to a unique folder.
    /// </summary>
    public TraceDocument? FindFor(InstalledProgram program)
    {
        TraceDocument? best = null;
        var bestScore = 0;

        foreach (var trace in List())
        {
            var score = MatchScore(trace, program);
            if (score > bestScore)
            {
                bestScore = score;
                best = trace;
            }
        }

        return best;
    }

    /// <summary>How strongly a trace describes the installed application; 0 when it does not.</summary>
    public static int MatchScore(TraceDocument trace, InstalledProgram program)
    {
        var traceName = NameNormalizer.Compact(trace.ProgramName);
        if (traceName.Length > 0 && traceName == NameNormalizer.Compact(program.Name))
        {
            return 100;
        }

        if (!string.IsNullOrWhiteSpace(trace.InstallLocation) && !string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            if (string.Equals(
                    trace.InstallLocation.TrimEnd('\\'),
                    program.InstallLocation.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return 95;
            }

            if (program.InstallLocation.StartsWith(trace.InstallLocation, StringComparison.OrdinalIgnoreCase)
                || trace.InstallLocation.StartsWith(program.InstallLocation, StringComparison.OrdinalIgnoreCase))
            {
                return 85;
            }
        }

        var tokens = NameNormalizer.MatchTokens(program.Name, program.Publisher);
        return tokens.Count > 0 && NameNormalizer.CountTokenMatches(trace.ProgramName, tokens) >= 2 ? 70 : 0;
    }

    private static bool IsSafeId(string id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Length <= 120
           && id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
           && !id.Contains("..", StringComparison.Ordinal);
}
