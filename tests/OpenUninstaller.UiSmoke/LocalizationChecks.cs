using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenUninstaller.App;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;
using OpenUninstaller.App.Views;
using OpenUninstaller.Core.Localization;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.UiSmoke;

internal static class LocalizationChecks
{
    private static readonly (string Id, string Settings, string Language, string Programs, string No)[] Languages =
    [
        ("english", "Settings", "Language", "Programs", "No"),
        ("turkish", "Ayarlar", "Dil", "Programlar", "Hayır"),
        ("german", "Einstellungen", "Sprache", "Programme", "Nein"),
        ("spanish", "Configuración", "Idioma", "Programas", "No"),
        ("french", "Paramètres", "Langue", "Programmes", "Non"),
        ("italiano", "Impostazioni", "Lingua", "Programmi", "No"),
        ("japanese", "設定", "言語", "プログラム", "いいえ"),
        ("portuguesebrazil", "Configurações", "Idioma", "Programas", "Não"),
        ("russian", "Настройки", "Язык", "Программы", "Нет"),
        ("simplifiedchinese", "设置", "语言", "程序", "否")
    ];

    public static int Run()
    {
        var result = 1;
        var thread = new Thread(() =>
        {
            var fixture = Directory.CreateTempSubdirectory("openuninstaller-localization-");
            try
            {
                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = new ResourceDictionary
                    {
                        Source = new Uri("/OpenUninstaller;component/AppResources.xaml", UriKind.Relative)
                    }
                };
                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
                {
                    try
                    {
                        await VerifyAsync(new AppPaths
                        {
                            ProgramData = Path.Combine(fixture.FullName, "shared"),
                            LocalAppData = Path.Combine(fixture.FullName, "user")
                        });
                        result = 0;
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            finally
            {
                fixture.Delete(recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static async Task VerifyAsync(AppPaths paths)
    {
        const string legacy = """{"language":"finnish","keepBackups":73}""";
        Directory.CreateDirectory(paths.DataDirectory);
        var legacyPath = Path.Combine(paths.DataDirectory, "ui-settings.json");
        File.WriteAllText(legacyPath, legacy);

        var services = AppServices.Create(paths);
        var localization = services.Localization;
        Expect("english", localization.CurrentLanguage, "removed language fallback");
        var vm = new MainViewModel(services, new WindowService(services));
        vm.SelectedNav = vm.NavItems.Single(item => item.Key == "settings");
        var shell = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        shell.Show();
        Layout(shell);
        var picker = Descendants(shell).OfType<ComboBox>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "LanguageSelector");
        Expect(Languages.Length, picker.Items.Count, "language picker item count");
        Expect("english", picker.SelectedValue, "initial language picker value");
        vm.Settings.RunDoctorCommand.Execute(null);
        var diagnosticDeadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Settings.IsBusy && DateTime.UtcNow < diagnosticDeadline)
            await Task.Delay(20);
        Expect(false, vm.Settings.IsBusy, "diagnostic completion");

        // Start in another language to verify that switching back to English also refreshes all bindings.
        foreach (var (id, title, language, programs, no) in Languages.Skip(1).Append(Languages[0]))
        {
            picker.SetCurrentValue(Selector.SelectedValueProperty, id);
            Layout(shell);
            Expect(id, localization.CurrentLanguage, "current language");
            Expect(title, vm.SelectedNav.Label, "selected navigation text");
            Expect(programs, vm.NavItems[0].Label, "programs navigation text");
            Expect(vm.Settings, vm.CurrentPage, "selected page after switching language");

            var texts = Descendants(shell).OfType<TextBlock>().Select(text => text.Text).ToArray();
            if (!texts.Contains(title) || !texts.Contains(language))
                throw new InvalidOperationException($"Settings did not update on the existing window for {id}.");
            if (!vm.Settings.DoctorSummary.Contains(localization.GetString("DataDirectoryIsWritable"), StringComparison.Ordinal))
                throw new InvalidOperationException($"The existing diagnostic report did not update for {id}.");
            var persisted = UiSettings.Load(paths);
            Expect(id, persisted.Language, "persisted language");
            Expect(true, File.Exists(Path.Combine(paths.LocalDataDirectory, "ui-settings.json")), "per-user settings file");
            Expect(legacy, File.ReadAllText(legacyPath), "legacy preferences kept for older builds");
            Expect(73, persisted.KeepBackups, "another persisted preference");
            Expect(id, AppServices.Create(paths).Localization.CurrentLanguage, "language on startup");

            CheckPages(vm, localization);
            CheckDialog(no);
            Console.WriteLine($"PASS {id}: live picker, navigation, eight pages, table header, dialog, diagnostics and saved settings");
        }
    }

    private static void CheckPages(MainViewModel vm, LocalizationService localization)
    {
        var pages = new (UserControl View, object ViewModel, string Key)[]
        {
            (new ProgramsView(), vm.Programs, "NavPrograms"),
            (new AutorunsView(), vm.Autoruns, "NavAutoruns"),
            (new JunkView(), vm.Junk, "NavJunk"),
            (new PrivacyView(), vm.Privacy, "NavPrivacy"),
            (new StoreAppsView(), vm.StoreApps, "NavStoreApps"),
            (new BackupsView(), vm.Backups, "NavBackups"),
            (new WindowsToolsView(), vm.WindowsTools, "NavTools"),
            (new SettingsView(), vm.Settings, "NavSettings")
        };
        foreach (var (view, viewModel, key) in pages)
        {
            view.DataContext = viewModel;
            Layout(view);
            if (!Descendants(view).OfType<TextBlock>().Any(text => text.Text == localization.GetString(key)))
                throw new InvalidOperationException($"Title {key} did not render in {localization.CurrentLanguage}.");
        }

        var autorunsGrid = Descendants(pages[1].View).OfType<DataGrid>().First();
        if (!autorunsGrid.Columns.Any(column => Equals(column.Header, localization.GetString("Name"))))
            throw new InvalidOperationException($"DataGrid header did not render in {localization.CurrentLanguage}.");
    }

    private static void CheckDialog(string dismissLabel)
    {
        Exception? failure = null;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var dialog = Application.Current.Windows.OfType<NoticeWindow>().Single();
            try
            {
                var dismiss = (Button)dialog.FindName("DismissButton");
                Expect(dismissLabel, dismiss.Content, "localized confirmation button");
                Expect(true, dismiss.IsDefault, "safe default confirmation action");
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog.Close(); }
        });
        Expect(MessageBoxResult.No, NoticeWindow.Show(null, "Fixture", "Fixture", MessageBoxButton.YesNo,
            MessageBoxImage.Warning), "closing a confirmation without accepting it");
        if (failure is not null) throw failure;
    }

    private static void Layout(FrameworkElement element)
    {
        element.ApplyTemplate();
        element.Measure(new Size(1320, 840));
        element.Arrange(new Rect(0, 0, 1320, 840));
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        element.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }

    private static void Expect(object expected, object? actual, string target)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Expected {target} to be '{expected}', got '{actual}'.");
    }
}
