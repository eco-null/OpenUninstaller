using System.Collections.ObjectModel;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.ViewModels;

public sealed class BackupsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private BackupRecord? _selected;
    private bool _overwrite;

    public BackupsViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        _overwrite = services.Settings.OverwriteOnRestore;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => SelectedBackup is not null && !IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, _ => SelectedBackup is not null && !IsBusy);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, _ => SelectedBackup is not null && !IsBusy);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => SelectedBackup is not null);
        PruneCommand = new AsyncRelayCommand(_ => PruneAsync(), _ => !IsBusy);
    }

    public ObservableCollection<BackupRecord> Backups { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand PruneCommand { get; }

    public BackupRecord? SelectedBackup
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionSummary));
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelection => _selected is not null;
    public bool Overwrite
    {
        get => _overwrite;
        set => SetProperty(ref _overwrite, value);
    }

    public string SelectionSummary => _selected is null
        ? "Select a backup to restore."
        : $"{_selected.ProgramName} · {_selected.Summary}";

    public string CountLabel => $"{Backups.Count} backup(s)";

    public override void Activate() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Loading backups…";
        try
        {
            var records = await Task.Run(_services.Backups.List);
            Backups.Clear();
            foreach (var record in records)
            {
                Backups.Add(record);
            }

            OnPropertyChanged(nameof(CountLabel));
            StatusMessage = CountLabel;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _windows.Error($"Backups could not be read:\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task PreviewAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Building restore preview…";
        try
        {
            var preview = await Task.Run(() => _services.Backups.PreviewRestore(SelectedBackup.Id, Overwrite));
            if (preview.Entries.Count == 0 && preview.Warnings.Count > 0)
            {
                _windows.Error(string.Join(Environment.NewLine, preview.Warnings), "Restore preview");
                return;
            }

            _windows.ShowRestorePreview(preview);
            StatusMessage = $"Preview: {preview.Summary}";
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task RestoreAsync(object? parameter)
    {
        var record = parameter as BackupRecord ?? SelectedBackup;
        if (record is null)
        {
            return;
        }

        if (!_windows.Confirm(
                $"Restore this backup?\n\n{record.ProgramName}\n{record.Summary}\n\n" +
                (Overwrite ? "Existing files will be overwritten." : "Existing files will be skipped."),
                "Restore backup",
                danger: false))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Restoring…";
        try
        {
            var result = await Task.Run(() => _services.Backups.RestoreAsync(record.Id, Overwrite));
            _windows.Info(
                $"Restored: {result.RestoredCount}\nFailed: {result.FailedCount}\nSkipped: {result.Entries.Count(e => e.Status == RestoreEntryStatus.Skipped)}" +
                (result.Warnings.Count > 0 ? "\n\n" + string.Join("\n", result.Warnings) : string.Empty),
                "Restore backup");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task DeleteAsync(object? parameter)
    {
        var record = parameter as BackupRecord ?? SelectedBackup;
        if (record is null)
        {
            return;
        }

        if (!_windows.Confirm(
                $"Delete this backup permanently?\n\n{record.ProgramName}\n{record.Summary}\n\n" +
                "The captured files and registry exports will be removed. This cannot be undone.",
                "Delete backup",
                danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var deleted = await Task.Run(() => _services.Backups.Delete(record.Id));
            if (!deleted)
            {
                _windows.Error("The backup could not be deleted.");
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task PruneAsync()
    {
        var keep = Math.Max(1, _services.Settings.KeepBackups);
        if (!_windows.Confirm(
                $"Keep only the newest {keep} backup(s) and delete the rest?",
                "Prune backups",
                danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var removed = await Task.Run(() => _services.Backups.Prune(keep));
            _windows.Info($"Removed {removed} old backup(s).", "Prune backups");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private void OpenFolder()
    {
        if (_selected is not null)
        {
            _windows.OpenFolder(_selected.Directory);
        }
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        PruneCommand.RaiseCanExecuteChanged();
    }
}
