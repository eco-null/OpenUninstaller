using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Diagnostics;
using System.Windows.Threading;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

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

        // Handle Windows Explorer context menu target if provided
        string? targetPath = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if ((e.Args[i] == "--target" || e.Args[i] == "-t") && i + 1 < e.Args.Length)
            {
                targetPath = e.Args[i + 1];
                break;
            }
        }

        mainWindow.Show();

        if (!string.IsNullOrWhiteSpace(targetPath) && (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            var resolvedTarget = ShortcutResolver.TryResolve(targetPath) ?? targetPath;
            var programs = Services.Scanner.Scan();
            var match = HunterService.MatchExecutable(resolvedTarget, null, 0, programs);

            if (match.Matched && match.Program is not null)
            {
                windows.ShowUninstall(match.Program, force: false, silent: Services.Settings.PreferSilentUninstall);
            }
            else
            {
                var targetProgram = HunterService.CreateCleanupTarget(resolvedTarget);
                windows.ShowUninstall(targetProgram, force: true, silent: false, scanOnly: true);
            }
        }
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
