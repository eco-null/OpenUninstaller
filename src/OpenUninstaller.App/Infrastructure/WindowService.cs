using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenUninstaller.App.ViewModels;
using OpenUninstaller.App.Views;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.Infrastructure;

/// <summary>Keeps window creation out of the view models.</summary>
public sealed class WindowService
{
    private readonly AppServices _services;

    public WindowService(AppServices services)
    {
        _services = services;
    }

    public Window? Owner { get; set; }

    public bool ShowUninstall(InstalledProgram program, bool force, bool silent, bool scanOnly = false)
    {
        var viewModel = new UninstallWorkflowViewModel(_services, program, force, silent, scanOnly);
        var window = new UninstallWindow
        {
            DataContext = viewModel,
            Owner = Owner ?? Application.Current.MainWindow
        };
        viewModel.RequestClose += () => window.Close();
        window.ShowDialog();
        var changed = viewModel.DeletionResult is not null;
        viewModel.Dispose();
        return changed;
    }

    public void ShowRestorePreview(RestorePreview preview)
    {
        var window = new RestorePreviewWindow(preview)
        {
            Owner = Owner ?? Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    public string? Prompt(string title, string label, string initialValue = "")
    {
        var window = new PromptWindow(title, label, initialValue)
        {
            Owner = Owner ?? Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Value : null;
    }

    /// <summary>Prompts until the user types <paramref name="expectedValue"/> or cancels.</summary>
    public string? PromptExact(string title, string label, string expectedValue)
    {
        var window = new PromptWindow(title, label, expectedValue: expectedValue)
        {
            Owner = Owner ?? Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public IReadOnlyList<string> PickFiles(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            CheckFileExists = true
        };
        return dialog.ShowDialog(Owner ?? Application.Current.MainWindow) == true
            ? dialog.FileNames
            : [];
    }

    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        return dialog.ShowDialog(Owner ?? Application.Current.MainWindow) == true
            ? dialog.FolderName
            : null;
    }

    public void ShowHunter(IReadOnlyList<InstalledProgram> programs)
    {
        var window = new HunterWindow(programs, program =>
        {
            ShowUninstall(program, force: false, silent: _services.Settings.PreferSilentUninstall);
        })
        {
            Owner = Owner ?? Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    public bool Confirm(string message, string title = "Confirm", bool danger = false)
    {
        var result = MessageBox.Show(
            Owner ?? Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            danger ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public void Error(string message, string title = "OpenUninstaller")
        => MessageBox.Show(
            Owner ?? Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    public void Info(string message, string title = "OpenUninstaller")
        => MessageBox.Show(
            Owner ?? Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Error($"Could not open the folder:\n{ex.Message}");
        }
    }

    public void RestartElevated()
    {
        if (!_services.Elevation.TryRelaunchElevated())
        {
            Error("Elevation was declined or is unavailable.");
            return;
        }

        Application.Current.Shutdown();
    }
}
