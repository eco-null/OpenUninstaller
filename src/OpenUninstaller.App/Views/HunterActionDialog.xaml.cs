using System.Diagnostics;
using System.IO;
using System.Windows;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

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

        TargetTitle.Text = match.Program?.Name ?? match.ProcessName ?? "Target Window";
        TargetSubtitle.Text = match.WindowTitle ?? match.Reason;
        ExecutableText.Text = match.ExePath ?? "Unknown path";
        PidText.Text = match.ProcessId.ToString();

        LiveStatsText.Text = $"Live tracking: {_tracker.FileEvents.Count} file events, {_tracker.SpawnedProcesses.Count} spawned processes recorded.";
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        ActionResult = HunterAction.Uninstall;
        Close();
    }

    private void OnKillProcessClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_match.ProcessId > 0)
            {
                var proc = Process.GetProcessById(_match.ProcessId);
                proc.Kill(entireProcessTree: true);
                MessageBox.Show(this, $"Process {proc.ProcessName} (PID {_match.ProcessId}) was terminated.", "Hunter mode", MessageBoxButton.OK, MessageBoxImage.Information);
                ActionResult = HunterAction.StopProcess;
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not stop process: {ex.Message}", "Hunter mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnKillDeleteClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            $"Are you sure you want to terminate the process and permanently delete its executable?\n\n{_match.ExePath}",
            "Confirm Kill and Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_match.ProcessId > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(_match.ProcessId);
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch (Exception)
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(_match.ExePath) && File.Exists(_match.ExePath))
            {
                File.Delete(_match.ExePath);
            }

            MessageBox.Show(this, "Process stopped and file deleted successfully.", "Hunter mode", MessageBoxButton.OK, MessageBoxImage.Information);
            ActionResult = HunterAction.StopAndDelete;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not delete file: {ex.Message}", "Hunter mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            MessageBox.Show(this, $"Could not open folder: {ex.Message}", "Hunter mode", MessageBoxButton.OK, MessageBoxImage.Warning);
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
