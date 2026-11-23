using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using OpenUninstaller.Core.Localization;

namespace OpenUninstaller.App.Infrastructure;

/// <summary>Looks up a translation independent of the control's data context.</summary>
[MarkupExtensionReturnType(typeof(BindingBase))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}

/// <summary>Localizes a built-in label or inserts a bound value into a translated sentence.</summary>
[MarkupExtensionReturnType(typeof(BindingBase))]
public sealed class LocValueExtension : MarkupExtension
{
    public string? Path { get; set; }
    public string? FormatKey { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding
        {
            Mode = BindingMode.OneWay,
            Converter = new LabelConverter(FormatKey)
        };
        binding.Bindings.Add(Path is null ? new Binding() : new Binding(Path));
        binding.Bindings.Add(new Binding(nameof(LocalizationService.CurrentLanguage))
        {
            Source = LocalizationService.Instance
        });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class LabelConverter(string? formatKey) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] == DependencyProperty.UnsetValue || values[0] is null)
                return string.Empty;

            var localization = LocalizationService.Instance;
            if (formatKey is not null)
                return localization.Format(formatKey, values[0]);

            return localization.TranslateLabel(values[0].ToString() ?? string.Empty);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
