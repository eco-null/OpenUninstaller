using System.Windows.Controls;
using System.Windows.Input;
using OpenUninstaller.App.ViewModels;

namespace OpenUninstaller.App.Views;

public partial class ProgramsView : UserControl
{
    public ProgramsView()
    {
        InitializeComponent();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProgramsViewModel { SelectedRow: not null } viewModel)
        {
            viewModel.UninstallCommand.Execute(null);
        }
    }
}
