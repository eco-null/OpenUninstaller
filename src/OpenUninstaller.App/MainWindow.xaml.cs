using System.Windows;
using OpenUninstaller.App.ViewModels;

namespace OpenUninstaller.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Infrastructure.ThemeManager.ApplyWindowChrome(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }
}
