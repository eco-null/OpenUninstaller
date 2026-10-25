using System.Windows;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.Views;

public partial class RestorePreviewWindow : Window
{
    public RestorePreviewWindow(RestorePreview preview)
    {
        InitializeComponent();
        SummaryText.Text = preview.Summary;
        EntriesGrid.ItemsSource = preview.Entries;
        if (preview.Warnings.Count > 0)
        {
            WarningCard.Visibility = Visibility.Visible;
            WarningText.Text = string.Join(Environment.NewLine, preview.Warnings);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
