using OpenUninstaller.App.Infrastructure;

namespace OpenUninstaller.App.ViewModels;

public sealed record NavItem(string Key, string Label, string Glyph);

public interface IPageViewModel
{
    void Activate();
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly WindowService _windows;
    private readonly HashSet<string> _activated = [];
    private NavItem _selectedNav;
    private object _currentPage;

    public MainViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        services.SettingsChanged += (_, _) => OnPropertyChanged(nameof(StatusLabel));

        Programs = new ProgramsViewModel(services, windows);
        Autoruns = new AutorunsViewModel(services, windows);
        Junk = new JunkViewModel(services, windows);
        Privacy = new PrivacyViewModel(services, windows);
        StoreApps = new StoreAppsViewModel(services, windows);
        Backups = new BackupsViewModel(services, windows);
        WindowsTools = new WindowsToolsViewModel(windows, services);
        Settings = new SettingsViewModel(services, windows);

        NavItems =
        [
            new("programs", "Programs", "\uE7B8"),
            new("autoruns", "Autoruns", "\uE945"),
            new("junk", "Junk files", "\uE74D"),
            new("privacy", "Privacy & shredder", "\uE72E"),
            new("store", "Store apps", "\uE719"),
            new("backups", "Backups", "\uE777"),
            new("tools", "Windows tools", "\uE90F"),
            new("settings", "Settings", "\uE713")
        ];

        _selectedNav = NavItems[0];
        _currentPage = Programs;
    }

    public ProgramsViewModel Programs { get; }
    public AutorunsViewModel Autoruns { get; }
    public JunkViewModel Junk { get; }
    public PrivacyViewModel Privacy { get; }
    public StoreAppsViewModel StoreApps { get; }
    public BackupsViewModel Backups { get; }
    public WindowsToolsViewModel WindowsTools { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !SetProperty(ref _selectedNav, value))
            {
                return;
            }

            CurrentPage = PageFor(value.Key);
            if (_activated.Add(value.Key))
            {
                (CurrentPage as IPageViewModel)?.Activate();
            }
        }
    }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string AdminLabel => _services.IsElevated ? "Administrator" : "Standard user";
    public string VersionLabel => $"v{_services.Version}";
    public string StatusLabel => "100% Offline · No telemetry · Zero external connections";

    public async Task InitializeAsync()
    {
        _activated.Add("programs");
        await Programs.RefreshAsync();
    }

    private object PageFor(string key) => key switch
    {
        "autoruns" => Autoruns,
        "junk" => Junk,
        "privacy" => Privacy,
        "store" => StoreApps,
        "backups" => Backups,
        "tools" => WindowsTools,
        "settings" => Settings,
        _ => Programs
    };
}
