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

    private IReadOnlyList<NavItem> _navItems = [];

    public MainViewModel(AppServices services, WindowService windows)
    {
        _services = services;
        _windows = windows;
        services.SettingsChanged += (_, _) => OnPropertyChanged(nameof(StatusLabel));
        services.Localization.LanguageChanged += _ => RefreshLocalization();

        Programs = new ProgramsViewModel(services, windows);
        Autoruns = new AutorunsViewModel(services, windows);
        Junk = new JunkViewModel(services, windows);
        Privacy = new PrivacyViewModel(services, windows);
        StoreApps = new StoreAppsViewModel(services, windows);
        Backups = new BackupsViewModel(services, windows);
        WindowsTools = new WindowsToolsViewModel(windows, services);
        Settings = new SettingsViewModel(services, windows);

        _navItems = BuildNavItems();
        _selectedNav = _navItems[0];
        _currentPage = Programs;
    }

    private IReadOnlyList<NavItem> BuildNavItems() =>
    [
        new("programs", _services.Localization.GetString("NavPrograms", "Programs"), "\uE7B8"),
        new("autoruns", _services.Localization.GetString("NavAutoruns", "Autoruns"), "\uE945"),
        new("junk", _services.Localization.GetString("NavJunk", "Junk files"), "\uE74D"),
        new("privacy", _services.Localization.GetString("NavPrivacy", "Privacy & shredder"), "\uE72E"),
        new("store", _services.Localization.GetString("NavStoreApps", "Store apps"), "\uE719"),
        new("backups", _services.Localization.GetString("NavBackups", "Backups"), "\uE777"),
        new("tools", _services.Localization.GetString("NavTools", "Windows tools"), "\uE90F"),
        new("settings", _services.Localization.GetString("NavSettings", "Settings"), "\uE713")
    ];

    private void RefreshLocalization()
    {
        var currentKey = _selectedNav?.Key ?? "programs";
        NavItems = BuildNavItems();
        _selectedNav = NavItems.FirstOrDefault(n => n.Key == currentKey) ?? NavItems[0];
        OnPropertyChanged(nameof(SelectedNav));
        OnPropertyChanged(nameof(StatusLabel));
    }

    public ProgramsViewModel Programs { get; }
    public AutorunsViewModel Autoruns { get; }
    public JunkViewModel Junk { get; }
    public PrivacyViewModel Privacy { get; }
    public StoreAppsViewModel StoreApps { get; }
    public BackupsViewModel Backups { get; }
    public WindowsToolsViewModel WindowsTools { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems
    {
        get => _navItems;
        private set => SetProperty(ref _navItems, value);
    }

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
