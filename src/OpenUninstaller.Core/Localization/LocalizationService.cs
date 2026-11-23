using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace OpenUninstaller.Core.Localization;

public sealed record LanguageInfo(string Id, string Name, string Code);

/// <summary>Loads the desktop translation catalog and notifies bindings when the language changes.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string English = "english";
    private static readonly Lazy<LocalizationService> LazyInstance = new(() => new LocalizationService());
    public static LocalizationService Instance => LazyInstance.Value;

    private readonly IReadOnlyDictionary<string, Dictionary<string, string>> _catalogs;
    private readonly Dictionary<string, string> _keysByEnglish;
    private LanguageInfo _language;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? LanguageChanged;

    public LocalizationService()
    {
        using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(
            "OpenUninstaller.Core.Localization.Languages.json")
            ?? throw new InvalidOperationException("The desktop language catalog is missing.");
        _catalogs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidOperationException("The desktop language catalog is empty.");

        AvailableLanguages = Array.AsReadOnly(_catalogs.Select(pair =>
                new LanguageInfo(pair.Key, pair.Value["__Name"], pair.Value["__Code"]))
            .ToArray());
        _language = AvailableLanguages.Single(language => language.Id == English);
        _keysByEnglish = _catalogs[English]
            .Where(pair => !pair.Key.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    public string CurrentLanguage
    {
        get => _language.Id;
        set => SetLanguage(value);
    }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language.Code);
    public string this[string key] => GetString(key);

    public string GetString(string key, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return fallback ?? string.Empty;

        if (_catalogs[_language.Id].TryGetValue(key, out var translation) && !string.IsNullOrWhiteSpace(translation))
            return translation;

        return _catalogs[English].GetValueOrDefault(key) ?? fallback ?? key;
    }

    /// <summary>Translates built-in labels whose English value is also a stored ID.</summary>
    public string TranslateLabel(string label) =>
        _keysByEnglish.TryGetValue(label, out var key) ? GetString(key) : label;

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, GetString(key), arguments);

    public void SetLanguage(string? languageId)
    {
        var id = languageId?.Trim().ToLowerInvariant();
        id = id switch
        {
            "eng" or "en" => "english",
            "ger" or "de" => "german",
            "esp" or "es" => "spanish",
            "fra" or "fr" => "french",
            "ita" or "it" or "italian" => "italiano",
            "jpn" or "ja" => "japanese",
            "rus" or "ru" => "russian",
            "tr" => "turkish",
            "sch" or "zh" or "zh-cn" or "zh-sg" => "simplifiedchinese",
            "pt" or "ptg" or "ptgstd" or "pt-pt" or "portuguese" or "portuguese_standard" => "portuguesebrazil",
            _ => id
        };

        var selected = AvailableLanguages.FirstOrDefault(language =>
            language.Id == id || language.Code.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? AvailableLanguages.Single(language => language.Id == English);
        if (selected == _language)
            return;

        _language = selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(_language.Id);
    }
}
