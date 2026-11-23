using System.Collections.ObjectModel;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.ViewModels;

public sealed record ShredTargetRow(string Path, bool IsDirectory, string Detail)
{
    public string Label
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }
}

public sealed class PrivacyCategoryRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public PrivacyCategoryRow(PrivacyCategory category, Action changed)
    {
        Category = category;
        _changed = changed;
    }

    public PrivacyCategory Category { get; }
    public string Id => Category.Id;
    public string Title => OpenUninstaller.Core.Localization.UiText.Label(Category.Title);
    public string Description => OpenUninstaller.Core.Localization.UiText.Label(Category.Description);
    public string SizeDisplay => Category.SizeDisplay;
    public string ItemLabel => F("ItemS4", Category.ItemCount);
    public string? Warning => Category.Warning;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }
}

public sealed class BrowserProfileRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public BrowserProfileRow(BrowserProfile profile, Action changed)
    {
        Profile = profile;
        _changed = changed;
    }

    public BrowserProfile Profile { get; }
    public string DisplayName => Profile.DisplayName;
    public string Detail => Profile.Detail;
    public string SizeDisplay => Profile.SizeDisplay;
    public string Path => Profile.ProfilePath;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }
}

public sealed class EvidenceRemoverDriveRow : ObservableObject
{
    public EvidenceRemoverDriveRow(DriveFreeSpaceInfo drive)
    {
        Drive = drive;
    }

    public DriveFreeSpaceInfo Drive { get; }
    public string DriveLetter => Drive.DriveLetter;
    public string DisplayName => Drive.DisplayName;
    public string FreeSpaceDisplay => Drive.FreeSpaceDisplay;
    public string TotalSizeDisplay => Drive.TotalSizeDisplay;
    public string Detail => F("FreeOf", Drive.DriveLetter, Drive.VolumeLabel, Drive.FreeSpaceDisplay, Drive.TotalSizeDisplay);
    public double FreePercentage => Drive.FreePercentage;
}

public sealed class PrivacyViewModel : PageViewModel, IDisposable
{
    private static readonly int[] PassOptions = [1, 3, 7];

    private readonly AppServices _services;
    private readonly WindowService _windows;
    private int _passes = 3;
    private bool _shredDryRun;
    private bool _privacyDryRun;
    private bool _browserDryRun;
    private (long Bytes, int Count)? _privacyTotal;
    private (long Bytes, int Files, int Profiles)? _browserTotal;

    public PrivacyViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;

