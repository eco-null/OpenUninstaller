using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;
using OpenUninstaller.App.Views;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.UiSmoke;

/// <summary>In-memory junk-preview checks. Never runs a filesystem scan or cleanup command.</summary>
internal static class JunkChecks
{
    public static int Run(string? screenshotDirectory = null)
    {
        var result = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new OpenUninstaller.App.App();
                app.InitializeComponent();
                var services = AppServices.Create();
                using var vm = new JunkViewModel(services, new WindowService(services));
                var category = new JunkCategory { Id = "user-temp", Title = "Temporary files", Description = "Fixture", SkippedItems = 3, IsComplete = false };
                var review = new JunkCategory { Id = "pattern-search", Title = "Pattern matches", Description = "Fixture", CanClean = false };
                for (var index = 0; index < 10_000; index++) category.Files.Add(File($"safe-{index}.tmp", "user-temp", true));
                review.Files.Add(File("personal.old", "pattern-search", false));
                category.ItemCount = category.Files.Count;
                category.Bytes = category.Files.Sum(file => file.Bytes);
                review.ItemCount = review.Files.Count;
                review.Bytes = review.Files.Sum(file => file.Bytes);
                vm.Categories.Add(new JunkCategoryRow(category, _ => { }));
                vm.Categories.Add(new JunkCategoryRow(review, _ => { }));
                Call(vm, "CompletePreview");
                Require(vm.SelectedCount == 0 && !vm.CleanCommand.CanExecute(null), "A new preview selected files automatically.");
                vm.SelectAllCommand.Execute(null);
                Require(vm.SelectedCount == 10_000, "Select all selected report-only files or lost safe candidates.");
                var report = vm.FilesView.Cast<JunkFileRow>().Single(row => row.File.CategoryId == "pattern-search");
                report.IsSelected = true;
                Require(!report.IsSelected, "A report-only candidate became selectable.");
                vm.SearchText = "safe-99";
                Require(vm.FilesView.Cast<object>().Count() == 111 && vm.SelectedCount == 10_000, "Filtering changed selection or returned wrong candidates.");
                vm.ClearSelectionCommand.Execute(null);
                Require(vm.SelectedCount == 0, "Clear selection did not include hidden rows.");
                vm.SearchText = string.Empty;
                vm.DryRun = true;
                Require(vm.DryRunLabel.Contains("nothing will be deleted", StringComparison.Ordinal), "Dry-run text did not update.");
                Require(vm.Categories[0].ItemLabel.Contains("3 skipped", StringComparison.Ordinal) && vm.Categories[0].ItemLabel.Contains("Incomplete", StringComparison.Ordinal), "Incomplete/skipped status is hidden.");
                CheckScopeOptions(vm);
                Console.WriteLine("PASS junk preview: safe selection, report-only refusal, filtering, dry-run, warnings, scope and input validation");
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    app.Resources.MergedDictionaries[0] = new ResourceDictionary
                    {
                        Source = new Uri($"/OpenUninstaller;component/Themes/{theme}.xaml", UriKind.Relative)
                    };
                    foreach (var width in new[] { 820d, 1220d, 1700d })
                    {
                        var page = new JunkView
                        {
                            DataContext = vm, Foreground = (Brush)app.FindResource("TextPrimary"),
                            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13,
                            UseLayoutRounding = true, SnapsToDevicePixels = true
                        };
                        page.Measure(new Size(width, 620));
                        page.Arrange(new Rect(0, 0, width, 620));
                        page.UpdateLayout();
                        var grid = Find<DataGrid>(page) ?? throw new InvalidOperationException("Missing junk file grid.");
                        Require(grid.Items.Count == 10_001 && grid.EnableRowVirtualization && grid.EnableColumnVirtualization,
                            "The junk preview lost rows or virtualization.");
                        Require(grid.ActualHeight > 100 && grid.ActualWidth <= width, $"Junk table is clipped at {width} DIP.");
                        var scope = FindAll<Expander>(page).Single(expander => Equals(expander.Header, "Scan scope and rules"));
                        scope.IsExpanded = true;
                        page.UpdateLayout();
                        var includeInput = FindAll<TextBox>(page).Single(input =>
                            System.Windows.Automation.AutomationProperties.GetName(input) == "Include all files from folders");
                        Require(includeInput.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(JunkViewModel.IncludedFolderPaths),
                            "The include-folder input is not bound to the separate review-only scope.");
                        Require(includeInput.ActualWidth > 200 && includeInput.ActualWidth < width,
                            $"The include-folder field is clipped at {width} DIP.");
                        Require(FindAll<CheckBox>(page).Any(check => Equals(check.Content, "Filter pattern matches by age")),
                            "The age filter cannot be disabled from the UI.");
                        if (screenshotDirectory is not null)
                            SaveScreenshot(app, page, width, screenshotDirectory, $"junk-scope-{theme.ToLowerInvariant()}-{width:0}.png");
                        scope.IsExpanded = false;
                        page.UpdateLayout();
                        if (screenshotDirectory is not null)
                            SaveScreenshot(app, page, width, screenshotDirectory, $"junk-{theme.ToLowerInvariant()}-{width:0}.png");
                        Console.WriteLine($"PASS junk XAML {theme} {width} DIP: {grid.ActualWidth:0}x{grid.ActualHeight:0} virtualized table");
                    }
                }
                app.Shutdown();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static void SaveScreenshot(Application app, JunkView page, double width, string directory, string filename)
    {
        Directory.CreateDirectory(directory);
        var background = new Border { Background = (Brush)app.FindResource("AppBackground"), Child = page };
        background.Measure(new Size(width, 620));
        background.Arrange(new Rect(0, 0, width, 620));
        background.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, 620, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
        background.Child = null;
    }

