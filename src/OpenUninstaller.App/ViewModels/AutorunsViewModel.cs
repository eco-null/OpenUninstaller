using System.Collections.ObjectModel;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.ViewModels;

public sealed class AutorunsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private readonly ObservableCollection<AutorunEntry> _allEntries = [];
    private readonly ObservableCollection<AutorunHistoryEntry> _allHistory = [];
    private AutorunEntry? _selected;
    private AutorunHistoryEntry? _selectedHistory;
    private string _searchText = string.Empty;
    private bool _isHistoryView;

    public AutorunsViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        ToggleCommand = new AsyncRelayCommand(
            ToggleAsync,
            parameter => (parameter is AutorunEntry || SelectedEntry is not null) && !IsBusy);
        ShowAutorunsCommand = new RelayCommand(_ => IsHistoryView = false);
        ShowHistoryCommand = new RelayCommand(_ => IsHistoryView = true);
        RollbackCommand = new AsyncRelayCommand(
            RollbackAsync,
            parameter => (parameter is AutorunHistoryEntry h ? h.CanRollback : SelectedHistoryEntry?.CanRollback == true) && !IsBusy);
        RollbackAllCommand = new AsyncRelayCommand(
            _ => RollbackAllAsync(),
            _ => CanRollbackCount > 0 && !IsBusy);
    }

    public ObservableCollection<AutorunEntry> Entries { get; } = [];
    public ObservableCollection<AutorunHistoryEntry> HistoryEntries { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand RollbackCommand { get; }
    public AsyncRelayCommand RollbackAllCommand { get; }
    public RelayCommand ShowAutorunsCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }

    public bool IsHistoryView
    {
        get => _isHistoryView;
        set
        {
            if (SetProperty(ref _isHistoryView, value))
            {
                OnPropertyChanged(nameof(IsAutorunsView));
                ApplyFilter();
            }
        }
    }

    public bool IsAutorunsView => !IsHistoryView;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public AutorunEntry? SelectedEntry
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                ToggleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AutorunHistoryEntry? SelectedHistoryEntry
    {
        get => _selectedHistory;
        set
        {
            if (SetProperty(ref _selectedHistory, value))
            {
                RollbackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int EnabledCount => _allEntries.Count(e => e.IsEnabled);
    public int DisabledCount => _allEntries.Count(e => !e.IsEnabled);
    public int HistoryCount => _allHistory.Count;
    public int CanRollbackCount => _allHistory.Count(h => h.CanRollback);
    public string CountLabel => $"{EnabledCount} enabled · {DisabledCount} disabled";

    public override void Activate() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Reading autoruns and history…";
        try
        {
            var scanTask = Task.Run(_services.Autoruns.Scan);
            var historyTask = Task.Run(_services.Autoruns.GetHistory);
            await Task.WhenAll(scanTask, historyTask);

            var scan = scanTask.Result;
            var history = historyTask.Result;

            _allEntries.Clear();
            foreach (var entry in scan.Entries)
            {
                _allEntries.Add(entry);
            }

            _allHistory.Clear();
            foreach (var h in history)
            {
                _allHistory.Add(h);
            }

            ApplyFilter();
            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(DisabledCount));
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(HistoryCount));
            OnPropertyChanged(nameof(CanRollbackCount));
            StatusMessage = scan.Warnings.Count == 0
                ? CountLabel
                : $"{CountLabel} · {scan.Warnings.Count} warning(s)";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _windows.Error($"Autoruns could not be read:\n{ex.Message}");
            StatusMessage = "Autoruns could not be read.";
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.RaiseCanExecuteChanged();
            ToggleCommand.RaiseCanExecuteChanged();
            RollbackCommand.RaiseCanExecuteChanged();
            RollbackAllCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RollbackAsync(object? parameter)
    {
        var item = parameter as AutorunHistoryEntry ?? SelectedHistoryEntry;
        if (item is null || !item.CanRollback)
        {
            return;
        }

        if (!_windows.Confirm(
            $"Restore autorun entry '{item.Name}' to its original startup state?",
            "Confirm rollback",
            danger: false))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Rolling back {item.Name}…";
        try
        {
            var (success, error) = await Task.Run(() =>
            {
                var ok = _services.Autoruns.Rollback(item.EntryId, out var err);
                return (ok, err);
            });

            if (!success)
            {
                _windows.Error(error ?? $"Failed to roll back '{item.Name}'.");
            }
        }
        catch (Exception ex)
        {
            _windows.Error($"Rollback error:\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task RollbackAllAsync()
    {
        if (CanRollbackCount == 0)
        {
            _windows.Info("There are no disabled entries available to roll back.", "Rollback");
            return;
        }

        if (!_windows.Confirm(
            $"Restore all {CanRollbackCount} disabled autorun entries to their original states?",
            "Confirm rollback all",
            danger: false))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Rolling back all disabled autorun entries…";
        try
        {
            var result = await Task.Run(() => _services.Autoruns.RollbackAll());
            _windows.Info(
                $"Rollback complete.\nRestored: {result.SucceededCount}\nFailed: {result.FailedCount}",
                "Rollback all");
        }
        catch (Exception ex)
        {
            _windows.Error($"Rollback all error:\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task ToggleAsync(object? parameter)
    {
        var entry = parameter as AutorunEntry ?? SelectedEntry;
        if (entry is null)
        {
            return;
        }

        var action = entry.IsEnabled ? "Disable" : "Enable";
        var explanation = entry.Source switch
        {
            AutorunSourceKind.MachineService when entry.IsEnabled =>
                "The service start mode is stored and the service is set to Disabled. It is not stopped.",
            AutorunSourceKind.MachineService =>
                "The original service start mode is restored from the disabled store.",
            AutorunSourceKind.ScheduledTask when entry.IsEnabled =>
                "The task is disabled through the Task Scheduler and the original definition is kept.",
            AutorunSourceKind.ScheduledTask or AutorunSourceKind.DisabledScheduledTask =>
                "The task is enabled again through the Task Scheduler. Its definition is kept.",
            _ when entry.IsEnabled =>
                "The entry is copied to OpenUninstaller's disabled store and can be re-enabled later.",
            _ => "The entry is restored from the disabled store."
        };
        if (!_windows.Confirm(
                $"{action} \"{entry.Name}\"?\n\n{explanation}",
                $"{action} autorun",
                danger: false))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"{action} {entry.Name}…";
        try
        {
            var (success, error) = await Task.Run(() =>
            {
                string? message;
                var ok = entry.IsEnabled
                    ? _services.Autoruns.Disable(entry.Id, out message)
                    : _services.Autoruns.Enable(entry.Id, out message);
                return (ok, error: message);
            });

            if (!success)
            {
                _windows.Error(error ?? "The autorun entry could not be changed.");
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allEntries
            : _allEntries.Where(entry =>
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.LocationDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (entry.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        Entries.Clear();
        foreach (var entry in matches)
        {
            Entries.Add(entry);
        }

        if (SelectedEntry is not null && !Entries.Contains(SelectedEntry))
        {
            SelectedEntry = null;
        }

        HistoryEntries.Clear();
        var historyMatches = string.IsNullOrEmpty(query)
            ? _allHistory
            : _allHistory.Where(h =>
                h.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (h.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || h.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || h.ActionLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || h.Command.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var h in historyMatches)
        {
            HistoryEntries.Add(h);
        }

        if (SelectedHistoryEntry is not null && !HistoryEntries.Contains(SelectedHistoryEntry))
        {
            SelectedHistoryEntry = null;
        }
    }
}
