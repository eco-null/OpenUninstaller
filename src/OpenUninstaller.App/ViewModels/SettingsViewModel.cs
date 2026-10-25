using System.Diagnostics;
using System.IO;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Localization;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private string _theme;
    private LanguageInfo? _selectedLanguage;
    private bool _enableContextMenu;
    private bool _preferSilent;
    private int _keepBackups;
    private string _doctorSummary = string.Empty;
    private string _serviceStatus = "Not checked";
    private string _serviceDetail = string.Empty;
    private bool? _serviceExecutableExists;
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
        _selectedLanguage = services.Localization.AvailableLanguages
            .FirstOrDefault(l => l.Id.Equals(services.Settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? services.Localization.AvailableLanguages.FirstOrDefault(l => l.Id == "english");

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
        RunMaintenanceCommand = new AsyncRelayCommand(_ => RunMaintenanceAsync(), _ => !IsBusy);

        UpdateServiceDetails();
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
    public AsyncRelayCommand RunMaintenanceCommand { get; }

    public IReadOnlyList<LanguageInfo> LanguageOptions => _services.Localization.AvailableLanguages;

    public LanguageInfo? SelectedLanguage
    {
        get => _selectedLanguage;
        set => SetProperty(ref _selectedLanguage, value);
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

    public string ServiceStatus
    {
        get => _serviceStatus;
        private set => SetProperty(ref _serviceStatus, value);
    }

    public string ServiceDetail
    {
        get => _serviceDetail;
        private set => SetProperty(ref _serviceDetail, value);
    }

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

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set => SetProperty(ref _doctorSummary, value);
    }

    public string VersionText => $"OpenUninstaller {_services.Version}";
    public string AdminText => _services.IsElevated
        ? "Running as administrator. Machine-wide cleanup is available."
        : "Running as a standard user. Machine-wide keys, folders and service management require elevation.";
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

        if (SelectedLanguage is not null)
        {
            _services.Settings.Language = SelectedLanguage.Id;
            _services.Localization.SetLanguage(SelectedLanguage.Id);
        }

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
        StatusMessage = "Settings and maintenance schedule saved.";
    }

    private async Task RunDoctorAsync()
    {
        IsBusy = true;
        StatusMessage = "Checking the environment…";
        try
        {
            var report = await Task.Run(_services.Doctor.Run);
            DoctorSummary = string.Join(
                Environment.NewLine,
                report.Checks.Concat(report.Warnings.Select(w => "! " + w)));
            StatusMessage = report.Healthy ? "Environment looks healthy." : "Environment has warnings.";
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
        ServiceStatus = status.Label;
        _serviceExecutableExists = status.ExecutableExists;
        UpdateServiceDetails();
    }

    private void UpdateServiceDetails()
    {
        var executableExists = _serviceExecutableExists
                               ?? File.Exists(_services.Background.ExecutablePath);
        ServiceDetail = executableExists
            ? $"{_services.Background.ServiceName} · {_services.Background.ExecutablePath}"
            : "The service executable is not in this build. Publish with scripts/publish.ps1 to include it.";
    }

    private async Task InstallServiceAsync()
    {
        if (!_services.IsElevated)
        {
            _windows.Error(
                "Installing the maintenance service needs administrator rights. Restart OpenUninstaller as administrator and try again.",
                "Background service");
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
                    "The maintenance service was installed and started. It runs one pass at startup and then follows maintenance.json.",
                    "Background service");
            }
            else
            {
                _windows.Error(error ?? "The service could not be installed.", "Background service");
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
                "Removing the maintenance service needs administrator rights. Restart OpenUninstaller as administrator and try again.",
                "Background service");
            return;
        }

        if (!_windows.Confirm(
                "Stop and remove the OpenUninstaller maintenance service? Backups, logs and settings are kept.",
                "Background service",
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
                _windows.Error(error ?? "The service could not be removed.", "Background service");
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
        StatusMessage = "Running maintenance…";
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
                string.IsNullOrWhiteSpace(message) ? "Nothing to do." : message,
                "Maintenance");
            StatusMessage = "Maintenance finished.";
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
            _windows.Error($"Could not open the documentation:\n{ex.Message}");
        }
    }
}
