using System.Collections.ObjectModel;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.ViewModels;

public sealed class JunkCategoryRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public JunkCategoryRow(JunkCategory category, Action changed)
    {
        Category = category;
        _changed = changed;
    }

    public JunkCategory Category { get; }
    public string Id => Category.Id;
    public string Title => Category.Title;
    public string Description => Category.Description;
    public string SizeDisplay => Category.SizeDisplay;
    public string ItemLabel => $"{Category.ItemCount} item(s)";
    public bool CanClean => Category.CanClean;
    public bool RequiresElevation => Category.RequiresElevation;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value && CanClean))
            {
                _changed();
            }
        }
    }
}

public sealed class JunkViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private bool _dryRun;
    private long _totalBytes;
    private int _totalItems;

    public JunkViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsBusy && SelectedCount > 0);
        SelectAllCommand = new RelayCommand(_ => SetAllSelected(true), _ => !IsBusy && Categories.Count > 0);
    }

    public ObservableCollection<JunkCategoryRow> Categories { get; } = [];
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand SelectAllCommand { get; }

    public bool DryRun
    {
        get => _dryRun;
        set => SetProperty(ref _dryRun, value);
    }

    public int SelectedCount => Categories.Count(c => c.IsSelected);
    public string SelectionLabel => SelectedCount == 0
        ? "No categories selected"
        : $"{SelectedCount} category(ies) selected";

    public string TotalLabel => $"{InstalledProgram.FormatBytes(_totalBytes)} · {_totalItems} item(s)";
    public string DryRunLabel => DryRun ? "Dry run: nothing will be deleted" : "Deletion is permanent for these caches";

    public override void Activate() => _ = ScanAsync();

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Calculating cache sizes…";
        try
        {
            Categories.Clear();
            _totalBytes = 0;
            _totalItems = 0;
            OnPropertyChanged(nameof(TotalLabel));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectionLabel));

            var progress = new Progress<JunkCategory>(category =>
            {
                Categories.Add(new JunkCategoryRow(category, OnSelectionChanged));
                _totalBytes += category.Bytes;
                _totalItems += category.ItemCount;
                OnPropertyChanged(nameof(TotalLabel));
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(SelectionLabel));
                RaiseCommandStates();
            });
            var scan = await Task.Run(() => _services.Junk.Scan(progress));

            _totalBytes = scan.TotalBytes;
            _totalItems = scan.TotalItems;
            OnPropertyChanged(nameof(TotalLabel));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectionLabel));
            StatusMessage = TotalLabel;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _windows.Error($"The junk scan failed:\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task CleanAsync()
    {
        var selected = Categories.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var cleaning = string.Join(", ", Categories.Where(c => c.IsSelected).Select(c => c.Title));
        if (!DryRun && !_windows.Confirm(
                $"Clean the selected categories?\n\n{cleaning}\n\n" +
                "Caches and temporary files will be deleted. Files in use are skipped.",
                "Clean junk files",
                danger: true))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Cleaning…";
        try
        {
            var result = await Task.Run(() => _services.Junk.Clean(selected, DryRun));
            var verb = DryRun ? "Would free" : "Freed";
            _windows.Info(
                $"{verb} {InstalledProgram.FormatBytes(result.FreedBytes)} across {result.DeletedItems} item(s).\n" +
                $"Skipped: {result.SkippedItems} · Failed: {result.FailedItems}" +
                (DryRun ? "\n\nDry run: nothing was deleted." : string.Empty),
                "Junk files");
        }
        finally
        {
            IsBusy = false;
        }

        await ScanAsync();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var category in Categories.Where(c => c.CanClean))
        {
            category.IsSelected = selected;
        }

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
    }
}
