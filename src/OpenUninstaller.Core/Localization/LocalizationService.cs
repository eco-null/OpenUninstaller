using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace OpenUninstaller.Core.Localization;

public sealed record LanguageInfo(string Id, string Name, string Code);

/// <summary>
/// Centralized multi-language localization engine supporting 45 international languages.
/// Provides instant fallback to English and dynamic runtime language switching.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> LazyInstance = new(() => new LocalizationService());
    public static LocalizationService Instance => LazyInstance.Value;

    private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageInfo> _availableLanguages = [];
    private string _currentLanguage = "english";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? LanguageChanged;

    public LocalizationService()
    {
        LoadLanguages();
        SetLanguage("english");
    }

    public IReadOnlyList<LanguageInfo> AvailableLanguages => _availableLanguages;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set => SetLanguage(value);
    }

    public string this[string key] => GetString(key);

    public string GetString(string key, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback ?? string.Empty;
        }

        if (_languages.TryGetValue(_currentLanguage, out var currentDict) &&
            currentDict.TryGetValue(key, out var val) &&
            !string.IsNullOrWhiteSpace(val))
        {
            return val;
        }

        if (_languages.TryGetValue("english", out var engDict) &&
            engDict.TryGetValue(key, out var engVal) &&
            !string.IsNullOrWhiteSpace(engVal))
        {
            return engVal;
        }

        return fallback ?? key;
    }

    public void SetLanguage(string languageId)
    {
        if (string.IsNullOrWhiteSpace(languageId))
        {
            languageId = "english";
        }

        var normalized = languageId.Trim().ToLowerInvariant();
        if (!_languages.ContainsKey(normalized))
        {
            normalized = _availableLanguages.FirstOrDefault(l =>
                l.Id.Equals(languageId, StringComparison.OrdinalIgnoreCase) ||
                l.Code.Equals(languageId, StringComparison.OrdinalIgnoreCase))?.Id ?? "english";
        }

        if (_currentLanguage == normalized && _availableLanguages.Count > 0)
        {
            return;
        }

        _currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(_currentLanguage);
    }

    private void LoadLanguages()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "OpenUninstaller.Core.Localization.Languages.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return;
            }

            using var doc = JsonDocument.Parse(stream);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var langId = prop.Name;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string name = langId;
                string code = langId.ToUpperInvariant();

                foreach (var item in prop.Value.EnumerateObject())
                {
                    if (item.Name == "__Name")
                    {
                        name = item.Value.GetString() ?? name;
                    }
                    else if (item.Name == "__Code")
                    {
                        code = item.Value.GetString() ?? code;
                    }
                    else
                    {
                        dict[item.Name] = item.Value.GetString() ?? string.Empty;
                    }
                }

                _languages[langId] = dict;
                _availableLanguages.Add(new LanguageInfo(langId, name, code));
            }

            _availableLanguages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
        }
    }
}
