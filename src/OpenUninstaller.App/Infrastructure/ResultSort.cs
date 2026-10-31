using System.ComponentModel;

namespace OpenUninstaller.App.Infrastructure;

public sealed record SortField(string Label, string Path, string Ascending = "A–Z", string Descending = "Z–A", string? PresencePath = null);
public sealed record SortOption(string Label, string Path, ListSortDirection Direction, string? PresencePath)
{
    // The shared ComboBox template renders SelectionBoxItem directly.
    public override string ToString() => Label;
}

/// <summary>Sorts the existing view so row identity, selection and queued items survive reordering.</summary>
public sealed class ResultSort : ObservableObject
{
    private readonly ICollectionView _view;
    private readonly string _namePath;
    private SortOption _selectedOption;

    public ResultSort(ICollectionView view, string namePath, params SortField[] fields)
    {
        _view = view;
        _namePath = namePath;
        Options = fields.SelectMany(field => new[]
        {
            new SortOption($"{field.Label} · {field.Ascending}", field.Path, ListSortDirection.Ascending, field.PresencePath),
            new SortOption($"{field.Label} · {field.Descending}", field.Path, ListSortDirection.Descending, field.PresencePath)
        }).ToArray();
        _selectedOption = Options[0];
        Apply();
    }

    public IReadOnlyList<SortOption> Options { get; }

    public SortOption SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (value is not null && Options.Contains(value) && SetProperty(ref _selectedOption, value)) Apply();
        }
    }

    private void Apply()
    {
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            // Unknown sizes/dates stay at the end in either direction.
            if (SelectedOption.PresencePath is { } presence)
                _view.SortDescriptions.Add(new SortDescription(presence, ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(SelectedOption.Path, SelectedOption.Direction));
            if (SelectedOption.Path != _namePath)
                _view.SortDescriptions.Add(new SortDescription(_namePath, ListSortDirection.Ascending));
        }
    }
}
