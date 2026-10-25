using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.App.ViewModels;

public sealed class WindowsToolsViewModel : PageViewModel
{
    private readonly WindowService _windows;
    private readonly WindowsToolsService _service;
    private readonly ObservableCollection<WindowsTool> _tools = [];
    private string _searchText = string.Empty;
    private string _selectedCategory = "All";
    private WindowsTool? _selectedTool;

    public WindowsToolsViewModel(WindowService windows, AppServices services)
    {
        _windows = windows;
        _service = services.WindowsTools;

        ToolsView = CollectionViewSource.GetDefaultView(_tools);
        ToolsView.Filter = FilterTool;

        LaunchCommand = new RelayCommand(Launch);
        RemoveCustomToolCommand = new RelayCommand(RemoveCustomTool, _ => SelectedTool?.IsCustom == true);
        Refresh();
    }

    public ICollectionView ToolsView { get; }

    public IReadOnlyList<string> Categories { get; } =
    [
        "All", "System", "Diagnostics", "Disk & Storage", "Network", "Security", "Custom"
    ];

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ToolsView.Refresh();
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ToolsView.Refresh();
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    public WindowsTool? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value))
            {
                RemoveCustomToolCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CountLabel => $"{ToolsView.Cast<WindowsTool>().Count()} of {_tools.Count} tool(s)";

    public RelayCommand LaunchCommand { get; }
    public RelayCommand RemoveCustomToolCommand { get; }

    public void Refresh()
    {
        _tools.Clear();
        foreach (var tool in _service.GetAllTools())
        {
            _tools.Add(tool);
        }
        ToolsView.Refresh();
        OnPropertyChanged(nameof(CountLabel));
    }

    public void AddCustomTool(string name, string command, string? arguments, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var tool = new WindowsTool
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            Command = command.Trim(),
            Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? "Custom user-defined tool" : description.Trim(),
            Category = "Custom",
            Glyph = "\uE71D",
            RequiresElevation = false,
            IsCustom = true
        };

        _service.AddCustomTool(tool);
        Refresh();
    }

    private void RemoveCustomTool(object? parameter)
    {
        var target = parameter as WindowsTool ?? SelectedTool;
        if (target is null || !target.IsCustom)
        {
            return;
        }

        if (!_windows.Confirm($"Remove custom tool '{target.Name}'?", "Remove Custom Tool"))
        {
            return;
        }

        _service.RemoveCustomTool(target.Id);
        Refresh();
    }

    private bool FilterTool(object obj)
    {
        if (obj is not WindowsTool tool)
        {
            return false;
        }

        if (_selectedCategory != "All" && !string.Equals(tool.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        return tool.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || tool.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || tool.Command.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void Launch(object? parameter)
    {
        if (parameter is not WindowsTool tool)
        {
            return;
        }

        if (tool.Command == "regedit.exe"
            && !_windows.Confirm(
                "Open the Registry Editor?\n\nChanges there can prevent Windows from starting. OpenUninstaller does not edit the registry from this shortcut.",
                "Registry Editor",
                danger: true))
        {
            return;
        }

        try
        {
            var startInfo = _service.CreateStartInfo(tool);
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _windows.Error($"Could not start {tool.Name}:\n{ex.Message}");
        }
    }
}
