using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.ViewModels;

public sealed class StoreAppsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private readonly ObservableCollection<MsixPackage> _packages = [];
    private MsixPackage? _selected;
    private string _searchText = string.Empty;

    public StoreAppsViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        PackagesView = CollectionViewSource.GetDefaultView(_packages);
        PackagesView.Filter = Filter;
        Sort = new ResultSort(PackagesView, nameof(MsixPackage.DisplayName),
            new("Name", nameof(MsixPackage.DisplayName)),
            new("Publisher", nameof(MsixPackage.Publisher)),
            new("Type", nameof(MsixPackage.KindDisplay)),
            new("Architecture", nameof(MsixPackage.Architecture)),
            new("Version", nameof(MsixPackage.Version)));
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        UninstallCommand = new AsyncRelayCommand(_ => UninstallSelectedAsync(), _ => CanActOnSelection);
        ResetCommand = new AsyncRelayCommand(_ => ResetSelectedAsync(), _ => CanActOnSelection);
        TerminateCommand = new AsyncRelayCommand(_ => TerminateSelectedAsync(), _ => CanActOnSelection);
        OpenLocationCommand = new RelayCommand(_ => OpenLocation(), _ => CanOpenLocation);
        CopyNameCommand = new RelayCommand(_ => CopyPackageFullName(), _ => HasSelection);
    }

    public ICollectionView PackagesView { get; }
    public ResultSort Sort { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand TerminateCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand CopyNameCommand { get; }

    public MsixPackage? SelectedPackage
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanActOnSelection));
                OnPropertyChanged(nameof(CanOpenLocation));
                UninstallCommand.RaiseCanExecuteChanged();
                ResetCommand.RaiseCanExecuteChanged();
                TerminateCommand.RaiseCanExecuteChanged();
                OpenLocationCommand.RaiseCanExecuteChanged();
                CopyNameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selected is not null;
    public bool CanActOnSelection => !IsBusy && _selected is not null && !_selected.IsFramework;
    public bool CanOpenLocation => _selected is not null && !string.IsNullOrWhiteSpace(_selected.InstallLocation);

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

    public string CountLabel => F("OfPackageS", PackagesView.Cast<MsixPackage>().Count(), _packages.Count);
    public string EmptyLabel => _packages.Count == 0
        ? L("NoMSIXOrMicrosoftStorePackagesAreRegistered")
        : L("NoPackageMatchesTheSearch");

    public override void Activate() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("ReadingTheMSIXPackageRepository");
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
            SetStatus("PackageSLoaded", _packages.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("ThePackageInventoryCouldNotBeRead", ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            TerminateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task UninstallSelectedAsync()
    {
        if (_selected is null)
        {
            return;
        }

        var package = _selected;
        if (!_windows.Confirm(
                F("UninstallWindowsPackagePackage", package.DisplayName, package.PackageFullName) +
                L("ThisWillRemoveThePackageForTheCurrent"),
                L("UninstallWindowsApp")))
        {
            return;
        }

        IsBusy = true;
        SetStatus("Uninstalling", package.DisplayName);
        UninstallCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
        TerminateCommand.RaiseCanExecuteChanged();
        try
        {
            var (success, error) = await _services.Msix.UninstallPackageAsync(package.PackageFullName);
            if (success)
            {
                _windows.Info(F("PackageHasBeenUninstalledSuccessfully", package.DisplayName), L("UninstallComplete"));
                await RefreshAsync();
            }
            else
            {
                _windows.Error(F("FailedToUninstall", package.DisplayName, error), L("UninstallFailed2"));
                SetStatus("UninstallFailed", error);
            }
        }
        finally
        {
            IsBusy = false;
            UninstallCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            TerminateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ResetSelectedAsync()
    {
        if (_selected is null)
        {
            return;
        }

        var package = _selected;
        if (!_windows.Confirm(
                F("ResetPackagePackage", package.DisplayName, package.PackageFullName) +
                L("ThisWillResetTheAppDataAndRestore"),
                L("ResetWindowsApp")))
        {
            return;
        }

        IsBusy = true;
        SetStatus("Resetting", package.DisplayName);
        UninstallCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
        TerminateCommand.RaiseCanExecuteChanged();
        try
        {
            var (success, error) = await _services.Msix.ResetPackageAsync(package.PackageFullName);
            if (success)
            {
                _windows.Info(F("PackageWasSuccessfullyReset", package.DisplayName), L("ResetComplete"));
                SetStatus("Reset2", package.DisplayName);
            }
            else
            {
                _windows.Error(F("FailedToReset", package.DisplayName, error), L("ResetFailed2"));
                SetStatus("ResetFailed", error);
            }
        }
        finally
        {
            IsBusy = false;
            UninstallCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            TerminateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task TerminateSelectedAsync()
    {
        if (_selected is null)
        {
            return;
        }

        var package = _selected;
        IsBusy = true;
        SetStatus("TerminatingProcessesFor", package.DisplayName);
        try
        {
            var (success, error) = await _services.Msix.TerminatePackageAsync(package.PackageFullName);
            if (success)
            {
                SetStatus("TerminatedProcessesFor", package.DisplayName);
            }
            else
            {
                _windows.Error(F("FailedToTerminateProcesses", error), L("TerminateFailed"));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenLocation()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_selected.InstallLocation))
        {
            return;
        }

        if (!MsixInventoryService.OpenPackageLocation(_selected.InstallLocation))
        {
            _windows.Error(F("CouldNotOpenInstallFolder", _selected.InstallLocation), L("OpenLocation"));
        }
    }

    private void CopyPackageFullName()
    {
        if (_selected is null)
        {
            return;
        }

        Clipboard.SetText(_selected.PackageFullName);
        SetStatus("PackageFullNameCopiedToClipboard");
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
