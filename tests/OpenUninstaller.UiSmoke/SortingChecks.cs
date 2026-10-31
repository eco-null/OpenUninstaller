using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.ViewModels;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.UiSmoke;

/// <summary>In-memory WPF regression checks. Never invokes inventory or mutation commands.</summary>
internal static class SortingChecks
{
    public static int Run()
    {
        var result = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var services = AppServices.Create();
                var windows = new WindowService(services);
                Programs(new ProgramsViewModel(services, windows));
                Autoruns(new AutorunsViewModel(services, windows));
                Store(new StoreAppsViewModel(services, windows));
                Console.WriteLine("PASS all sorting regression checks");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static void Programs(ProgramsViewModel vm)
    {
        var rows = Field<ProgramRow>(vm, "_rows");
        ProgramRow Row(string name, long? size, DateOnly? date, string? publisher) => new(new InstalledProgram
        {
            Id = name, Name = name, Publisher = publisher, EstimatedSizeBytes = size, InstallDate = date,
            Architecture = ProgramArchitecture.X64,
            Location = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\SortingFixture")
        }, null);
        var old = new DateOnly(2020, 1, 1);
        var newer = new DateOnly(2026, 10, 31);
        rows.Add(Row("Zulu", 2L * 1024 * 1024 * 1024, old, "Alpha publisher"));
        rows.Add(Row("Alpha", 900L * 1024 * 1024, newer, "Zulu publisher"));
        rows.Add(Row("Beta", 900L * 1024 * 1024, old, null));
        rows.Add(Row("Unknown", null, null, null));
        var selected = rows[0];
        selected.IsQueued = true;
        vm.SelectedRow = selected;
        var grid = Grid(vm.RowsView, vm.Sort);
        grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(vm.SelectedRow)) { Source = vm, Mode = BindingMode.TwoWay });
        Names(vm.RowsView, "Alpha", "Beta", "Unknown", "Zulu");
        Require(vm.Sort.SelectedOption.ToString() == "Name · A–Z", "The selected sort must display its label.");
        Choose(vm.Sort, "Program.EstimatedSizeBytes", ListSortDirection.Descending);
        Names(vm.RowsView, "Zulu", "Alpha", "Beta", "Unknown");
        Choose(vm.Sort, "Program.EstimatedSizeBytes", ListSortDirection.Ascending);
        Names(vm.RowsView, "Alpha", "Beta", "Zulu", "Unknown");
        Choose(vm.Sort, "Program.InstallDate", ListSortDirection.Descending);
        Names(vm.RowsView, "Alpha", "Beta", "Zulu", "Unknown");
        Choose(vm.Sort, "Program.InstallDate", ListSortDirection.Ascending);
        Names(vm.RowsView, "Beta", "Zulu", "Alpha", "Unknown");
        Require(ReferenceEquals(vm.SelectedRow, selected) && ReferenceEquals(grid.SelectedItem, selected)
            && selected.IsQueued && vm.QueuedCount == 1, "Sorting lost program selection or queue.");
        var size = new DataGridTextColumn { Binding = new Binding("SizeDisplay"), SortMemberPath = "Program.EstimatedSizeBytes" };
        grid.Columns.Add(size);
        Click(grid, size);
        Require(vm.Sort.SelectedOption.Direction == ListSortDirection.Ascending, "First size header click failed.");
        Click(grid, size);
        Names(vm.RowsView, "Zulu", "Alpha", "Beta", "Unknown");
        Require(size.SortDirection == ListSortDirection.Descending && vm.Sort.SelectedOption.Path == size.SortMemberPath,
            "Header and dropdown are out of sync.");
        Choose(vm.Sort, "Name", ListSortDirection.Descending);
        Require(size.SortDirection is null, "Old header indicator remained after changing the dropdown.");
        vm.SearchText = "Alpha";
        Names(vm.RowsView, "Zulu", "Alpha"); // Also matches Alpha publisher; current sort remains descending.
        vm.SelectedFilter = "Large programs (≥ 500 MB)";
        Names(vm.RowsView, "Zulu", "Alpha");
        vm.SearchText = "__no_match__";
        Require(vm.VisibleCount == 0, "Empty sorted search did not work.");
        vm.SearchText = "";
        vm.SelectedFilter = "All programs";
        var snapshot = rows.ToArray();
        rows.Clear();
        foreach (var row in snapshot.Reverse()) rows.Add(row);
        Names(vm.RowsView, "Zulu", "Unknown", "Beta", "Alpha");
        Console.WriteLine("PASS Programs: numeric sizes, raw dates, missing values, name ties, selection/queue, headers, filters, repopulation");
    }

    private static void Autoruns(AutorunsViewModel vm)
    {
        var entries = Field<AutorunEntry>(vm, "_allEntries");
        entries.Add(new AutorunEntry { Id = "z", Name = "Zulu", Command = "z.exe", Source = AutorunSourceKind.UserRun,
            LocationDisplay = "Fixture", IsEnabled = true, Publisher = "Alpha publisher" });
        entries.Add(new AutorunEntry { Id = "a", Name = "Alpha", Command = "a.exe", Source = AutorunSourceKind.ScheduledTask,
            LocationDisplay = "Fixture", IsEnabled = false });
        entries.Add(new AutorunEntry { Id = "s", Name = "System", Command = "s.exe", Source = AutorunSourceKind.MachineService,
            LocationDisplay = "Fixture", IsEnabled = true, IsWindowsComponent = true });
        var history = Field<AutorunHistoryEntry>(vm, "_allHistory");
        var timestamp = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(3));
        history.Add(new AutorunHistoryEntry { Id = "old", EntryId = "z", Name = "Old", Command = "z.exe",
            Source = AutorunSourceKind.UserRun, Action = AutorunHistoryAction.Disabled, Timestamp = timestamp });
        history.Add(history[0] with { Id = "new", Name = "New", Timestamp = timestamp.ToOffset(TimeSpan.Zero).AddMinutes(1) });
        vm.SearchText = " ";
        Names(vm.EntriesView, "Alpha", "Zulu");
        vm.SelectedEntry = entries[0];
        var grid = Grid(vm.EntriesView, vm.Sort);
        grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(vm.SelectedEntry)) { Source = vm, Mode = BindingMode.TwoWay });
        Choose(vm.Sort, "IsEnabled", ListSortDirection.Descending);
        Names(vm.EntriesView, "Zulu", "Alpha");
        Choose(vm.Sort, "IsEnabled", ListSortDirection.Ascending);
        Names(vm.EntriesView, "Alpha", "Zulu");
        Require(vm.SelectedEntry == entries[0] && ReferenceEquals(grid.SelectedItem, entries[0]), "Autorun sorting lost selection.");
        vm.SelectedMode = "Advanced";
        Names(vm.EntriesView, "Alpha", "System", "Zulu");
        vm.SearchText = "z.exe";
        Names(vm.EntriesView, "Zulu");
        Require(vm.SelectedEntry == entries[0], "Matching autorun selection was lost during filtering.");
        vm.SearchText = "__no_match__";
        Require(vm.SelectedEntry is null && vm.Entries.Count == 0, "Hidden autorun selection was retained.");
        vm.SearchText = "";
        vm.SelectedMode = "History & rollback";
        Names(vm.HistoryView, "New", "Old");
        Choose(vm.HistorySort, "Timestamp", ListSortDirection.Ascending);
        Names(vm.HistoryView, "Old", "New");
        vm.SelectedMode = "Basic";
        Names(vm.EntriesView, "Alpha", "Zulu");
        Require(vm.HistorySort.SelectedOption.Direction == ListSortDirection.Ascending, "History sort was lost on mode switch.");
        Console.WriteLine("PASS Autoruns: states, Basic/Advanced, selection, search, history offsets/order, independent sorts");
    }

    private static void Store(StoreAppsViewModel vm)
    {
        var packages = Field<MsixPackage>(vm, "_packages");
        packages.Add(new MsixPackage { Name = "z", DisplayName = "Zulu", PackageFullName = "z_fixture", Publisher = "Alpha" });
        packages.Add(new MsixPackage { Name = "a", DisplayName = "Alpha", PackageFullName = "a_fixture", Publisher = "Zulu" });
        vm.SelectedPackage = packages[0];
        Names(vm.PackagesView, "Alpha", "Zulu");
        Choose(vm.Sort, "Publisher", ListSortDirection.Ascending);
        Names(vm.PackagesView, "Zulu", "Alpha");
        Require(vm.SelectedPackage == packages[0], "Package selection was lost.");
        vm.SearchText = "a_fixture";
        Names(vm.PackagesView, "Alpha");
        vm.SearchText = "";
        Choose(vm.Sort, "DisplayName", ListSortDirection.Descending);
        Names(vm.PackagesView, "Zulu", "Alpha");
        Console.WriteLine("PASS Store: names, publishers, selection, filtered and reset ordering");
    }

    private static DataGrid Grid(ICollectionView view, ResultSort sort)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, ItemsSource = view };
        GridSorting.SetSort(grid, sort);
        return grid;
    }

    private static void Click(DataGrid grid, DataGridColumn column) =>
        typeof(DataGrid).GetMethod("OnSorting", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(grid, [new DataGridSortingEventArgs(column)]);

    private static ObservableCollection<T> Field<T>(object target, string name) =>
        (ObservableCollection<T>)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Choose(ResultSort sort, string path, ListSortDirection direction) =>
        sort.SelectedOption = sort.Options.Single(option => option.Path == path && option.Direction == direction);

    private static void Names(ICollectionView view, params string[] expected)
    {
        var actual = view.Cast<object>().Select(item => item switch
        {
            ProgramRow row => row.Name, AutorunEntry entry => entry.Name,
            AutorunHistoryEntry history => history.Name, MsixPackage package => package.DisplayName,
            _ => throw new InvalidOperationException("Unknown row type.")
        }).ToArray();
        Require(actual.SequenceEqual(expected), $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
