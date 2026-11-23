using System.Windows;

namespace OpenUninstaller.App.Views;

/// <summary>Uses the application language for both the message and its buttons.</summary>
public partial class NoticeWindow : Window
{
    private NoticeWindow(string message, string title, bool confirmation, MessageBoxImage image)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        var (glyph, color) = image switch
        {
            MessageBoxImage.Error => ("\uEA39", "Danger"),
            MessageBoxImage.Warning => ("\uE7BA", "Warning"),
            MessageBoxImage.Question => ("\uE897", "TextSecondary"),
            _ => ("\uE946", "TextSecondary")
        };
        NoticeIcon.Text = glyph;
        NoticeIcon.SetResourceReference(ForegroundProperty, color);
        DismissButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        DismissButton.IsDefault = confirmation;
        AcceptButton.IsDefault = !confirmation;
        if (confirmation)
        {
            AcceptButton.Content = OpenUninstaller.Core.Localization.UiText.L("NoticeYes");
            if (image == MessageBoxImage.Warning)
                AcceptButton.SetResourceReference(StyleProperty, "DangerButton");
        }

        Loaded += (_, _) => (confirmation ? DismissButton : AcceptButton).Focus();
    }

    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var confirmation = buttons == MessageBoxButton.YesNo;
        var window = new NoticeWindow(message, title, confirmation, image);
        if (owner?.IsVisible == true)
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var accepted = window.ShowDialog() == true;
        return confirmation
            ? (accepted ? MessageBoxResult.Yes : MessageBoxResult.No)
            : MessageBoxResult.OK;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnDismiss(object sender, RoutedEventArgs e) => DialogResult = false;
}