    private static void CheckScopeOptions(JunkViewModel vm)
    {
        var fixture = Directory.CreateTempSubdirectory("ou-junk-ui-scope-");
        try
        {
            vm.IncludeKnownLocations = false;
            RequireThrows(vm, "Choose safe locations", () => Call(vm, "BuildOptions", false));
            vm.IncludedFolderPaths = "relative";
            RequireThrows(vm, "existing absolute path", () => Call(vm, "BuildOptions", false));
            vm.IncludedFolderPaths = fixture.FullName;
            vm.Patterns = "C:\\*.tmp";
            vm.MinimumAgeHours = 1;
            var included = (JunkScanOptions)Call(vm, "BuildOptions", false)!;
            Require(!included.IncludeKnownLocations && included.SearchRoots.Count == 0 &&
                    included.IncludeRoots.SequenceEqual([fixture.FullName]) && included.Patterns.Count == 0,
                "Include-only scope requires or inherits ordinary pattern rules.");
            vm.FolderPaths = fixture.FullName;
            RequireThrows(vm, "File patterns", () => Call(vm, "BuildOptions", false));
            vm.Patterns = "*.tmp";
            RequireThrows(vm, "24 hours", () => Call(vm, "BuildOptions", false));
            vm.UseAgeFilter = false;
            var explicitOptions = (JunkScanOptions)Call(vm, "BuildOptions", false)!;
            Require(!explicitOptions.UseAgeFilter && explicitOptions.IncludeRoots.SequenceEqual([fixture.FullName]) &&
                    explicitOptions.SearchRoots.SequenceEqual([fixture.FullName]),
                "Manual scan lost the separate include scope or disabled age filter.");
            vm.FolderPaths = "relative-pattern-folder";
            vm.IncludedFolderPaths = "relative-include-folder";
            vm.Patterns = "C:\\*.tmp";
            var automatic = (JunkScanOptions)Call(vm, "BuildOptions", true)!;
            Require(automatic.IncludeKnownLocations && automatic.SearchRoots.Count == 0 && automatic.IncludeRoots.Count == 0 &&
                    automatic.MinimumAge.TotalHours == 24 && automatic.UseLastAccessTime && automatic.UseAgeFilter,
                "Automatic activation inherited unsubmitted broad scope or changed safe defaults.");
        }
        finally
        {
            vm.IncludeKnownLocations = true;
            vm.FolderPaths = vm.IncludedFolderPaths = string.Empty;
            vm.Patterns = "*.tmp";
            vm.MinimumAgeHours = 24;
            vm.UseAgeFilter = true;
            fixture.Delete();
        }
    }

    private static JunkFile File(string name, string category, bool canClean) => new()
    {
        Id = name, Identity = name, CategoryId = category, Root = @"C:\JunkFixture",
        Path = @"C:\JunkFixture\" + name, Bytes = 100, LastWriteUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CanClean = canClean, Reason = canClean ? "Known temporary folder" : "File name alone does not establish safe deletion"
    };
    private static object? Call(object target, string method, params object[] args) =>
        (target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(method)).Invoke(target, args);
    private static void RequireThrows(JunkViewModel vm, string message, Action action)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException argument && argument.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Invalid junk options were not rejected: {message}; {vm.Patterns}");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
