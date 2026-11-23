using System.ComponentModel;
using System.Windows;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.Views;

public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is UninstallWorkflowViewModel viewModel && viewModel.IsScanOnly)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not UninstallWorkflowViewModel viewModel || !viewModel.IsRunningState)
        {
            return;
        }

        var result = NoticeWindow.Show(
            this,
            L("AnOperationIsStillRunningCancelItAnd"),
            L("OperationInProgress"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        viewModel.RequestCancellation();
    }
}
