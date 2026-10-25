using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.App.Views;

public partial class HunterWindow : Window, IDisposable
{
    private readonly IReadOnlyList<InstalledProgram> _programs;
    private readonly Action<InstalledProgram> _onUninstall;
    private readonly LiveActivityTracker _tracker = new();
    private bool _resolving;
    private bool _disposed;

    public HunterWindow()
        : this([], _ => { })
    {
    }

    public HunterWindow(IReadOnlyList<InstalledProgram> programs, Action<InstalledProgram> onUninstall)
    {
        InitializeComponent();
        _programs = programs;
        _onUninstall = onUninstall;

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            _tracker.FileActivityDetected += _ => UpdateTrackingStatus();
            _tracker.ProcessActivityDetected += _ => UpdateTrackingStatus();
            _tracker.Start();
        };

        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tracker.Stop();
        _tracker.Dispose();
        GC.SuppressFinalize(this);
    }

    private void UpdateTrackingStatus()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (TrackingStatusText is not null)
            {
                TrackingStatusText.Text = $"Live activity: {_tracker.FileEvents.Count} file changes, {_tracker.SpawnedProcesses.Count} processes";
            }
        });
    }

    private async void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_resolving)
        {
            return;
        }

        _resolving = true;
        Hide();
        try
        {
            await Task.Delay(180);
            var match = HunterService.MatchCursor(_programs);
            if (!match.Matched && string.IsNullOrWhiteSpace(match.ExePath))
            {
                var detail = string.IsNullOrWhiteSpace(match.Reason)
                    ? "No window or process found under cursor."
                    : match.Reason;

                var result = MessageBox.Show(
                    $"{detail}\n\nDo you want to inspect live system activity recorded so far?",
                    "Hunter mode",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    new LiveChangesWindow(_tracker.FileEvents, _tracker.SpawnedProcesses).ShowDialog();
                }

                Close();
                return;
            }

            var dialog = new HunterActionDialog(match, _tracker);
            dialog.ShowDialog();

            switch (dialog.ActionResult)
            {
                case HunterAction.Uninstall:
                    var program = match.Program;
                    if (program is null && !string.IsNullOrWhiteSpace(match.ExePath))
                    {
                        program = HunterService.CreateCleanupTarget(match.ExePath);
                    }

                    Close();
                    if (program is not null)
                    {
                        _onUninstall(program);
                    }
                    return;

                case HunterAction.StopProcess:
                case HunterAction.StopAndDelete:
                case HunterAction.Completed:
                    Close();
                    return;

                case HunterAction.Cancel:
                default:
                    Show();
                    Activate();
                    Focus();
                    break;
            }
        }
        finally
        {
            _resolving = false;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
