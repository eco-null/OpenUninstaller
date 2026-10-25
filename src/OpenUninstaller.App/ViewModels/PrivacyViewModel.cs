using System.Collections.ObjectModel;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;

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
    public string Title => Category.Title;
    public string Description => Category.Description;
    public string SizeDisplay => Category.SizeDisplay;
    public string ItemLabel => $"{Category.ItemCount} item(s)";
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
    public string Detail => $"{Drive.DriveLetter} ({Drive.VolumeLabel}) - {Drive.FreeSpaceDisplay} free of {Drive.TotalSizeDisplay}";
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
    private string _privacyTotalLabel = "Not scanned yet";
    private string _browserTotalLabel = "Not scanned yet";

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
        ? "No files or folders selected"
        : $"{ShredTargets.Count} target(s) selected";

    public string ShredDetailLabel
    {
        get
        {
            if (ShredTargets.Count == 0)
            {
                return "Add files or a folder to overwrite them permanently.";
            }

            var files = ShredTargets.Count(t => !t.IsDirectory);
            var folders = ShredTargets.Count - files;
            return $"{files} file(s) · {folders} folder(s) · {Passes} overwrite pass(es)";
        }
    }

    public string PrivacyTotalLabel
    {
        get => _privacyTotalLabel;
        private set => SetProperty(ref _privacyTotalLabel, value);
    }

    public string BrowserTotalLabel
    {
        get => _browserTotalLabel;
        private set => SetProperty(ref _browserTotalLabel, value);
    }

    public int SelectedCount => Categories.Count(c => c.IsSelected);
    public string SelectedLabel => SelectedCount == 0
        ? "No privacy categories selected"
        : $"{SelectedCount} category(ies) selected";

    public int BrowserSelectedCount => BrowserProfiles.Count(p => p.IsSelected);
    public string BrowserSelectedLabel => BrowserSelectedCount == 0
        ? "No browser profiles selected"
        : $"{BrowserSelectedCount} profile(s) selected";

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
        StatusMessage = "Reading Windows history and app state…";
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

            PrivacyTotalLabel = $"{InstalledProgram.FormatBytes(scan.TotalBytes)} · {scan.TotalItems} item(s)";

            BrowserProfiles.Clear();
            var browsers = await Task.Run(() => _services.Browser.Scan());
            foreach (var profile in browsers.Profiles)
            {
                BrowserProfiles.Add(new BrowserProfileRow(profile, OnBrowserSelectionChanged));
            }

            BrowserTotalLabel = browsers.TotalFiles == 0
                ? "No browser history or cookie files found"
                : $"{InstalledProgram.FormatBytes(browsers.TotalBytes)} · {browsers.TotalFiles} file(s) in {browsers.Profiles.Count} profile(s)";
            StatusMessage = scan.Categories.Sum(c => c.ItemCount) == 0 && BrowserProfiles.Count == 0
                ? "No history traces were found."
                : $"{PrivacyTotalLabel} · {BrowserTotalLabel}";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _windows.Error($"The privacy scan failed:\n{ex.Message}");
            StatusMessage = "The privacy scan failed.";
        }
        finally
        {
            IsBusy = false;
            OnSelectionChanged();
        }
    }

    private void AddFiles()
    {
        foreach (var file in _windows.PickFiles("Select files to shred"))
        {
            AddTarget(file, isDirectory: false);
        }
    }

    private void AddFolder()
    {
        var folder = _windows.PickFolder("Select a folder to shred");
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
            detail = "Folder";
        }
        else
        {
            try
            {
                detail = InstalledProgram.FormatBytes(new FileInfo(full).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detail = "File";
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

        if (!ShredDryRun)
        {
            var warning = string.Join(
                Environment.NewLine,
                paths.Take(12).Select(p => "• " + p)) + (paths.Count > 12 ? $"{Environment.NewLine}• …" : string.Empty);
            var confirmation = _windows.PromptExact(
                "Shred permanently",
                "This cannot be undone. Files are overwritten, truncated and deleted; the recycle bin is not used.\n" +
                "SSD wear-levelling and filesystem journals may still keep copies.\n\n" +
                warning + "\n\nType SHRED to continue:",
                "SHRED");
            if (confirmation is null)
            {
                return;
            }
        }

        IsBusy = true;
        StatusMessage = ShredDryRun ? "Calculating the shred plan…" : "Overwriting and deleting…";
        try
        {
            var result = await Task.Run(() =>
                _services.Shredder.Shred(paths, Passes, ShredDryRun));
            var verb = ShredDryRun ? "Would shred" : "Shredded";
            var message = $"{verb} {result.ShreddedFiles} file(s), {InstalledProgram.FormatBytes(result.ShreddedBytes)}" +
                          (result.DeletedDirectories > 0 ? $", removed {result.DeletedDirectories} folder(s)" : string.Empty) +
                          $".{Environment.NewLine}Passes: {result.Passes} · Skipped: {result.Skipped.Count} · Failed: {result.Failed.Count}";
            if (result.Failed.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Failed.Take(8));
            }

            if (result.Rejected.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Rejected.Take(8));
            }

            if (ShredDryRun)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Dry run: nothing was deleted.";
            }

            if (result.Failed.Count == 0 && result.Rejected.Count == 0)
            {
                _windows.Info(message, "Shred files");
            }
            else
            {
                _windows.Error(message, "Shred files");
            }

            if (!ShredDryRun)
            {
                ClearTargets();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Shredding was cancelled.";
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

        if (!PrivacyDryRun)
        {
            var names = string.Join(Environment.NewLine, selected.Select(c => "• " + c.Title));
            var confirmation = _windows.PromptExact(
                "Clean Windows history",
                "These history entries are deleted without a backup so the privacy data is not copied elsewhere.\n\n" +
                names + "\n\nType CLEAN to continue:",
                "CLEAN");
            if (confirmation is null)
            {
                return;
            }
        }

        IsBusy = true;
        StatusMessage = PrivacyDryRun ? "Calculating what would be removed…" : "Cleaning history…";
        try
        {
            var ids = selected.Select(c => c.Id).ToList();
            var result = await Task.Run(() => _services.Privacy.Clean(ids, PrivacyDryRun));
            var message = $"{(PrivacyDryRun ? "Would remove" : "Removed")} {result.ItemsDeleted} item(s), " +
                          $"{InstalledProgram.FormatBytes(result.Bytes)}." +
                          $"{Environment.NewLine}Failed: {result.FailedItems}";
            if (result.Messages.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(10));
            }

            if (PrivacyDryRun)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Dry run: nothing was deleted.";
            }

            if (result.Succeeded || PrivacyDryRun)
            {
                _windows.Info(message, "Privacy");
            }
            else
            {
                _windows.Error(message, "Privacy");
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

        if (!BrowserDryRun)
        {
            var names = string.Join(Environment.NewLine, selected.Select(p => "• " + p.DisplayName));
            var confirmation = _windows.PromptExact(
                "Remove browser history and cookies",
                "These profile databases are deleted without a backup so the browsing history is not copied elsewhere.\n" +
                "A browser that is running is skipped. Passwords, bookmarks, autofill and extensions are never touched.\n\n" +
                names + "\n\nType CLEAN to continue:",
                "CLEAN");
            if (confirmation is null)
            {
                return;
            }
        }

        IsBusy = true;
        StatusMessage = BrowserDryRun ? "Calculating browser cleanup…" : "Removing browser history and cookies…";
        try
        {
            var result = await Task.Run(() => _services.Browser.Clean(selected, BrowserDryRun));
            var message = result.Summary;
            if (result.Skipped > 0)
            {
                message += $"{Environment.NewLine}Skipped: {result.Skipped} file(s).";
            }

            if (result.Messages.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(10));
            }

            if (BrowserDryRun)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Dry run: nothing was deleted.";
            }

            if (result.Messages.Count == 0 || BrowserDryRun)
            {
                _windows.Info(message, "Browser data");
            }
            else
            {
                _windows.Error(message, "Browser data");
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
            $"Wipe unallocated free space on {drive.DisplayName} ({drive.FreeSpaceDisplay})?\n\n" +
            "This will overwrite all deleted files and unused sectors with zeros to make forensic data recovery impossible. " +
            "Existing files and programs will NOT be touched.\n\n" +
            "Do you want to proceed?",
            "Evidence Remover - Wipe Free Space",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        IsWiping = true;
        WipePercent = 0;
        WipeProgressStatus = $"Initializing free space wipe on {drive.DisplayName}…";
        WipeEtaStatus = string.Empty;
        _wipeCts = new CancellationTokenSource();

        var progress = new Progress<WipeProgress>(p =>
        {
            WipePercent = p.PercentComplete;
            WipeProgressStatus = $"{p.PercentComplete:F1}% — {InstalledProgram.FormatBytes(p.BytesWiped)} of {InstalledProgram.FormatBytes(p.TotalBytesToWipe)} wiped";
            WipeEtaStatus = p.EstimatedTimeRemaining.HasValue
                ? $"ETA: {p.EstimatedTimeRemaining.Value:mm\\:ss}"
                : string.Empty;
        });

        try
        {
            var result = await _services.EvidenceRemover.WipeDriveAsync(drive.DriveName, progress, _wipeCts.Token);
            if (result.Succeeded)
            {
                var formatted = InstalledProgram.FormatBytes(result.TotalBytesWiped);
                _windows.Info($"Free space wipe complete!\nSuccessfully wiped {formatted} in {result.Duration:mm\\:ss}.", "Evidence Remover");
                WipeProgressStatus = "Free space wipe complete.";
            }
            else
            {
                _windows.Error($"Free space wipe stopped or failed:\n{result.ErrorMessage}");
                WipeProgressStatus = $"Failed: {result.ErrorMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            _windows.Info("Free space wipe was cancelled. Temporary wipe files were cleaned up.", "Evidence Remover");
            WipeProgressStatus = "Wipe cancelled.";
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
            WipeProgressStatus = "Cancelling wipe and cleaning up temporary files…";
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
