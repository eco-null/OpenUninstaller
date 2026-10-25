using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.App.ViewModels;

public enum WorkflowState
{
    Confirm,
    Running,
    Review,
    Result
}

public sealed class LeftoverItemRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public LeftoverItemRow(LeftoverItem item, Action selectionChanged)
    {
        Item = item;
        _selectionChanged = selectionChanged;
        _isSelected = item.IsSelected;
    }

    public LeftoverItem Item { get; }
    public string DisplayPath => Item.DisplayPath;
    public string KindLabel => Item.KindLabel;
    public string ConfidenceLabel => Item.ConfidenceLabel;
    public string Reason => Item.Reason;
    public bool IsProtected => Item.IsProtected;
    public string? ProtectedReason => Item.ProtectedReason;
    public string SizeDisplay => Item.SizeDisplay;
    public string Tooltip => Item.IsProtected ? Item.ProtectedReason ?? Reason : Reason;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsProtected && value)
            {
                return;
            }

            if (SetProperty(ref _isSelected, value))
            {
                Item.IsSelected = value;
                _selectionChanged();
            }
        }
    }
}

public sealed class LeftoverGroupRow
{
    public required string Title { get; init; }
    public ObservableCollection<LeftoverItemRow> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
}

public sealed class UninstallWorkflowViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly bool _scanOnly;
    private readonly CancellationTokenSource _cts = new();
    private WorkflowState _state;
    private string _statusText = string.Empty;
    private string? _error;
    private bool _preferSilent;
    private bool _force;
    private bool _isBusy;
    private LeftoverScanResult? _scan;
    private LeftoverScanDiff? _scanDiff;
    private UninstallResult? _uninstall;
    private LeftoverDeletionResult? _deletion;

    public UninstallWorkflowViewModel(
        AppServices services,
        InstalledProgram program,
        bool force,
        bool silent,
        bool scanOnly)
    {
        _services = services;
        _scanOnly = scanOnly;
        Program = program;
        _force = force;
        _preferSilent = silent;
        _state = scanOnly ? WorkflowState.Running : WorkflowState.Confirm;

        var command = UninstallCommandResolver.Resolve(program, silent);
        CommandDisplay = string.IsNullOrWhiteSpace(command.DisplayCommand)
            ? "No uninstall command is registered."
            : command.DisplayCommand;
        CommandWarning = command.Error;
        IsCommandUsable = command.IsUsable;

        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => !IsBusy);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => !IsBusy && SelectedCount > 0);
        IgnoreCommand = new RelayCommand(_ => IgnoreSelected(), _ => !IsBusy && SelectedCount > 0);
        ResetIgnoredCommand = new AsyncRelayCommand(_ => ResetIgnoredAsync(), _ => !IsBusy && HasIgnoredEntries);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke());
        SelectAllCommand = new RelayCommand(_ => SelectAll(), _ => State == WorkflowState.Review);
        SelectNoneCommand = new RelayCommand(_ => SelectNone(), _ => State == WorkflowState.Review);
        OpenBackupCommand = new RelayCommand(_ => OpenPath(DeletionResult?.BackupDirectory), _ => DeletionResult?.BackupDirectory is not null);
        OpenLogCommand = new RelayCommand(_ => OpenPath(_services.Log.FilePath));
    }

    public event Action? RequestClose;

    public InstalledProgram Program { get; }
    public string Title => _scanOnly ? "Scan for leftovers" : "Uninstall application";
    public string CommandDisplay { get; }
    public string? CommandWarning { get; }
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public bool IsCommandUsable { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand IgnoreCommand { get; }
    public AsyncRelayCommand ResetIgnoredCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand OpenBackupCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public ObservableCollection<LeftoverGroupRow> Groups { get; } = [];

    public WorkflowState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsConfirmState));
            OnPropertyChanged(nameof(IsRunningState));
            OnPropertyChanged(nameof(IsReviewState));
            OnPropertyChanged(nameof(IsResultState));
            OnPropertyChanged(nameof(IsScanOnly));                DeleteCommand.RaiseCanExecuteChanged();
                IgnoreCommand.RaiseCanExecuteChanged();
                ResetIgnoredCommand.RaiseCanExecuteChanged();
                SelectAllCommand.RaiseCanExecuteChanged();
            SelectNoneCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(ResultDetail));
        }
    }

    public bool IsConfirmState => State == WorkflowState.Confirm;
    public bool IsRunningState => State == WorkflowState.Running;
    public bool IsReviewState => State == WorkflowState.Review;
    public bool IsResultState => State == WorkflowState.Result;
    public bool IsScanOnly => _scanOnly;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                StartCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool PreferSilent
    {
        get => _preferSilent;
        set => SetProperty(ref _preferSilent, value);
    }

    public bool Force
    {
        get => _force;
        set
        {
            if (SetProperty(ref _force, value))
            {
                Error = null;
            }
        }
    }

    public LeftoverScanResult? Scan
    {
        get => _scan;
        private set => SetProperty(ref _scan, value);
    }

    public LeftoverScanDiff? ScanDiff
    {
        get => _scanDiff;
        private set
        {
            if (SetProperty(ref _scanDiff, value))
            {
                OnPropertyChanged(nameof(DiffSummary));
                OnPropertyChanged(nameof(HasScanHistory));
            }
        }
    }

    public string DiffSummary => ScanDiff?.Summary ?? "No previous scan was stored for this application.";
    public bool HasScanHistory => ScanDiff?.HasPrevious == true;

    public int IgnoredCount => Scan?.IgnoredCount ?? 0;
    public bool HasIgnoredEntries => IgnoredCount > 0 || _services.Ignored.CountFor(Program) > 0;

    public UninstallResult? UninstallResult
    {
        get => _uninstall;
        private set => SetProperty(ref _uninstall, value);
    }

    public LeftoverDeletionResult? DeletionResult
    {
        get => _deletion;
        private set => SetProperty(ref _deletion, value);
    }

    public string ReviewSummary => Scan is null
        ? string.Empty
        : $"{Scan.Items.Count} candidate(s) · {SelectedCount} selected · {InstalledProgram.FormatBytes(Scan.Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes))}";
    public string ReviewWarning => "Deleting the wrong key can break other software. Review the evidence before deleting.";
    public string ResultSummary
    {
        get
        {
            if (_deletion is not null)
            {
                return $"Removed {_deletion.DeletedCount} item(s), skipped {_deletion.SkippedCount}, failed {_deletion.FailedCount}.";
            }

            if (_uninstall is not null)
            {
                return string.Join(" ", _uninstall.Messages);
            }

            return "No changes were made.";
        }
    }

    public string ResultDetail
    {
        get
        {
            var lines = new List<string>();
            if (_uninstall?.StillRegistered == true)
            {
                lines.Add("The application is still registered.");
            }

            if (_uninstall?.TimedOut == true)
            {
                lines.Add("The vendor uninstaller did not finish. It was not terminated.");
            }

            if (_deletion?.BackupId is not null)
            {
                lines.Add($"Backup: {_deletion.BackupId}");
                lines.Add($"Folder: {_deletion.BackupDirectory}");
            }

            lines.Add($"Log: {_services.Log.FilePath ?? "(not available)"}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public int SelectedCount => Groups.SelectMany(g => g.Items).Count(i => i.IsSelected);

    public string SelectedSizeLabel => InstalledProgram.FormatBytes(
        Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).Sum(i => i.Item.SizeBytes));

    public int HighConfidenceCount => Scan?.Items.Count(i => i.Confidence >= LeftoverConfidence.High) ?? 0;

    public bool HasProtectedItems => Scan?.Items.Any(i => i.IsProtected) == true;

    public string EmptyReviewText => "No leftover candidates were found. Close the window to finish.";

    public void RequestCancellation()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The workflow already finished.
        }
    }

    public void Dispose() => _cts.Dispose();

    public async Task InitializeAsync()
    {
        if (!_scanOnly)
        {
            return;
        }

        State = WorkflowState.Running;
        try
        {
            await ScanForLeftoversAsync();
        }
        catch (OperationCanceledException)
        {
            Error = "The scan was cancelled.";
            State = WorkflowState.Result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            State = WorkflowState.Result;
        }
    }

    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        Error = null;
        IsBusy = true;
        State = WorkflowState.Running;
        StatusText = _scanOnly ? "Scanning for leftovers…" : "Preparing the uninstaller…";
        try
        {
            if (!_scanOnly)
            {
                if (!Force && !IsCommandUsable)
                {
                    Error = "The vendor uninstaller is unavailable. Enable forced uninstall to remove the registration and scan for leftovers.";
                    State = WorkflowState.Confirm;
                    return;
                }

                var progress = new Progress<OperationProgress>(value => StatusText = value.Message);
                UninstallResult = await _services.Uninstall.RunAsync(
                    Program,
                    new UninstallOptions
                    {
                        PreferSilent = PreferSilent,
                        Force = Force,
                        WaitTimeout = TimeSpan.FromMinutes(30)
                    },
                    progress,
                    _cts.Token);

                if (UninstallResult.StillRegistered || UninstallResult.TimedOut)
                {
                    Error = UninstallResult.TimedOut
                        ? "The vendor uninstaller is still running. Nothing was deleted."
                        : "The application is still registered. Nothing was deleted. Use forced uninstall if the vendor uninstaller is broken.";
                    State = WorkflowState.Result;
                    return;
                }
            }

            await ScanForLeftoversAsync();
        }
        catch (OperationCanceledException)
        {
            Error = "The operation was cancelled.";
            State = WorkflowState.Result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
            State = WorkflowState.Result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanForLeftoversAsync()
    {
        StatusText = "Scanning registry and common folders…";
        var otherPrograms = await Task.Run(_services.ScanPrograms, _cts.Token);
        Scan = await _services.Leftovers.ScanAsync(
            Program,
            otherPrograms,
            new Progress<OperationProgress>(value => StatusText = value.Message),
            _cts.Token);
        ScanDiff = await Task.Run(
            () => _services.History.CompareAndSave(Scan, DateTimeOffset.UtcNow),
            _cts.Token);
        if (_services.History.LastError is { } historyError)
        {
            Scan.Warnings.Add($"Scan history could not be saved: {historyError}");
        }

        OnPropertyChanged(nameof(IgnoredCount));
        OnPropertyChanged(nameof(HasIgnoredEntries));

        Groups.Clear();
        foreach (var group in Scan.Groups)
        {
            if (!group.HasItems)
            {
                continue;
            }

            var row = new LeftoverGroupRow { Title = group.Title };
            foreach (var item in group.Items)
            {
                row.Items.Add(new LeftoverItemRow(item, OnSelectionChanged));
            }

            Groups.Add(row);
        }

        RaiseReviewState();
        State = Scan.Items.Count == 0 ? WorkflowState.Result : WorkflowState.Review;
        StatusText = Scan.UsedTrace
            ? $"Trace evidence: {Scan.TraceId}"
            : "Heuristic evidence (no install trace)";
    }

    private async Task DeleteAsync()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).Select(i => i.Item).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Creating a backup…";
        try
        {
            DeletionResult = await _services.Deletion.DeleteAsync(Program.Name, selected, dryRun: false, _cts.Token);
            State = WorkflowState.Result;
            StatusText = "Done.";
        }
        catch (OperationCanceledException)
        {
            Error = "The operation was cancelled. Any completed items remain backed up.";
            State = WorkflowState.Result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            State = WorkflowState.Result;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(ResultDetail));
        }
    }

    private void Cancel()
    {
        _cts.Cancel();
        StatusText = "Cancellation requested…";
    }

    private void SelectAll()
    {
        foreach (var item in Groups.SelectMany(g => g.Items).Where(i => !i.IsProtected))
        {
            item.IsSelected = true;
        }

        OnSelectionChanged();
    }

    private void SelectNone()
    {
        foreach (var item in Groups.SelectMany(g => g.Items))
        {
            item.IsSelected = false;
        }

        OnSelectionChanged();
    }

    private void IgnoreSelected()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).Select(i => i.Item).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var added = _services.Ignored.Add(Program.Id, Program.Name, selected, DateTimeOffset.UtcNow);
        if (_services.Ignored.LastError is { } ignoreError)
        {
            Error = $"The ignored list could not be saved: {ignoreError}";
            return;
        }

        if (Scan is not null)
        {
            Scan.IgnoredCount += added;
        }

        foreach (var group in Groups.ToList())
        {
            foreach (var row in group.Items.Where(i => i.IsSelected).ToList())
            {
                group.Items.Remove(row);
            }

            if (!group.HasItems)
            {
                Groups.Remove(group);
            }
        }

        OnPropertyChanged(nameof(IgnoredCount));
        OnPropertyChanged(nameof(HasIgnoredEntries));
        StatusText = $"{added} item(s) ignored for this application. They will be hidden from future scans until you reset the list.";
        OnSelectionChanged();
        if (Groups.Count == 0)
        {
            State = WorkflowState.Result;
        }
    }

    private async Task ResetIgnoredAsync()
    {
        var removed = _services.Ignored.Clear(Program.Id, Program.Name);
        if (_services.Ignored.LastError is { } clearError)
        {
            Error = $"The ignored list could not be updated: {clearError}";
            return;
        }

        StatusText = $"{removed} ignored item(s) restored to the review list.";
        await ScanForLeftoversAsync();
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Error = ex.Message;
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeLabel));
        OnPropertyChanged(nameof(ReviewSummary));
        IgnoreCommand.RaiseCanExecuteChanged();
        ResetIgnoredCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private void RaiseReviewState()
    {
        OnPropertyChanged(nameof(ReviewSummary));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSizeLabel));
        OnPropertyChanged(nameof(HighConfidenceCount));
        OnPropertyChanged(nameof(HasProtectedItems));
        OnPropertyChanged(nameof(IgnoredCount));
        OnPropertyChanged(nameof(HasIgnoredEntries));
        OnPropertyChanged(nameof(DiffSummary));
        OnPropertyChanged(nameof(HasScanHistory));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(ResultDetail));
    }
}
