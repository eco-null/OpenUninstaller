using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.ViewModels;

public sealed class StoreAppsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly ObservableCollection<MsixPackage> _packages = [];
    private MsixPackage? _selected;
    private string _searchText = string.Empty;

    public StoreAppsViewModel(AppServices services)
    {
        _services = services;
        PackagesView = CollectionViewSource.GetDefaultView(_packages);
        PackagesView.Filter = Filter;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
    }

    public ICollectionView PackagesView { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public MsixPackage? SelectedPackage
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                PackagesView.Refresh();
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    public string CountLabel => $"{PackagesView.Cast<MsixPackage>().Count()} of {_packages.Count} package(s)";
    public string EmptyLabel => _packages.Count == 0
        ? "No MSIX or Microsoft Store packages are registered for this user."
        : "No package matches the search.";

    public override void Activate() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Reading the MSIX package repository…";
        try
        {
            var packages = await Task.Run(_services.Msix.Scan);
            _packages.Clear();
            foreach (var package in packages)
            {
                _packages.Add(package);
            }

            PackagesView.Refresh();
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(EmptyLabel));
            StatusMessage = $"{_packages.Count} package(s). Read-only inventory: Windows manages package removal, updates and activation.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The package inventory could not be read: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private bool Filter(object item)
    {
        if (item is not MsixPackage package)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        var term = _searchText.Trim();
        return package.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || package.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
               || (package.Publisher?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
               || (package.PackageFullName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
