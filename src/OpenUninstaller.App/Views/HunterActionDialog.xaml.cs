using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.Views;

public enum HunterAction
{
    Cancel,
    Uninstall,
    StopProcess,
    StopAndDelete,
    Completed
}

public partial class HunterActionDialog : Window
{
    private readonly HunterMatch _match;
    private readonly LiveActivityTracker _tracker;

    public HunterAction ActionResult { get; private set; } = HunterAction.Cancel;

    public HunterActionDialog(HunterMatch match, LiveActivityTracker tracker)
    {
        InitializeComponent();
        _match = match;
        _tracker = tracker;

        TargetTitle.Text = match.Program?.Name ?? match.ProcessName ?? L("TargetWindow");
        TargetSubtitle.Text = match.WindowTitle ?? match.Reason;
        ExecutableText.Text = match.ExePath ?? L("UnknownPath");
        PidText.Text = match.ProcessId.ToString();

        LiveStatsText.Text = F("LiveTrackingFileEventsSpawnedProcessesRecorded", _tracker.FileEvents.Count, _tracker.SpawnedProcesses.Count);
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        ActionResult = HunterAction.Uninstall;
        Close();
    }

    private async void OnKillProcessClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_match.ProcessId > 0)
            {
                IsEnabled = false;
                await StopTargetProcessAsync();
                NoticeWindow.Show(this, F("ProcessPIDWasTerminated", _match.ProcessName, _match.ProcessId), L("HunterMode"), MessageBoxButton.OK, MessageBoxImage.Information);
                ActionResult = HunterAction.StopProcess;
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            NoticeWindow.Show(this, F("CouldNotStopProcess", ex.Message), L("HunterMode"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnKillDeleteClick(object sender, RoutedEventArgs e)
    {
        var confirm = NoticeWindow.Show(
            this,
            F("StopTheProcessAndBackUpItsExecutable", _match.ExePath),
            L("ConfirmKillAndDelete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            IsEnabled = false;
            var services = ((App)Application.Current).Services;
            var guard = services.Guard.ValidateForDeletion(_match.ExePath ?? string.Empty, recursive: false);
            if (!guard.Allowed || guard.NormalizedPath is not { } path)
            {
                throw new InvalidOperationException(guard.Reason ?? L("TheExecutableIsProtected"));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(L("TheExecutableNoLongerExists"), path);
            }

            await StopTargetProcessAsync();
            var result = await services.Deletion.DeleteAsync(
                _match.Program?.Name ?? _match.ProcessName ?? Path.GetFileName(path),
                [new LeftoverItem
                {
                    Id = $"file|{path.ToLowerInvariant()}",
                    Kind = LeftoverKind.File,
                    DisplayPath = path,
                    FilePath = path,
                    Confidence = LeftoverConfidence.Exact,
                    Reason = L("ExecutableSelectedInHunterMode")
                }], dryRun: false);
            if (result.Items.Count != 1 || result.Items[0].Status != DeletionStatus.Deleted)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine,
                    result.Items.Select(i => i.Detail).Concat(result.Warnings)));
            }

            NoticeWindow.Show(this, F("ExecutableBackedUpAndDeletedBackup", result.BackupId), L("HunterMode"), MessageBoxButton.OK, MessageBoxImage.Information);
            ActionResult = HunterAction.StopAndDelete;
            Close();
        }
        catch (Exception ex)
        {
            NoticeWindow.Show(this, F("CouldNotDeleteFile", ex.Message), L("HunterMode"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task StopTargetProcessAsync()
    {
        if (_match.ProcessId <= 0 || string.IsNullOrWhiteSpace(_match.ExePath))
        {
            throw new InvalidOperationException(L("TheTargetProcessIdentityIsUnavailable"));
        }

        using var process = Process.GetProcessById(_match.ProcessId);
        var currentPath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentPath)
            || !string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(_match.ExePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L("TheProcessExecutableChangedSelectTheTargetAgain"));
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_match.ExePath) && File.Exists(_match.ExePath))
            {
                Process.Start("explorer.exe", $"/select,\"{_match.ExePath}\"");
            }
            else if (!string.IsNullOrWhiteSpace(_match.ExePath) && Directory.Exists(_match.ExePath))
            {
                Process.Start("explorer.exe", $"\"{_match.ExePath}\"");
            }
        }
        catch (Exception ex)
        {
            NoticeWindow.Show(this, F("CouldNotOpenFolder", ex.Message), L("HunterMode"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnViewChangesClick(object sender, RoutedEventArgs e)
    {
        var changesWindow = new LiveChangesWindow(_tracker.FileEvents, _tracker.SpawnedProcesses)
        {
            Owner = this
        };
        changesWindow.ShowDialog();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ActionResult = HunterAction.Cancel;
        Close();
    }
}
