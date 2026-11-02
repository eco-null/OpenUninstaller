using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.App.ViewModels;

public sealed class JunkCategoryRow : ObservableObject
{
    private readonly Action<JunkCategoryRow> _changed;
    private bool _isSelected;
    public JunkCategoryRow(JunkCategory category, Action<JunkCategoryRow> changed) { Category = category; _changed = changed; }
    public JunkCategory Category { get; }
    public string Id => Category.Id;
    public string Title => Category.Title;
    public string Description => Category.Description;
    public string SizeDisplay => Category.SizeDisplay;
    public string ItemLabel => $"{Category.ItemCount:N0} item(s) · {Category.SkippedItems:N0} skipped" +
        (Category.IsComplete ? string.Empty : " · Incomplete");
    public bool CanClean => Category.CanClean;
    public bool RequiresElevation => Category.RequiresElevation;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value && CanClean)) _changed(this); }
    }
    internal void ReflectSelection(bool selected) => SetProperty(ref _isSelected, selected, nameof(IsSelected));
}

public sealed class JunkFileRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;
    public JunkFileRow(JunkFile file, string category, Action changed) { File = file; Category = category; _changed = changed; }
    public JunkFile File { get; }
    public string Path => File.Path;
    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Category { get; }
    public long Bytes => File.Bytes;
    public string SizeDisplay => InstalledProgram.FormatBytes(Bytes);
    public DateTime LastWriteUtc => File.LastWriteUtc;
    public string ModifiedDisplay => LastWriteUtc.ToLocalTime().ToString("g");
    public string Reason => File.Reason;
    public bool CanClean => File.CanClean;
    public string SafetyLabel => CanClean ? "Safe location" : "Review only";
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value && CanClean)) _changed(); }
    }
}

