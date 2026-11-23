using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenUninstaller.Core.Localization;

namespace OpenUninstaller.Tests;

public sealed partial class LocalizationTests
{
    [Fact]
    public void Every_supported_language_has_a_complete_catalog_with_matching_format_arguments()
    {
        using var resource = typeof(LocalizationService).Assembly.GetManifestResourceStream(
            "OpenUninstaller.Core.Localization.Languages.json");
        using var document = JsonDocument.Parse(resource!);
        var service = new LocalizationService();
        var expected = Strings(document.RootElement.GetProperty("english"));

        Assert.Equal(10, service.AvailableLanguages.Count);
        Assert.Equal(service.AvailableLanguages.Count, service.AvailableLanguages.Select(language => language.Code).Distinct().Count());
        foreach (var language in service.AvailableLanguages)
        {
            service.SetLanguage(language.Code);
            Assert.Equal(language.Id, service.CurrentLanguage);
            Assert.Equal(CultureInfo.GetCultureInfo(language.Code), service.Culture);

            var strings = Strings(document.RootElement.GetProperty(language.Id));
            Assert.Equal(expected.Keys.Order(), strings.Keys.Order());
            foreach (var (key, source) in expected)
            {
                var translation = strings[key];
                Assert.False(string.IsNullOrWhiteSpace(translation), $"{language.Id}/{key} is empty.");
                Assert.Equal(FormatArguments().Matches(source).Select(match => match.Value).Order(),
                    FormatArguments().Matches(translation).Select(match => match.Value).Order());
                Assert.Equal(CompositeFormat.Parse(source).MinimumArgumentCount,
                    CompositeFormat.Parse(translation).MinimumArgumentCount);
                Assert.Equal(translation, service[key]);
            }

            Assert.Contains("SHRED", service["TypeSHREDToContinue"], StringComparison.Ordinal);
            Assert.Contains("CLEAN", service["TypeCLEANToContinue"], StringComparison.Ordinal);
            Assert.Contains("02:04", service.Format("ETA", TimeSpan.FromSeconds(124)), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(" TR ", "turkish")]
    [InlineData("GER", "german")]
    [InlineData("SCH", "simplifiedchinese")]
    [InlineData("portuguese_standard", "portuguesebrazil")]
    [InlineData("PTG", "portuguesebrazil")]
    [InlineData("finnish", "english")]
    [InlineData(null, "english")]
    public void Saved_language_ids_and_legacy_codes_resolve_to_supported_languages(string? saved, string expected)
    {
        var service = new LocalizationService();
        service.SetLanguage("spanish");
        service.SetLanguage(saved);
        Assert.Equal(expected, service.CurrentLanguage);
    }

    [Fact]
    public void Changing_language_updates_indexer_observers_and_ignores_repeated_values()
    {
        var service = new LocalizationService();
        var properties = new List<string?>();
        var languages = new List<string>();
        service.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        service.LanguageChanged += languages.Add;

        service.SetLanguage("turkish");
        service.SetLanguage("tr-TR");

        Assert.Contains("Item[]", properties);
        Assert.Equal(["turkish"], languages);
        Assert.Equal("Kaldır", service["ActionUninstall"]);
        Assert.Equal("Yenile", service["ActionRefresh"]);
        Assert.Equal("Gereksiz dosyalar", service["NavJunk"]);
        Assert.Equal("fallback", service.GetString("MissingTranslation", "fallback"));
    }

    private static Dictionary<string, string> Strings(JsonElement catalog) => catalog.EnumerateObject()
        .Where(entry => !entry.Name.StartsWith("__", StringComparison.Ordinal))
        .ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!);

    [GeneratedRegex(@"(?<!\{)\{\d+[^{}]*\}(?!\})")]
    private static partial Regex FormatArguments();
}
