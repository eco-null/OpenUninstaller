using System.Windows;
using System.Windows.Input;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Services;

namespace OpenUninstaller.App.Views;

public partial class HunterWindow : Window
{
    private readonly IReadOnlyList<InstalledProgram> _programs;
    private readonly Action<InstalledProgram> _onUninstall;
    private bool _resolving;

    public HunterWindow()
        : this([], _ => { })
    {
    }

    public HunterWindow(IReadOnlyList<InstalledProgram> programs, Action<InstalledProgram> onUninstall)
    {
        InitializeComponent();
        _programs = programs;
        _onUninstall = onUninstall;
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    private async void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_resolving)
        {
            return;
        }

        _resolving = true;
        Hide();
        try
        {
            await Task.Delay(180);
            var match = HunterService.MatchCursor(_programs);
            if (!match.Matched || match.Program is null)
            {
                var detail = string.IsNullOrWhiteSpace(match.ExePath)
                    ? match.Reason
                    : $"{match.Reason}\n\nExecutable: {match.ExePath}";
                MessageBox.Show(
                    detail,
                    "Hunter mode",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
                return;
            }

            var answer = MessageBox.Show(
                $"Matched application:\n{match.Program.Name}\n\n{match.Reason}\n\n" +
                $"Executable: {match.ExePath}\n\nOpen the uninstall workflow?",
                "Hunter mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
            {
                var program = match.Program;
                Close();
                _onUninstall(program);
                return;
            }

            Close();
        }
        finally
        {
            _resolving = false;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
