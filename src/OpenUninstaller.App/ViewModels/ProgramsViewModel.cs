using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.ViewModels;

public sealed class ProgramRow : ObservableObject
{
    private bool _isQueued;

    public ProgramRow(InstalledProgram program, ImageSource? icon)
    {
        Program = program;
        Icon = icon;
    }

    public InstalledProgram Program { get; }
    public ImageSource? Icon { get; }

    public bool IsQueued
    {
        get => _isQueued;
        set => SetProperty(ref _isQueued, value);
    }

    public string Name => Program.Name;
    public string PublisherDisplay => Program.Publisher ?? "—";
    public string VersionDisplay => Program.Version ?? "—";
    public string SizeDisplay => Program.SizeDisplay;
    public bool HasSize => Program.EstimatedSizeBytes is > 0;
    public bool HasInstallDate => Program.InstallDate.HasValue;
    public string InstallDateDisplay => Program.InstallDate?.ToString("yyyy-MM-dd") ?? "—";
    public string ArchitectureDisplay => Program.ArchitectureDisplay;
    public string LocationDisplay => Program.InstallLocation ?? L("NoInstallLocationRecorded");
    public string UninstallDisplay => Program.UninstallCommand ?? L("NoUninstallCommandRegistered");
    public string RegistryDisplay => Program.RegistryPath;
    public bool IsNew => Program.IsNew;
    public bool IsLarge => Program.IsLarge;
}

public sealed class ProgramsViewModel : PageViewModel
{
    public static readonly string[] FilterOptions =
    [
        "All programs",
        "New programs (≤ 7 days)",
        "Large programs (≥ 500 MB)",
        "32-bit programs",
        "64-bit programs"
    ];

    private static readonly JsonSerializerOptions ExportJsonOptions = new() { WriteIndented = true };
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private readonly ObservableCollection<ProgramRow> _rows = [];
    private ProgramRow? _selected;
    private string _searchText = string.Empty;
    private string _selectedFilter = "All programs";
    private bool _includeSystemComponents;
    private int _programCount;

