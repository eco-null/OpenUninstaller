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
        "official", "release", "update", "tool", "tools", "utility", "manager",
        // Categories and software classifications
        "player", "reader", "viewer", "editor", "studio", "converter", "recorder",
        "creator", "browser", "driver", "drivers", "cleaner", "optimizer", "booster",
        "assistant", "helper", "daemon", "agent", "engine", "center", "centre",
        "launcher", "uninstaller", "uninstall", "bootstrapper", "downloader",
        // Component, background, and runtime descriptors
        "boot", "startup", "tray", "updater", "notifier", "notification", "monitor",
        "broker", "host", "worker", "background", "autorun", "crash", "reporter",
        "handler", "hook", "patcher",
        // Editions and packaging
        "enterprise", "professional", "community", "personal", "business", "home",
        "starter", "basic", "express", "lite", "plus", "ultra", "max",
        "runtime", "redistributable", "framework", "platform", "package", "packet",
        "bundle", "sdk", "api", "service", "services", "system", "systems",
        "security", "antivirus",
        // Release channels and tags
        "beta", "alpha", "preview", "nightly", "canary", "insiders", "rc", "build"
    };

    public static bool IsStopWord(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var compact = Compact(token);
        return StopWords.Contains(compact);
    }

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
        var separated = SplitCamelCaseAndDigits(withoutVersions);
        var parts = SeparatorPattern().Split(separated.ToLowerInvariant());
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            var token = Compact(part);
            if (token.Length < 3 || token.All(char.IsDigit) || StopWords.Contains(token) || tokens.Contains(token))
            {
                continue;
            }

            tokens.Add(token);
        }

        if (tokens.Count == 0)
        {
            foreach (var part in parts)
            {
                var token = Compact(part);
                if (token.Length >= 3 && !token.All(char.IsDigit) && !tokens.Contains(token))
                {
                    tokens.Add(token);
                }
            }
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

    /// <summary>Counts how many tokens occur in a folder/file name using discrete token matching.</summary>
    public static int CountTokenMatches(string candidate, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(candidate) || tokens.Count == 0)
        {
            return 0;
        }

        var candidateTokens = Tokens(candidate);
        var candidateCompact = Compact(candidate);
        var matches = 0;

        foreach (var token in tokens)
        {
            if (token.Length < 3)
            {
                continue;
            }

            if (candidateTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                matches++;
                continue;
            }

            if (string.Equals(candidateCompact, token, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
                continue;
            }

            if (token.Length >= 5 && candidateTokens.Any(c =>
                c.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
                c[token.Length..].All(char.IsDigit)))
            {
                matches++;
            }
        }

        return matches;
    }

    public static bool LooksLikeVersion(string text)
        => VersionOnlyPattern().IsMatch(text.Trim());

    private static string SplitCamelCaseAndDigits(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var step1 = LowerUpperPattern().Replace(input, "$1 $2");
        var step2 = AcronymPattern().Replace(step1, "$1 $2");
        var step3 = LetterDigitPattern().Replace(step2, "$1 $2");
        return DigitLetterPattern().Replace(step3, "$1 $2");
    }

    [GeneratedRegex(@"([a-z])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex LowerUpperPattern();

    [GeneratedRegex(@"([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex AcronymPattern();

    [GeneratedRegex(@"([a-zA-Z])([0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LetterDigitPattern();

    [GeneratedRegex(@"([0-9])([a-zA-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex DigitLetterPattern();

    [GeneratedRegex(@"\b(?:v|ver|version)?\s*\d+(?:\.\d+){1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\b\d+(?:\.\d+){1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionOnlyPattern();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorPattern();
}
