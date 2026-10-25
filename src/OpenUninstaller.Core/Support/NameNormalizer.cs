using System.Text;
using System.Text.RegularExpressions;

namespace OpenUninstaller.Core.Support;

/// <summary>Tokenizes product names for conservative leftover matching.</summary>
public static partial class NameNormalizer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "pro", "free", "trial", "setup", "installer", "install", "app", "application", "software",
        "inc", "ltd", "llc", "gmbh", "corp", "corporation", "co", "the", "for", "and", "with",
        "windows", "win", "microsoft", "edition", "version", "portable", "standard", "premium",
        "ultimate", "suite", "client", "server", "desktop", "x64", "x86", "32", "64", "bit",
        "official", "release", "update", "tool", "tools", "utility", "manager"
    };

    public static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var withoutVersions = VersionPattern().Replace(text, " ");
        var parts = SeparatorPattern().Split(withoutVersions.ToLowerInvariant());
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            var token = Compact(part);
            if (token.Length < 3 || StopWords.Contains(token) || tokens.Contains(token))
            {
                continue;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// Tokens used for matching: the product name without the publisher prefix, plus left-over
    /// distinctive name tokens when the name is generic.
    /// </summary>
    public static IReadOnlyList<string> MatchTokens(string? name, string? publisher)
    {
        var nameTokens = Tokens(name);
        if (nameTokens.Count == 0)
        {
            return [];
        }

        var publisherTokens = Tokens(publisher);
        if (publisherTokens.Count > 0)
        {
            var stripped = nameTokens.Where(t => !publisherTokens.Contains(t)).ToList();
            if (stripped.Count > 0)
            {
                return stripped;
            }
        }

        return nameTokens;
    }

    /// <summary>Counts how many tokens occur in a folder/file name.</summary>
    public static int CountTokenMatches(string candidate, IReadOnlyList<string> tokens)
    {
        var compact = Compact(candidate);
        if (compact.Length == 0)
        {
            return 0;
        }

        var matches = 0;
        foreach (var token in tokens)
        {
            if (token.Length >= 3 && compact.Contains(token, StringComparison.Ordinal))
            {
                matches++;
            }
        }

        return matches;
    }

    public static bool LooksLikeVersion(string text)
        => VersionOnlyPattern().IsMatch(text.Trim());

    [GeneratedRegex(@"\b(?:v|ver|version)?\s*\d+(?:\.\d+){1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\b\d+(?:\.\d+){1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionOnlyPattern();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorPattern();
}
