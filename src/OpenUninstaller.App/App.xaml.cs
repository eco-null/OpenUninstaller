using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Diagnostics;
using System.Windows.Threading;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;

namespace OpenUninstaller.App;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Services = AppServices.Create();
        ThemeManager.Apply(Services.Settings.Theme);

        var windows = new WindowService(Services);
        var mainWindow = new MainWindow
        {
            DataContext = new MainViewModel(Services, windows)
        };
        windows.Owner = mainWindow;
        MainWindow = mainWindow;

        DispatcherUnhandledException += OnUnhandledException;
        EnableBindingDiagnostics();
        mainWindow.Show();
    }

    private void EnableBindingDiagnostics()
    {
#if DEBUG
        try
        {
            Directory.CreateDirectory(Services.Paths.LogsDirectory);
            var listener = new TextWriterTraceListener(
                Path.Combine(Services.Paths.LogsDirectory, "wpf-bindings.log"));
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
#endif
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            MainWindow,
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe operation was stopped. No files were deleted.",
            "OpenUninstaller",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
