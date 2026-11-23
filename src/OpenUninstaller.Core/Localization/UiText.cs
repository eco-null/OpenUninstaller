namespace OpenUninstaller.Core.Localization;

/// <summary>Access to desktop wording from code that also runs in the English CLI.</summary>
public static class UiText
{
    public static string L(string key) => LocalizationService.Instance.GetString(key);

    public static string F(string key, params object?[] arguments) =>
        LocalizationService.Instance.Format(key, arguments);

    public static string Label(string value) => LocalizationService.Instance.TranslateLabel(value);
}
