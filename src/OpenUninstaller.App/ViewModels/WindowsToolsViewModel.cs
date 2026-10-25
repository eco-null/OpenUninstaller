using System.Diagnostics;
using OpenUninstaller.App.Infrastructure;

namespace OpenUninstaller.App.ViewModels;

public sealed record WindowsTool(string Name, string Command, string Description, string Glyph);

public sealed class WindowsToolsViewModel : PageViewModel
{
    private readonly WindowService _windows;

    public WindowsToolsViewModel(WindowService windows)
    {
        _windows = windows;
        LaunchCommand = new RelayCommand(Launch);
    }

    public IReadOnlyList<WindowsTool> Tools { get; } =
    [
        new("Programs and Features", "appwiz.cpl", "The built-in installed-programs list.", "\uE7B8"),
        new("Disk Cleanup", "cleanmgr.exe", "Windows' own cleanup utility.", "\uE74D"),
        new("Task Manager", "taskmgr.exe", "Processes, startup impact and performance.", "\uE9D9"),
        new("Startup Apps", "ms-settings:startupapps", "Windows' Startup Apps settings page.", "\uE945"),
        new("Services", "services.msc", "Windows service console.", "\uE90F"),
        new("System Restore", "rstrui.exe", "Create or roll back to a restore point.", "\uE777"),
        new("Windows Update", "ms-settings:windowsupdate", "Check for and manage updates.", "\uE895"),
        new("Registry Editor", "regedit.exe", "Advanced registry editing. Use with care.", "\uE71C")
    ];

    public RelayCommand LaunchCommand { get; }

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
            Process.Start(new ProcessStartInfo
            {
                FileName = tool.Command,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _windows.Error($"Could not start {tool.Name}:\n{ex.Message}");
        }
    }
}
