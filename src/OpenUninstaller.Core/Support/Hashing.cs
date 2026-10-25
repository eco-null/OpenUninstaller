using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenUninstaller.Core.Support;

public static partial class Hashing
{
    public static string Sha256Hex(string text)
        => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Stable identifier for a set of identity components.</summary>
    public static string StableId(params string[] parts)
        => Sha256Hex(string.Join("\u001f", parts))[..16];

    public static string Slug(string text)
    {
        var cleaned = SlugCleaner().Replace(text.ToLowerInvariant(), "-").Trim('-');
        if (cleaned.Length == 0)
        {
            cleaned = "item";
        }

        return cleaned.Length > 48 ? cleaned[..48].Trim('-') : cleaned;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugCleaner();
}