    public ProgramsViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        RowsView = CollectionViewSource.GetDefaultView(_rows);
        RowsView.Filter = FilterRow;
        Sort = new ResultSort(RowsView, nameof(ProgramRow.Name),
            new("Name", nameof(ProgramRow.Name)),
            new("Publisher", "Program.Publisher"),
            new("Size", "Program.EstimatedSizeBytes", "Smallest first", "Largest first", nameof(ProgramRow.HasSize)),
            new("Installed", "Program.InstallDate", "Oldest first", "Newest first", nameof(ProgramRow.HasInstallDate)),
            new("Version", nameof(ProgramRow.VersionDisplay)),
            new("Type", nameof(ProgramRow.ArchitectureDisplay)));

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        UninstallCommand = new RelayCommand(_ => OpenUninstall(force: false), _ => CanActOnSelection);
        ForceUninstallCommand = new RelayCommand(_ => OpenUninstall(force: true), _ => CanActOnSelection);
        QueueUninstallCommand = new AsyncRelayCommand(_ => RunQueueAsync(), _ => !IsBusy && QueuedCount > 0);
        ClearQueueCommand = new RelayCommand(_ => ClearQueue(), _ => !IsBusy && QueuedCount > 0);
        ScanCommand = new RelayCommand(_ => OpenScan(), _ => CanActOnSelection);
        TraceInstallCommand = new AsyncRelayCommand(_ => TraceInstallAsync(), _ => !IsBusy);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => !IsBusy);
        HunterCommand = new RelayCommand(_ => RunHunter(), _ => !IsBusy);
        OrphanScanCommand = new RelayCommand(_ => OpenOrphanScan(), _ => !IsBusy);
        ToggleSystemCommand = new RelayCommand(_ => IncludeSystemComponents = !IncludeSystemComponents, _ => !IsBusy);
    }

    public ICollectionView RowsView { get; }
    public ResultSort Sort { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand ForceUninstallCommand { get; }
    public AsyncRelayCommand QueueUninstallCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public RelayCommand ScanCommand { get; }
    public AsyncRelayCommand TraceInstallCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand HunterCommand { get; }
    public RelayCommand OrphanScanCommand { get; }
    public RelayCommand ToggleSystemCommand { get; }

    public ProgramRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedProgram));
                RaiseCommandStates();
            }
        }
    }

    public InstalledProgram? SelectedProgram => _selected?.Program;
    public bool HasSelection => _selected is not null;
    public bool CanActOnSelection => _selected is not null && !IsBusy;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RowsView.Refresh();
                OnPropertyChanged(nameof(VisibleCount));
            }
        }
    }

    public bool IncludeSystemComponents
    {
        get => _includeSystemComponents;
        set
        {
            if (SetProperty(ref _includeSystemComponents, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string[] AvailableFilters => FilterOptions;

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                RowsView.Refresh();
                OnPropertyChanged(nameof(VisibleCount));
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    public int ProgramCount
    {
        get => _programCount;
        private set
        {
            if (SetProperty(ref _programCount, value))
            {
                OnPropertyChanged(nameof(CountLabel));
                OnPropertyChanged(nameof(VisibleCount));
            }
        }
    }

    public int VisibleCount => RowsView?.Cast<object>().Count() ?? 0;

    public string CountLabel => VisibleCount == ProgramCount
        ? F("Applications", ProgramCount)
        : F("OfApplications", VisibleCount, ProgramCount);

    public int QueuedCount => _rows.Count(r => r.IsQueued);
    public bool HasQueue => QueuedCount > 0;
    public string QueueActionLabel => QueuedCount == 0 ? L("UninstallQueued2") : F("UninstallQueued", QueuedCount);
    public string QueueSummary => QueuedCount == 0
        ? L("TickQueueInTheListToReviewSeveral")
        : F("ApplicationSQueuedEachKeepsItsOwnConfirmation", QueuedCount);

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("ScanningInstalledApplications");
        RaiseCommandStates();
        try
        {
            var queuedIds = _rows.Where(r => r.IsQueued).Select(r => r.Program.Id).ToHashSet(StringComparer.Ordinal);
            var programs = await Task.Run(() => _services.Scanner.Scan(IncludeSystemComponents));
            _rows.Clear();
            foreach (var program in programs)
            {
                var iconPath = ResolveIconPath(program);
                var row = new ProgramRow(program, IconLoader.TryLoad(iconPath))
                {
                    IsQueued = queuedIds.Contains(program.Id)
                };
                row.PropertyChanged += OnRowPropertyChanged;
                _rows.Add(row);
            }

            ProgramCount = _rows.Count;
            RowsView.Refresh();
            OnPropertyChanged(nameof(VisibleCount));
            SetStatus("ApplicationsRefreshed", ProgramCount, DateTime.Now);
            RaiseQueueState();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            SetStatus("TheInventoryCouldNotBeReadCompletely");
            _windows.Error(F("SomeRegistryEntriesCouldNotBeRead", ex.Message));
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    public override void Activate() => _ = RefreshAsync();

    private bool FilterRow(object item)
    {
        if (item is not ProgramRow row)
        {
            return false;
        }

        if (!string.Equals(SelectedFilter, "All programs", StringComparison.OrdinalIgnoreCase))
        {
            if (SelectedFilter.StartsWith("New programs", StringComparison.OrdinalIgnoreCase) && !row.IsNew)
            {
                return false;
            }

            if (SelectedFilter.StartsWith("Large programs", StringComparison.OrdinalIgnoreCase) && !row.IsLarge)
            {
                return false;
            }

            if (SelectedFilter.StartsWith("32-bit", StringComparison.OrdinalIgnoreCase) &&
                row.Program.Architecture != ProgramArchitecture.X86)
            {
                return false;
            }

            if (SelectedFilter.StartsWith("64-bit", StringComparison.OrdinalIgnoreCase) &&
                row.Program.Architecture != ProgramArchitecture.X64)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.PublisherDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.LocationDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenUninstall(bool force)
    {
        if (_selected is null)
        {
            return;
        }

        var performDeletion = _windows.ShowUninstall(
            _selected.Program,
            force,
            _services.Settings.PreferSilentUninstall);
        if (performDeletion)
        {
            _ = RefreshAsync();
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProgramRow.IsQueued))
        {
            RaiseQueueState();
        }
    }

    private async Task RunQueueAsync()
    {
        var queued = _rows.Where(r => r.IsQueued).ToList();
        if (queued.Count == 0)
        {
            return;
        }

        var preview = string.Join(Environment.NewLine, queued.Take(12).Select(r => $"• {r.Name}"));
        if (queued.Count > 12)
        {
            preview += Environment.NewLine + F("AndMore", queued.Count - 12);
        }

        if (!_windows.Confirm(
                F("ReviewQueuedApplicationSOneAfterAnother", queued.Count, preview) +
                L("EachApplicationStillOpensItsOwnConfirmationAnd"),
                L("BatchUninstallQueue")))
        {
            return;
        }

        IsBusy = true;
        RaiseCommandStates();
        var reviewed = 0;
        try
        {
            foreach (var row in queued)
            {
                SetStatus("QueueOf", reviewed + 1, queued.Count, row.Name);
                _windows.ShowUninstall(
                    row.Program,
                    force: false,
                    silent: _services.Settings.PreferSilentUninstall);
                row.IsQueued = false;
                reviewed++;
            }
        }
        finally
        {
            IsBusy = false;
        }

        SetStatus("QueueFinishedApplicationSReviewed", reviewed);
        RaiseQueueState();
        await RefreshAsync();
    }

    private void ClearQueue()
    {
        foreach (var row in _rows.Where(r => r.IsQueued))
        {
            row.IsQueued = false;
        }

        RaiseQueueState();
    }

    private void RaiseQueueState()
    {
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueActionLabel));
        OnPropertyChanged(nameof(QueueSummary));
        QueueUninstallCommand.RaiseCanExecuteChanged();
        ClearQueueCommand.RaiseCanExecuteChanged();
    }

    private void OpenScan()
    {
        if (_selected is null)
        {
            return;
        }

        _windows.ShowUninstall(
            _selected.Program,
            force: false,
            silent: _services.Settings.PreferSilentUninstall,
            scanOnly: true);
    }

    private async Task TraceInstallAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = L("SelectAnInstallerToTrace"),
            Filter = $"{L("InstallerFiles")}|*.exe;*.msi|{L("AllFiles")}|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var name = _windows.Prompt(
            L("TraceAnInstall"),
            L("ProgramNameUsedToMatchTheTraceLater"),
            Path.GetFileNameWithoutExtension(dialog.FileName));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        IsBusy = true;
        SetStatus("PreparingSnapshot");
        RaiseCommandStates();
        var progress = new Progress<OperationProgress>(value => SetStatus(() => value.Message));
        try
        {
            var trace = await _services.TraceInstaller.TraceAsync(
                dialog.FileName,
                name,
                waitTimeout: TimeSpan.FromMinutes(30),
                progress: progress);
            _windows.Info(
                F("TraceSavedProgramRegistryEntriesFiles", trace.ProgramName, trace.KeyCount, trace.FileCount) +
                L("RunItAgainWhenYouUninstallTheApplication"),
                L("TraceComplete"));
            SetStatus("TraceSavedFor", trace.ProgramName);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _windows.Error(F("TheTraceCouldNotBeCompleted", ex.Message));
            SetStatus("TraceFailed");
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = L("ExportTheApplicationInventory"),
            Filter = "CSV|*.csv|JSON|*.json",
            FileName = $"openuninstaller-inventory-{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var rows = _rows.ToList();
            if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var payload = rows.Select(row => new
                {
                    name = row.Name,
                    version = row.Program.Version,
                    publisher = row.Program.Publisher,
                    architecture = row.ArchitectureDisplay,
                    sizeBytes = row.Program.EstimatedSizeBytes,
                    installDate = row.Program.InstallDate?.ToString("yyyy-MM-dd"),
                    installLocation = row.Program.InstallLocation,
                    uninstallCommand = row.Program.UninstallCommand,
                    registryPath = row.RegistryDisplay
                });
                await File.WriteAllTextAsync(
                    dialog.FileName,
                    JsonSerializer.Serialize(payload, ExportJsonOptions));
            }
            else
            {
                var builder = new StringBuilder();
                builder.AppendLine("Name,Version,Publisher,Architecture,SizeBytes,InstallDate,InstallLocation,UninstallCommand,RegistryPath");
                foreach (var row in rows)
                {
                    var values = new[]
                    {
                        row.Name,
                        row.Program.Version,
                        row.Program.Publisher,
                        row.ArchitectureDisplay,
                        row.Program.EstimatedSizeBytes?.ToString(),
                        row.Program.InstallDate?.ToString("yyyy-MM-dd"),
                        row.Program.InstallLocation,
                        row.Program.UninstallCommand,
                        row.RegistryDisplay
                    };
                    builder.AppendLine(string.Join(",", values.Select(Csv)));
                }

                await File.WriteAllTextAsync(dialog.FileName, builder.ToString());
            }

            SetStatus("ExportedApplications", rows.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _windows.Error(F("TheExportFailed", ex.Message));
        }
    }

    private void OpenOrphanScan()
        => _windows.ShowUninstall(
            LeftoverScanner.OrphanScanTarget,
            force: false,
            silent: false,
            scanOnly: true);

    private void RunHunter()
    {
        var programs = _rows.Select(row => row.Program).ToList();
        _windows.ShowHunter(programs);
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        ForceUninstallCommand.RaiseCanExecuteChanged();
        QueueUninstallCommand.RaiseCanExecuteChanged();
        ClearQueueCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        TraceInstallCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        ToggleSystemCommand.RaiseCanExecuteChanged();
        OrphanScanCommand.RaiseCanExecuteChanged();
    }

    private static string? ResolveIconPath(InstalledProgram program)
    {
        var icon = program.DisplayIcon;
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var path = icon.Trim().Trim('"');
            var comma = path.LastIndexOf(',');
            if (comma > 2 && int.TryParse(path[(comma + 1)..], out _))
            {
                path = path[..comma];
            }

            if (File.Exists(path))
            {
                return path;
            }
        }

        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            var exe = Directory.Exists(program.InstallLocation)
                ? Directory.EnumerateFiles(program.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
            if (exe is not null)
            {
                return exe;
            }
        }

        return null;
    }

    private static string Csv(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Contains('"') || value.Contains(',') || value.Contains('\n')
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : value;
}
