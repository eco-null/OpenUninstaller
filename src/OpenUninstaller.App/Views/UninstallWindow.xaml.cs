using System.ComponentModel;
using System.Windows;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;

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

        var result = MessageBox.Show(
            this,
            "An operation is still running. Cancel it and close?\n\nA vendor uninstaller may already be mid-flight; OpenUninstaller will not terminate it.",
            "Operation in progress",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        viewModel.RequestCancellation();
    }
}
