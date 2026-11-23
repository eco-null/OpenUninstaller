using System.Windows;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.Views;

public partial class PromptWindow : Window
{
    private readonly string? _expectedValue;

    public PromptWindow(string title, string label, string initialValue = "", string? expectedValue = null)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        _expectedValue = expectedValue;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Value => InputBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            NoticeWindow.Show(this, L("AValueIsRequired"), "OpenUninstaller", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_expectedValue is not null
            && !string.Equals(InputBox.Text.Trim(), _expectedValue, StringComparison.Ordinal))
        {
            NoticeWindow.Show(
                this,
                F("TypeExactlyToConfirmThisActionCannotBe", _expectedValue),
                L("ConfirmationRequired"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            InputBox.SelectAll();
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
