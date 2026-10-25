namespace OpenUninstaller.Cli;

/// <summary>Small, dependency-free option parser with an explicit flag list.</summary>
internal sealed class ArgumentSet
{
    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yes", "force", "silent", "all", "csv", "dry-run", "overwrite", "delete",
        "no-scan", "help", "version", "verbose", "cursor", "diff", "ignore",
        "clear", "enable", "disable", "service"
    };

    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = [];

    public IReadOnlyList<string> Positionals => _positionals;

    public static ArgumentSet Parse(string[] args)
    {
        var result = new ArgumentSet();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "-?" or "--help")
            {
                result._options["help"] = null;
                continue;
            }

            if (arg is "-y")
            {
                result._options["yes"] = null;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result._positionals.Add(arg);
                continue;
            }

            var body = arg[2..];
            if (body.Length == 0)
            {
                throw new ArgumentException("Invalid option '--'.");
            }

            string name;
            string? value = null;
            var equals = body.IndexOf('=');
            if (equals >= 0)
            {
                name = body[..equals];
                value = body[(equals + 1)..];
            }
            else
            {
                name = body;
            }

            if (result._options.ContainsKey(name))
            {
                throw new ArgumentException($"Option '--{name}' was specified more than once.");
            }

            if (value is null && !KnownFlags.Contains(name))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
            }

            result._options[name] = value;
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string? FirstPositional => _positionals.Count > 0 ? _positionals[0] : null;

    public int? IntValue(string name, int? fallback = null)
    {
        var raw = Value(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var parsed))
        {
            throw new ArgumentException($"Option '--{name}' expects an integer.");
        }

        return parsed;
    }
}
