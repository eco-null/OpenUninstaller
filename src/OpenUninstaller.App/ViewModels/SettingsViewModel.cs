using System.Diagnostics;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Localization;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;
using static OpenUninstaller.Core.Localization.UiText;

namespace OpenUninstaller.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private string _theme;
    private bool _enableContextMenu;
    private bool _preferSilent;
    private int _keepBackups;
    private DoctorReport? _doctorReport;
    private BackgroundServiceStatus? _service;
    private bool _serviceNeedsUpdate;
    private bool _maintenanceEnabled;
    private int _maintenanceInterval;
    private int _maintenanceKeepBackups;
    private int _maintenanceLogDays;
    private int _maintenanceScanDays;
    private bool _maintenanceCleanJunk;
    private string _maintenanceCategories;

    public SettingsViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        _theme = services.Settings.Theme;
        _preferSilent = services.Settings.PreferSilentUninstall;
        _keepBackups = services.Settings.KeepBackups;
        _enableContextMenu = services.ContextMenu.IsEnabled();

        var maintenance = MaintenanceSettings.Load(services.Paths);
        _maintenanceEnabled = maintenance.Enabled;
        _maintenanceInterval = maintenance.IntervalMinutes;
        _maintenanceKeepBackups = maintenance.KeepBackups;
        _maintenanceLogDays = maintenance.LogRetentionDays;
        _maintenanceScanDays = maintenance.ScanHistoryRetentionDays;
        _maintenanceCleanJunk = maintenance.CleanJunk;
        _maintenanceCategories = string.Join(", ", maintenance.JunkCategories);

        SaveCommand = new RelayCommand(_ => Save());
        ElevateCommand = new RelayCommand(_ => _windows.RestartElevated(), _ => !_services.IsElevated);
        OpenDataCommand = new RelayCommand(_ => _windows.OpenFolder(_services.Paths.DataDirectory));
        OpenBackupsCommand = new RelayCommand(_ => _windows.OpenFolder(_services.Paths.BackupsDirectory));
        OpenLogsCommand = new RelayCommand(_ => _windows.OpenFolder(_services.Paths.LogsDirectory));
        OpenTracesCommand = new RelayCommand(_ => _windows.OpenFolder(_services.Paths.TracesDirectory));
        RunDoctorCommand = new AsyncRelayCommand(_ => RunDoctorAsync(), _ => !IsBusy);
        OpenDocsCommand = new RelayCommand(_ => OpenDocs());
        RefreshServiceCommand = new AsyncRelayCommand(_ => RefreshServiceAsync(), _ => !IsBusy);
        InstallServiceCommand = new AsyncRelayCommand(_ => InstallServiceAsync(), _ => !IsBusy);
        RemoveServiceCommand = new AsyncRelayCommand(_ => RemoveServiceAsync(), _ => !IsBusy);
        UpdateServiceCommand = new AsyncRelayCommand(_ => UpdateServiceAsync(), _ => !IsBusy && ServiceNeedsUpdate);
        RunMaintenanceCommand = new AsyncRelayCommand(_ => RunMaintenanceAsync(), _ => !IsBusy);

    }

    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light"];

    public RelayCommand SaveCommand { get; }
    public RelayCommand ElevateCommand { get; }
    public RelayCommand OpenDataCommand { get; }
    public RelayCommand OpenBackupsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenTracesCommand { get; }
    public AsyncRelayCommand RunDoctorCommand { get; }
    public RelayCommand OpenDocsCommand { get; }
    public AsyncRelayCommand RefreshServiceCommand { get; }
    public AsyncRelayCommand InstallServiceCommand { get; }
    public AsyncRelayCommand RemoveServiceCommand { get; }
    public AsyncRelayCommand UpdateServiceCommand { get; }
    public AsyncRelayCommand RunMaintenanceCommand { get; }

    /// <summary>The installed service runs another version or an older location than this build.</summary>
    public bool ServiceNeedsUpdate
    {
        get => _serviceNeedsUpdate;
        private set
        {
            if (SetProperty(ref _serviceNeedsUpdate, value))
            {
                UpdateServiceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<LanguageInfo> LanguageOptions => _services.Localization.AvailableLanguages;

    public string SelectedLanguageId
    {
        get => _services.Localization.CurrentLanguage;
        set
        {
            if (value == SelectedLanguageId || !LanguageOptions.Any(language => language.Id == value))
                return;

            _services.Localization.SetLanguage(value);
            _services.Settings.Language = SelectedLanguageId;
            if (!_services.Settings.Save())
                SetStatus("LanguageChangedButCouldNotBeSaved");
            OnPropertyChanged();
        }
    }

    public bool EnableContextMenu
    {
        get => _enableContextMenu;
        set => SetProperty(ref _enableContextMenu, value);
    }

    public string Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                ThemeManager.Apply(value);
            }
        }
    }

    public bool PreferSilent
    {
        get => _preferSilent;
        set => SetProperty(ref _preferSilent, value);
    }

    public int KeepBackups
    {
        get => _keepBackups;
        set => SetProperty(ref _keepBackups, Math.Clamp(value, 1, 500));
    }

    public string ServiceStatus => _service?.Label ?? L("NotChecked");

    public string ServiceDetail =>
        _service is { Installed: true } state && !string.IsNullOrWhiteSpace(state.Detail)
            ? state.Detail
            : (_service?.ExecutableExists ?? File.Exists(_services.Background.ExecutablePath))
                ? $"{_services.Background.ServiceName} · {_services.Background.ExecutablePath}"
                : L("TheServiceExecutableIsNotInThisBuild");

    public bool IsElevated => _services.IsElevated;
    public string ServiceExecutable => _services.Background.ExecutablePath;

    public bool MaintenanceEnabled
    {
        get => _maintenanceEnabled;
        set => SetProperty(ref _maintenanceEnabled, value);
    }

    public int MaintenanceInterval
    {
        get => _maintenanceInterval;
        set => SetProperty(ref _maintenanceInterval, Math.Clamp(value, 15, 43_200));
    }

    public int MaintenanceKeepBackups
    {
        get => _maintenanceKeepBackups;
        set => SetProperty(ref _maintenanceKeepBackups, Math.Clamp(value, 1, 500));
    }

    public int MaintenanceLogDays
    {
        get => _maintenanceLogDays;
        set => SetProperty(ref _maintenanceLogDays, Math.Clamp(value, 1, 3650));
    }

    public int MaintenanceScanDays
    {
        get => _maintenanceScanDays;
        set => SetProperty(ref _maintenanceScanDays, Math.Clamp(value, 1, 3650));
    }

    public bool MaintenanceCleanJunk
    {
        get => _maintenanceCleanJunk;
        set => SetProperty(ref _maintenanceCleanJunk, value);
    }

    public string MaintenanceCategories
    {
        get => _maintenanceCategories;
        set => SetProperty(ref _maintenanceCategories, value);
    }

    public string DoctorSummary => _doctorReport is { } report
        ? string.Join(Environment.NewLine, report.Checks.Concat(report.Warnings.Select(w => "! " + w)))
        : string.Empty;

    public string VersionText => $"OpenUninstaller {_services.Version}";
    public string AdminText => _services.IsElevated
        ? L("RunningAsAdministratorMachineWideCleanupIsAvailable")
        : L("RunningAsAStandardUserMachineWideKeys");
    public string DataPath => _services.Paths.DataDirectory;
    public string BackupPath => _services.Paths.BackupsDirectory;
    public string TracePath => _services.Paths.TracesDirectory;
    public string LogPath => _services.Paths.LogsDirectory;
    public string AppVersion => _services.Version;

    public override void Activate() => _ = RefreshServiceAsync();

    private void Save()
    {
        _services.Settings.Theme = Theme;
        _services.Settings.PreferSilentUninstall = PreferSilent;
        _services.Settings.KeepBackups = KeepBackups;

        _services.Settings.EnableContextMenu = EnableContextMenu;
        _services.ContextMenu.SetEnabled(EnableContextMenu);

        _services.Settings.Save();

        var categories = MaintenanceCategories
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        new MaintenanceSettings
        {
            Enabled = MaintenanceEnabled,
            IntervalMinutes = MaintenanceInterval,
            KeepBackups = MaintenanceKeepBackups,
            LogRetentionDays = MaintenanceLogDays,
            ScanHistoryRetentionDays = MaintenanceScanDays,
            CleanJunk = MaintenanceCleanJunk,
            JunkCategories = categories
        }.Save(_services.Paths);

        _services.NotifySettingsChanged();
        SetStatus("SettingsAndMaintenanceScheduleSaved");
    }

    private async Task RunDoctorAsync()
    {
        IsBusy = true;
        SetStatus("CheckingTheEnvironment");
        try
        {
            _doctorReport = await Task.Run(_services.Doctor.Run);
            OnPropertyChanged(nameof(DoctorSummary));
            SetStatus(_doctorReport.Healthy ? "EnvironmentLooksHealthy" : "EnvironmentHasWarnings");
        }
        finally
        {
            IsBusy = false;
            RunDoctorCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RefreshServiceAsync()
    {
        var status = await Task.Run(_services.Background.Query);
        _service = status;
        ServiceNeedsUpdate = status.Installed && status.NeedsUpdate;
        OnPropertyChanged(nameof(ServiceStatus));
        OnPropertyChanged(nameof(ServiceDetail));
    }

    private async Task UpdateServiceAsync()
    {
        if (!_services.IsElevated)
        {
            _windows.Error(
                L("UpdatingTheMaintenanceServiceNeedsAdministratorRightsRestart"),
                L("BackgroundService"));
            return;
        }

        IsBusy = true;
        try
        {
            var (ok, error) = await Task.Run(() =>
            {
                var updated = _services.Background.Update(out var updateError);
                return (updated, updateError);
            });
            if (ok)
            {
                _windows.Info(
                    L("TheMaintenanceServiceNowRunsThisVersionIts"),
                    L("BackgroundService"));
            }
            else
            {
                _windows.Error(error ?? L("TheServiceCouldNotBeUpdated"), L("BackgroundService"));
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshServiceAsync();
        }
    }

    private async Task InstallServiceAsync()
    {
        if (!_services.IsElevated)
        {
            _windows.Error(
                L("InstallingTheMaintenanceServiceNeedsAdministratorRightsRestart"),
                L("BackgroundService"));
            return;
        }

        IsBusy = true;
        try
        {
            var (ok, error) = await Task.Run(() =>
            {
                var installed = _services.Background.Install(out var installError);
                return (installed, installError);
            });
            if (ok)
            {
                _windows.Info(
                    L("TheMaintenanceServiceWasInstalledAndStartedIt"),
                    L("BackgroundService"));
            }
            else
            {
                _windows.Error(error ?? L("TheServiceCouldNotBeInstalled"), L("BackgroundService"));
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshServiceAsync();
        }
    }

    private async Task RemoveServiceAsync()
    {
        if (!_services.IsElevated)
        {
            _windows.Error(
                L("RemovingTheMaintenanceServiceNeedsAdministratorRightsRestart"),
                L("BackgroundService"));
            return;
        }

        if (!_windows.Confirm(
                L("StopAndRemoveTheOpenUninstallerMaintenanceServiceBackups"),
                L("BackgroundService"),
                danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (ok, error) = await Task.Run(() =>
            {
                var removed = _services.Background.Remove(out var removeError);
                return (removed, removeError);
            });
            if (!ok)
            {
                _windows.Error(error ?? L("TheServiceCouldNotBeRemoved"), L("BackgroundService"));
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshServiceAsync();
        }
    }

    private async Task RunMaintenanceAsync()
    {
        IsBusy = true;
        SetStatus("RunningMaintenance");
        try
        {
            var settings = MaintenanceSettings.Load(_services.Paths);
            var result = await _services.Maintenance.RunOnceAsync(settings);
            var message = string.Join(Environment.NewLine, result.Steps);
            if (result.Warnings.Count > 0)
            {
                message += Environment.NewLine + Environment.NewLine +
                           string.Join(Environment.NewLine, result.Warnings.Select(w => "! " + w));
            }

            _windows.Info(
                string.IsNullOrWhiteSpace(message) ? L("NothingToDo") : message,
                L("Maintenance"));
            SetStatus("MaintenanceFinished");
        }
        finally
        {
            IsBusy = false;
            RunMaintenanceCommand.RaiseCanExecuteChanged();
        }
    }

    private void OpenDocs()
    {
        try
        {
            var readme = Path.Combine(_services.Paths.AppBaseDirectory, "README.md");
            if (File.Exists(readme))
            {
                Process.Start(new ProcessStartInfo { FileName = readme, UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/",
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _windows.Error(F("CouldNotOpenTheDocumentation", ex.Message));
        }
    }
}