        AddFilesCommand = new RelayCommand(_ => AddFiles(), _ => !IsBusy);
        AddFolderCommand = new RelayCommand(_ => AddFolder(), _ => !IsBusy);
        RemoveTargetCommand = new RelayCommand(
            parameter => RemoveTarget(parameter as ShredTargetRow),
            parameter => parameter is ShredTargetRow && !IsBusy);
        ClearTargetsCommand = new RelayCommand(_ => ClearTargets(), _ => !IsBusy && ShredTargets.Count > 0);
        ShredCommand = new AsyncRelayCommand(_ => ShredAsync(), _ => !IsBusy && ShredTargets.Count > 0);
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsBusy && SelectedCount > 0);
        SelectAllCommand = new RelayCommand(_ => SetAllSelected(true), _ => !IsBusy && Categories.Count > 0);
        CleanBrowserCommand = new AsyncRelayCommand(_ => CleanBrowserAsync(), _ => !IsBusy && BrowserSelectedCount > 0);
        SelectAllBrowserCommand = new RelayCommand(_ => SetAllBrowserSelected(true), _ => !IsBusy && BrowserProfiles.Count > 0);
        WipeFreeSpaceCommand = new AsyncRelayCommand(_ => WipeFreeSpaceAsync(), _ => CanWipe);
        CancelWipeCommand = new RelayCommand(_ => CancelWipe(), _ => IsWiping);
    }

    public ObservableCollection<ShredTargetRow> ShredTargets { get; } = [];
    public ObservableCollection<PrivacyCategoryRow> Categories { get; } = [];
    public ObservableCollection<BrowserProfileRow> BrowserProfiles { get; } = [];
    public ObservableCollection<EvidenceRemoverDriveRow> Drives { get; } = [];

    private EvidenceRemoverDriveRow? _selectedDrive;
    private bool _isWiping;
    private double _wipePercent;
    private string _wipeProgressStatus = string.Empty;
    private string _wipeEtaStatus = string.Empty;
    private CancellationTokenSource? _wipeCts;

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveTargetCommand { get; }
    public RelayCommand ClearTargetsCommand { get; }
    public AsyncRelayCommand ShredCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public AsyncRelayCommand CleanBrowserCommand { get; }
    public RelayCommand SelectAllBrowserCommand { get; }
    public AsyncRelayCommand WipeFreeSpaceCommand { get; }
    public RelayCommand CancelWipeCommand { get; }

    public IReadOnlyList<int> PassesOptions => PassOptions;

    public int Passes
    {
        get => _passes;
        set => SetProperty(ref _passes, Math.Clamp(value, 1, 7));
    }

    public bool ShredDryRun
    {
        get => _shredDryRun;
        set => SetProperty(ref _shredDryRun, value);
    }

    public bool PrivacyDryRun
    {
        get => _privacyDryRun;
        set => SetProperty(ref _privacyDryRun, value);
    }

    public bool BrowserDryRun
    {
        get => _browserDryRun;
        set => SetProperty(ref _browserDryRun, value);
    }

    public string ShredTargetLabel => ShredTargets.Count == 0
        ? L("NoFilesOrFoldersSelected")
        : F("TargetSSelected", ShredTargets.Count);

    public string ShredDetailLabel
    {
        get
        {
            if (ShredTargets.Count == 0)
            {
                return L("AddFilesOrAFolderToOverwriteThem");
            }

            var files = ShredTargets.Count(t => !t.IsDirectory);
            var folders = ShredTargets.Count - files;
            return F("FileSFolderSOverwritePassEs", files, folders, Passes);
        }
    }

    public string PrivacyTotalLabel => _privacyTotal is { } total
        ? F("ItemS3", InstalledProgram.FormatBytes(total.Bytes), total.Count)
        : L("NotScannedYet");

    public string BrowserTotalLabel => _browserTotal switch
    {
        null => L("NotScannedYet"),
        { Files: 0 } => L("NoBrowserHistoryOrCookieFilesFound"),
        { } total => F("FileSInProfileS", InstalledProgram.FormatBytes(total.Bytes), total.Files, total.Profiles)
    };

    public int SelectedCount => Categories.Count(c => c.IsSelected);
    public string SelectedLabel => SelectedCount == 0
        ? L("NoPrivacyCategoriesSelected")
        : F("CategoryIesSelected", SelectedCount);

    public int BrowserSelectedCount => BrowserProfiles.Count(p => p.IsSelected);
    public string BrowserSelectedLabel => BrowserSelectedCount == 0
        ? L("NoBrowserProfilesSelected")
        : F("ProfileSSelected", BrowserSelectedCount);

    public EvidenceRemoverDriveRow? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
            {
                WipeFreeSpaceCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanWipe));
            }
        }
    }

    public bool IsWiping
    {
        get => _isWiping;
        private set
        {
            if (SetProperty(ref _isWiping, value))
            {
                WipeFreeSpaceCommand.RaiseCanExecuteChanged();
                CancelWipeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanWipe));
            }
        }
    }

    public bool CanWipe => !IsWiping && SelectedDrive is not null && !IsBusy;

    public double WipePercent
    {
        get => _wipePercent;
        private set => SetProperty(ref _wipePercent, value);
    }

    public string WipeProgressStatus
    {
        get => _wipeProgressStatus;
        private set => SetProperty(ref _wipeProgressStatus, value);
    }

    public string WipeEtaStatus
    {
        get => _wipeEtaStatus;
        private set => SetProperty(ref _wipeEtaStatus, value);
    }

    public override void Activate() => _ = ScanAsync();

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("ReadingWindowsHistoryAndAppState");
        try
        {
            Drives.Clear();
            var availableDrives = await Task.Run(() => _services.EvidenceRemover.GetAvailableDrives());
            foreach (var d in availableDrives)
            {
                Drives.Add(new EvidenceRemoverDriveRow(d));
            }
            if (SelectedDrive is null && Drives.Count > 0)
            {
                SelectedDrive = Drives[0];
            }

            Categories.Clear();
            var scan = await Task.Run(() => _services.Privacy.Scan());
            foreach (var category in scan.Categories)
            {
                Categories.Add(new PrivacyCategoryRow(category, OnSelectionChanged));
            }

            _privacyTotal = (scan.TotalBytes, scan.TotalItems);
            OnPropertyChanged(nameof(PrivacyTotalLabel));

            BrowserProfiles.Clear();
            var browsers = await Task.Run(() => _services.Browser.Scan());
            foreach (var profile in browsers.Profiles)
            {
                BrowserProfiles.Add(new BrowserProfileRow(profile, OnBrowserSelectionChanged));
            }

            _browserTotal = (browsers.TotalBytes, browsers.TotalFiles, browsers.Profiles.Count);
            OnPropertyChanged(nameof(BrowserTotalLabel));
            var foundHistory = scan.Categories.Any(c => c.ItemCount != 0) || BrowserProfiles.Count != 0;
            SetStatus(() => !foundHistory
                ? L("NoHistoryTracesWereFound")
                : $"{PrivacyTotalLabel} · {BrowserTotalLabel}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _windows.Error(F("ThePrivacyScanFailed2", ex.Message));
            SetStatus("ThePrivacyScanFailed");
        }
        finally
        {
            IsBusy = false;
            OnSelectionChanged();
        }
    }

    private void AddFiles()
    {
        foreach (var file in _windows.PickFiles(L("SelectFilesToShred")))
        {
            AddTarget(file, isDirectory: false);
        }
    }

    private void AddFolder()
    {
        var folder = _windows.PickFolder(L("SelectAFolderToShred"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            AddTarget(folder, isDirectory: true);
        }
    }

    private void AddTarget(string path, bool isDirectory)
    {
        var full = Path.GetFullPath(path);
        if (ShredTargets.Any(t => string.Equals(t.Path, full, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string detail;
        if (isDirectory)
        {
            detail = L("Folder");
        }
        else
        {
            try
            {
                detail = InstalledProgram.FormatBytes(new FileInfo(full).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detail = L("File");
            }
        }

        ShredTargets.Add(new ShredTargetRow(full, isDirectory, detail));
        NotifyTargetsChanged();
    }

    private void RemoveTarget(ShredTargetRow? target)
    {
        if (target is null)
        {
            return;
        }

        ShredTargets.Remove(target);
        NotifyTargetsChanged();
    }

    private void ClearTargets()
    {
        ShredTargets.Clear();
        NotifyTargetsChanged();
    }

    private async Task ShredAsync()
    {
        var paths = ShredTargets.Select(t => t.Path).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var dryRun = ShredDryRun;
        var passes = Passes;
        if (!dryRun)
        {
            var warning = string.Join(
                Environment.NewLine,
                paths.Take(12).Select(p => "• " + p)) + (paths.Count > 12 ? $"{Environment.NewLine}• …" : string.Empty);
            var confirmation = _windows.PromptExact(
                L("ShredPermanently"),
                L("ThisCannotBeUndoneFilesAreOverwrittenTruncated") +
                L("SSDWearLevellingAndFilesystemJournalsMayStill") +
                warning + L("TypeSHREDToContinue"),
                "SHRED");
            if (confirmation is null)
            {
                return;
            }
        }

        IsBusy = true;
        SetStatus(dryRun ? "CalculatingTheShredPlan" : "OverwritingAndDeleting");
        try
        {
            var result = await Task.Run(() =>
                _services.Shredder.Shred(paths, passes, dryRun));
            var message = F(dryRun ? "ShredPreviewSummary" : "ShredResultSummary", result.ShreddedFiles,
                              InstalledProgram.FormatBytes(result.ShreddedBytes)) +
                          (result.DeletedDirectories > 0 ? F("RemovedFolderS", result.DeletedDirectories) : string.Empty) +
                          "\n" + F("ShredResultCounts", result.Passes, result.Skipped.Count, result.Failed.Count);
            if (result.Failed.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Failed.Take(8));
            }

            if (result.Rejected.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Rejected.Take(8));
            }

            if (dryRun)
            {
                message += "\n\n" + L("DryRunNothingWasDeleted");
            }

            if (result.Failed.Count == 0 && result.Rejected.Count == 0)
            {
                _windows.Info(message, L("ShredFiles"));
            }
            else
            {
                _windows.Error(message, L("ShredFiles"));
            }

            if (!dryRun)
            {
                ClearTargets();
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("ShreddingWasCancelled");
        }
        finally
        {
            IsBusy = false;
            NotifyTargetsChanged();
        }
    }

    private async Task CleanAsync()
    {
        var selected = Categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var dryRun = PrivacyDryRun;
        if (!dryRun)
        {
            var names = string.Join(Environment.NewLine, selected.Select(c => "• " + c.Title));
            var confirmation = _windows.PromptExact(
                L("CleanWindowsHistory"),
                L("TheseHistoryEntriesAreDeletedWithoutABackup") +
                names + L("TypeCLEANToContinue"),
                "CLEAN");
            if (confirmation is null)
            {
                return;
            }
        }

        IsBusy = true;
        SetStatus(dryRun ? "CalculatingWhatWouldBeRemoved" : "CleaningHistory");
        try
        {
            var ids = selected.Select(c => c.Id).ToList();
            var result = await Task.Run(() => _services.Privacy.Clean(ids, dryRun));
            var message = F(dryRun ? "WouldRemovePrivacyItems" : "RemovedPrivacyItems",
                              result.ItemsDeleted, InstalledProgram.FormatBytes(result.Bytes)) +
                          F("Failed2", Environment.NewLine, result.FailedItems);
            if (result.Messages.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(10));
            }

            if (dryRun)
            {
                message += "\n\n" + L("DryRunNothingWasDeleted");
            }

            if (result.Succeeded || dryRun)
            {
                _windows.Info(message, L("Privacy"));
            }
            else
            {
                _windows.Error(message, L("Privacy"));
            }
        }
        finally
        {
            IsBusy = false;
        }

        await ScanAsync();
    }

    private async Task CleanBrowserAsync()
    {
        var selected = BrowserProfiles.Where(p => p.IsSelected).Select(p => p.Profile).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var dryRun = BrowserDryRun;
        if (!dryRun)
        {
            var names = string.Join(Environment.NewLine, selected.Select(p => "• " + p.DisplayName));
            var confirmation = _windows.PromptExact(
                L("RemoveBrowserHistoryAndCookies"),
                L("TheseProfileDatabasesAreDeletedWithoutABackup") +
                L("ABrowserThatIsRunningIsSkippedPasswords") +
                names + L("TypeCLEANToContinue"),
                "CLEAN");
            if (confirmation is null)
            {
                return;
            }
        }

        IsBusy = true;
        SetStatus(dryRun ? "CalculatingBrowserCleanup" : "RemovingBrowserHistoryAndCookies");
        try
        {
            var result = await Task.Run(() => _services.Browser.Clean(selected, dryRun));
            var message = result.Summary;
            if (result.Skipped > 0)
            {
                message += "\n" + F("SkippedBrowserFiles", result.Skipped);
            }

            if (result.Messages.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(10));
            }

            if (dryRun)
            {
                message += "\n\n" + L("DryRunNothingWasDeleted");
            }

            if (result.Messages.Count == 0 || dryRun)
            {
                _windows.Info(message, L("BrowserData"));
            }
            else
            {
                _windows.Error(message, L("BrowserData"));
            }
        }
        finally
        {
            IsBusy = false;
        }

        await ScanAsync();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var category in Categories)
        {
            category.IsSelected = selected;
        }

        OnSelectionChanged();
    }

    private void SetAllBrowserSelected(bool selected)
    {
        foreach (var profile in BrowserProfiles)
        {
            profile.IsSelected = selected;
        }

        OnBrowserSelectionChanged();
    }

    private void OnBrowserSelectionChanged()
    {
        OnPropertyChanged(nameof(BrowserSelectedCount));
        OnPropertyChanged(nameof(BrowserSelectedLabel));
        CleanBrowserCommand.RaiseCanExecuteChanged();
        SelectAllBrowserCommand.RaiseCanExecuteChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedLabel));
        CleanCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
    }

    private void NotifyTargetsChanged()
    {
        OnPropertyChanged(nameof(ShredTargetLabel));
        OnPropertyChanged(nameof(ShredDetailLabel));
        AddFilesCommand.RaiseCanExecuteChanged();
        AddFolderCommand.RaiseCanExecuteChanged();
        RemoveTargetCommand.RaiseCanExecuteChanged();
        ClearTargetsCommand.RaiseCanExecuteChanged();
        ShredCommand.RaiseCanExecuteChanged();
    }

    private async Task WipeFreeSpaceAsync()
    {
        if (SelectedDrive is null || IsWiping)
        {
            return;
        }

        var drive = SelectedDrive.Drive;
        var confirmed = _windows.Confirm(
            F("WipeUnallocatedFreeSpaceOn", drive.DisplayName, drive.FreeSpaceDisplay) +
            L("WipeFreeSpaceExplanation") + "\n\n" +
            L("DoYouWantToProceed"),
            L("EvidenceRemoverWipeFreeSpace"),
            danger: true);

        if (!confirmed)
        {
            return;
        }

        IsWiping = true;
        WipePercent = 0;
        WipeProgressStatus = F("InitializingFreeSpaceWipeOn", drive.DisplayName);
        WipeEtaStatus = string.Empty;
        _wipeCts = new CancellationTokenSource();

        var progress = new Progress<WipeProgress>(p =>
        {
            WipePercent = p.PercentComplete;
            WipeProgressStatus = F("OfWiped", p.PercentComplete, InstalledProgram.FormatBytes(p.BytesWiped), InstalledProgram.FormatBytes(p.TotalBytesToWipe));
            WipeEtaStatus = p.EstimatedTimeRemaining.HasValue
                ? F("ETA", p.EstimatedTimeRemaining.Value)
                : string.Empty;
        });

        try
        {
            var result = await _services.EvidenceRemover.WipeDriveAsync(drive.DriveName, progress, _wipeCts.Token);
            if (result.Succeeded)
            {
                var formatted = InstalledProgram.FormatBytes(result.TotalBytesWiped);
                _windows.Info(F("FreeSpaceWipeCompleteSuccessfullyWipedIn", formatted, result.Duration), L("EvidenceRemover"));
                WipeProgressStatus = L("FreeSpaceWipeComplete");
            }
            else
            {
                _windows.Error(F("FreeSpaceWipeStoppedOrFailed", result.ErrorMessage));
                WipeProgressStatus = F("Failed", result.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            _windows.Info(L("FreeSpaceWipeWasCancelledTemporaryWipeFiles"), L("EvidenceRemover"));
            WipeProgressStatus = L("WipeCancelled");
        }
        finally
        {
            _wipeCts?.Dispose();
            _wipeCts = null;
            IsWiping = false;
            WipeEtaStatus = string.Empty;
            _ = ScanAsync();
        }
    }

    private void CancelWipe()
    {
        if (_wipeCts is { IsCancellationRequested: false })
        {
            WipeProgressStatus = L("CancellingWipeAndCleaningUpTemporaryFiles");
            _wipeCts.Cancel();
        }
    }

    public void Dispose()
    {
        _wipeCts?.Cancel();
        _wipeCts?.Dispose();
        _wipeCts = null;
    }
}
