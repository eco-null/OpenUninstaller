using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace OpenUninstaller.App.Infrastructure;

/// <summary>Keeps native header clicks and the explicit sort picker in agreement.</summary>
public static class GridSorting
{
    public static readonly DependencyProperty SortProperty = DependencyProperty.RegisterAttached(
        "Sort", typeof(ResultSort), typeof(GridSorting), new PropertyMetadata(null, OnSortChanged));
    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached(
        "Subscription", typeof(Subscription), typeof(GridSorting));

    public static ResultSort? GetSort(DependencyObject target) => (ResultSort?)target.GetValue(SortProperty);
    public static void SetSort(DependencyObject target, ResultSort? value) => target.SetValue(SortProperty, value);

    private static void OnSortChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not DataGrid grid) return;
        (grid.GetValue(SubscriptionProperty) as Subscription)?.Detach();
        grid.SetValue(SubscriptionProperty, args.NewValue is ResultSort sort ? new Subscription(grid, sort) : null);
    }

    private sealed class Subscription
    {
        private readonly DataGrid _grid;
        private readonly ResultSort _sort;

        public Subscription(DataGrid grid, ResultSort sort)
        {
            _grid = grid;
            _sort = sort;
            grid.Loaded += OnLoaded;
            grid.Sorting += OnSorting;
            PropertyChangedEventManager.AddHandler(sort, OnOptionChanged, nameof(ResultSort.SelectedOption));
            UpdateHeaders();
        }

        public void Detach()
        {
            _grid.Loaded -= OnLoaded;
            _grid.Sorting -= OnSorting;
            PropertyChangedEventManager.RemoveHandler(_sort, OnOptionChanged, nameof(ResultSort.SelectedOption));
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => UpdateHeaders();
        private void OnOptionChanged(object? sender, PropertyChangedEventArgs args) => UpdateHeaders();

        private void UpdateHeaders()
        {
            foreach (var column in _grid.Columns)
                column.SortDirection = column.SortMemberPath == _sort.SelectedOption.Path ? _sort.SelectedOption.Direction : null;
        }

        private void OnSorting(object sender, DataGridSortingEventArgs args)
        {
            args.Handled = true;
            var direction = args.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending : ListSortDirection.Ascending;
            var option = _sort.Options.FirstOrDefault(o => o.Path == args.Column.SortMemberPath && o.Direction == direction);
            if (option is not null) _sort.SelectedOption = option;
            UpdateHeaders();
        }
    }
}