public sealed class JunkDriveRow : ObservableObject
{
    private bool _isSelected;
    public required string Root { get; init; }
    public required string Label { get; init; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class JunkViewModel : PageViewModel, IDisposable
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private CancellationTokenSource? _cancellation;
    private List<JunkFileRow> _files = [];
    private ICollectionView _filesView = CollectionViewSource.GetDefaultView(Array.Empty<JunkFileRow>());
    private bool _dryRun, _updatingSelection;
    private bool _includeKnownLocations = true, _useLastAccessTime = true, _useDefaultExclusions = true, _useAgeFilter = true;
    private int _minimumAgeHours = 24;
    private string _folderPaths = string.Empty, _includedFolderPaths = string.Empty, _excludedPaths = string.Empty;
    private string _patterns = string.Join("; ", JunkService.JunkPatterns);
    private string _searchText = string.Empty, _warningText = string.Empty, _validationMessage = string.Empty;
    private long _totalBytes, _scanGeneration;
    private int _totalItems;

    public JunkViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsBusy && SelectedCount > 0);
        SelectAllCommand = new RelayCommand(_ => SetAllSelected(true), _ => !IsBusy && Categories.Count > 0);
        ClearSelectionCommand = new RelayCommand(_ => SetAllSelected(false), _ => !IsBusy && SelectedCount > 0);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsBusy && _cancellation is not null);
        AddFolderCommand = new RelayCommand(_ => AddFolder(), _ => !IsBusy);
        AddIncludedFolderCommand = new RelayCommand(_ => AddIncludedFolder(), _ => !IsBusy);
        ResetPatternsCommand = new RelayCommand(_ => Patterns = string.Join("; ", JunkService.JunkPatterns), _ => !IsBusy);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    Drives.Add(new JunkDriveRow { Root = drive.RootDirectory.FullName, Label = drive.Name });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public ObservableCollection<JunkCategoryRow> Categories { get; } = [];
    public ObservableCollection<JunkDriveRow> Drives { get; } = [];
    public IReadOnlyList<int> AgeOptions { get; } = [24, 48, 168];
    public ICollectionView FilesView { get => _filesView; private set => SetProperty(ref _filesView, value); }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand AddIncludedFolderCommand { get; }
    public RelayCommand ResetPatternsCommand { get; }
    public bool IncludeKnownLocations { get => _includeKnownLocations; set => SetProperty(ref _includeKnownLocations, value); }
    public bool UseLastAccessTime { get => _useLastAccessTime; set => SetProperty(ref _useLastAccessTime, value); }
    public bool UseAgeFilter { get => _useAgeFilter; set => SetProperty(ref _useAgeFilter, value); }
    public bool UseDefaultExclusions { get => _useDefaultExclusions; set => SetProperty(ref _useDefaultExclusions, value); }
    public int MinimumAgeHours { get => _minimumAgeHours; set => SetProperty(ref _minimumAgeHours, value); }
    public string FolderPaths { get => _folderPaths; set => SetProperty(ref _folderPaths, value); }
    public string IncludedFolderPaths { get => _includedFolderPaths; set => SetProperty(ref _includedFolderPaths, value); }
    public string ExcludedPaths { get => _excludedPaths; set => SetProperty(ref _excludedPaths, value); }
    public string Patterns { get => _patterns; set => SetProperty(ref _patterns, value); }
    public string ValidationMessage
    {
        get => _validationMessage;
        private set { if (SetProperty(ref _validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage)); }
    }
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public string WarningText
    {
        get => _warningText;
        private set { if (SetProperty(ref _warningText, value)) OnPropertyChanged(nameof(HasWarnings)); }
    }
    public bool HasWarnings => WarningText.Length > 0;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) { FilesView.Refresh(); OnPropertyChanged(nameof(FileCountLabel)); } }
    }
    public bool DryRun
    {
        get => _dryRun;
        set { if (SetProperty(ref _dryRun, value)) OnPropertyChanged(nameof(DryRunLabel)); }
    }
    public int SelectedCount => _files.Count(f => f.IsSelected) + Categories.Count(c => c.Id == "recycle-bin" && c.IsSelected);
    public string SelectionLabel => $"{_files.Count(f => f.IsSelected):N0} file(s) selected" +
        (Categories.Any(c => c.Id == "recycle-bin" && c.IsSelected) ? " · Recycle bin selected" : string.Empty);
    public string TotalLabel => $"{InstalledProgram.FormatBytes(_totalBytes)} · {_totalItems:N0} item(s)";
    public string FileCountLabel => $"{FilesView.Cast<object>().Count():N0} of {_files.Count:N0} file(s)";
    public bool HasFiles => _files.Count > 0;
    public string DryRunLabel => DryRun ? "Dry run: nothing will be deleted" : "Selected cache files are permanently deleted";

    // Automatic activation never starts a drive-wide scan.
    public override void Activate()
    {
        if (Categories.Count == 0 && !IsBusy) _ = ScanAsync(knownLocationsOnly: true);
    }
    public Task ScanAsync() => ScanAsync(knownLocationsOnly: false);

    private async Task ScanAsync(bool knownLocationsOnly)
    {
        if (IsBusy) return;
        ValidationMessage = string.Empty;
        JunkScanOptions options;
        try { options = BuildOptions(knownLocationsOnly); }
        catch (ArgumentException ex) { ValidationMessage = ex.Message; return; }
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        var cancellation = _cancellation;
        var generation = ++_scanGeneration;
        RaiseCommandStates();
        StatusMessage = "Scanning temporary files and caches…";
        Categories.Clear();
        SetFiles([]);
        WarningText = string.Empty;
        _totalBytes = 0;
        _totalItems = 0;
        OnPropertyChanged(nameof(TotalLabel));
        try
        {
            var progress = new Progress<JunkCategory>(category =>
            {
                if (generation != _scanGeneration || !IsBusy) return;
                Categories.Add(new JunkCategoryRow(category, CategorySelectionChanged));
                StatusMessage = $"Scanned {category.Title} · {category.ItemCount:N0} item(s)";
            });
            var scan = await Task.Run(() => _services.Junk.Scan(options, progress, cancellation.Token));
            // Final results do not depend on progress callback delivery order.
            Categories.Clear();
            foreach (var category in scan.Categories) Categories.Add(new JunkCategoryRow(category, CategorySelectionChanged));
            CompletePreview();
            StatusMessage = scan.IsComplete
                ? $"Scan complete · {TotalLabel}"
                : $"Scan incomplete · {TotalLabel}. Review warnings before cleaning.";
        }
        catch (OperationCanceledException)
        {
            CompletePreview();
            StatusMessage = $"Scan cancelled · Partial results: {TotalLabel}. Scan again to finish.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            CompletePreview();
            ValidationMessage = $"The scan could not finish: {ex.Message}";
            StatusMessage = "Partial results. Review warnings and scan again.";
        }
        finally
        {
            _cancellation = null;
            cancellation.Dispose();
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private JunkScanOptions BuildOptions(bool knownLocationsOnly)
    {
        // Page activation always uses the safe preset, independently of unsubmitted scope edits.
        if (knownLocationsOnly) return new JunkScanOptions();
        var roots = SplitLines(FolderPaths).Concat(Drives.Where(d => d.IsSelected).Select(d => d.Root)).ToArray();
        var includedRoots = SplitLines(IncludedFolderPaths);
        var includeKnown = IncludeKnownLocations;
        if (!includeKnown && roots.Length == 0 && includedRoots.Length == 0)
            throw new ArgumentException("Choose safe locations, a pattern folder, an include folder, or at least one local drive.");
        foreach (var root in roots.Concat(includedRoots))
            if (!System.IO.Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                throw new ArgumentException($"The scan folder is not an existing absolute path: {root}");
        var patterns = roots.Length == 0 ? [] : Patterns.Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (roots.Length > 0 && patterns.Length == 0) throw new ArgumentException("Enter at least one file pattern, such as *.tmp.");
        if (patterns.Any(p => p.IndexOfAny(['\\', '/', ':']) >= 0))
            throw new ArgumentException("File patterns may use * and ?; enter folders separately.");
        if (UseAgeFilter && roots.Length > 0 && MinimumAgeHours < 24)
            throw new ArgumentException("The minimum file age must be at least 24 hours.");
        return new JunkScanOptions
        {
            IncludeKnownLocations = includeKnown,
            SearchRoots = roots,
            IncludeRoots = includedRoots,
            Patterns = patterns,
            ExcludedPaths = SplitLines(ExcludedPaths),
            MinimumAge = TimeSpan.FromHours(MinimumAgeHours),
            UseLastAccessTime = UseLastAccessTime,
            UseAgeFilter = UseAgeFilter,
            UseDefaultExclusions = UseDefaultExclusions
        };
    }
    private static string[] SplitLines(string value) => value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private void CompletePreview()
    {
        _totalBytes = Categories.Sum(c => c.Category.Bytes);
        _totalItems = Categories.Sum(c => c.Category.ItemCount);
        SetFiles(Categories.SelectMany(c => c.Category.Files.Select(f => new JunkFileRow(f, c.Title, OnFileSelectionChanged))).ToList());
        WarningText = string.Join(Environment.NewLine, Categories.SelectMany(c => c.Category.Warnings.Select(w => $"{c.Title}: {w}")));
        OnPropertyChanged(nameof(TotalLabel));
    }
    private void SetFiles(List<JunkFileRow> files)
    {
        _files = files;
        FilesView = CollectionViewSource.GetDefaultView(_files);
        FilesView.Filter = value => value is JunkFileRow row && (string.IsNullOrWhiteSpace(SearchText) ||
            row.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileCountLabel));
        OnSelectionChanged();
    }

    private async Task CleanAsync()
    {
        if (IsBusy) return;
        var selectedFiles = _files.Where(f => f.IsSelected && f.CanClean).Select(f => f.File).ToList();
        var recycleBin = Categories.Any(c => c.Id == "recycle-bin" && c.IsSelected);
        if (selectedFiles.Count == 0 && !recycleBin) return;
        var dryRun = DryRun;
        if (!dryRun && !_windows.Confirm(
            $"Permanently delete {selectedFiles.Count:N0} selected cache file(s)?\n\n" +
            "Only files in this preview are processed. Changed files and files in use are skipped." +
            (recycleBin ? "\n\nThe entire Recycle bin will also be emptied, including items added since this scan." : string.Empty),
            "Clean junk files", danger: true)) return;
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        var cancellation = _cancellation;
        RaiseCommandStates();
        StatusMessage = "Cleaning selected files…";
        try
        {
            var result = await Task.Run(() =>
            {
                var filesResult = _services.Junk.CleanFiles(selectedFiles, dryRun, cancellation.Token);
                if (recycleBin)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var binResult = _services.Junk.Clean(["recycle-bin"], dryRun, cancellation.Token);
                    filesResult.FreedBytes += binResult.FreedBytes;
                    filesResult.DeletedItems += binResult.DeletedItems;
                    filesResult.SkippedItems += binResult.SkippedItems;
                    filesResult.FailedItems += binResult.FailedItems;
                    filesResult.Messages.AddRange(binResult.Messages);
                }
                return filesResult;
            });
            WarningText = string.Join(Environment.NewLine, result.Messages);
            var verb = dryRun ? "Would free" : "Freed";
            _windows.Info($"{verb} {InstalledProgram.FormatBytes(result.FreedBytes)} across {result.DeletedItems:N0} item(s).\n" +
                $"Skipped: {result.SkippedItems:N0} · Failed: {result.FailedItems:N0}" +
                (result.Messages.Count > 0 ? "\n\nSee cleanup details on the Junk files page." : string.Empty) +
                (dryRun ? "\n\nDry run: nothing was deleted." : string.Empty), "Junk files");
            StatusMessage = $"{verb} {InstalledProgram.FormatBytes(result.FreedBytes)} · Skipped: {result.SkippedItems:N0} · Failed: {result.FailedItems:N0}. Scan again to refresh.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cleanup cancelled. Some selected files may have been processed; scan again to refresh."; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        { ValidationMessage = $"Cleanup could not finish: {ex.Message}"; StatusMessage = "Scan again before another cleanup."; }
        finally
        {
            _cancellation = null;
            cancellation.Dispose();
            IsBusy = false;
            if (!dryRun) SetAllSelected(false);
            RaiseCommandStates();
        }
    }
    private void AddFolder()
    {
        var folder = _windows.PickFolder("Choose a folder to scan for temporary patterns");
        if (folder is not null) FolderPaths = string.IsNullOrWhiteSpace(FolderPaths) ? folder : FolderPaths.TrimEnd() + Environment.NewLine + folder;
    }
    private void AddIncludedFolder()
    {
        var folder = _windows.PickFolder("Include all files in a folder for review");
        if (folder is not null) IncludedFolderPaths = string.IsNullOrWhiteSpace(IncludedFolderPaths)
            ? folder : IncludedFolderPaths.TrimEnd() + Environment.NewLine + folder;
    }
    private void CategorySelectionChanged(JunkCategoryRow category)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        foreach (var file in _files.Where(f => f.File.CategoryId == category.Id && f.CanClean)) file.IsSelected = category.IsSelected;
        _updatingSelection = false;
        OnSelectionChanged();
    }
    private void OnFileSelectionChanged()
    {
        if (_updatingSelection) return;
        foreach (var category in Categories.Where(c => c.Id != "recycle-bin"))
        {
            var files = _files.Where(f => f.File.CategoryId == category.Id && f.CanClean).ToList();
            category.ReflectSelection(files.Count > 0 && files.All(f => f.IsSelected));
        }
        OnSelectionChanged();
    }
    private void SetAllSelected(bool selected)
    {
        _updatingSelection = true;
        foreach (var file in _files) file.IsSelected = selected;
        foreach (var category in Categories) category.ReflectSelection(selected && category.CanClean);
        _updatingSelection = false;
        OnSelectionChanged();
    }
    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionLabel));
        RaiseCommandStates();
    }
    private void RaiseCommandStates()
    {
        ScanCommand.RaiseCanExecuteChanged();
        CleanCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AddFolderCommand.RaiseCanExecuteChanged();
        AddIncludedFolderCommand.RaiseCanExecuteChanged();
        ResetPatternsCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        // The active operation owns and disposes its token source in its finally block.
        _cancellation?.Cancel();
        GC.SuppressFinalize(this);
    }
}
