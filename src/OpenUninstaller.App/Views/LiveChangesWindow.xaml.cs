using System.Windows;
using OpenUninstaller.Core.Models;

namespace OpenUninstaller.App.Views;

public partial class LiveChangesWindow : Window
{
    public LiveChangesWindow(IReadOnlyList<LiveTrackedFileEvent> fileEvents, IReadOnlyList<LiveTrackedProcess> processes)
    {
        InitializeComponent();
        FileEventsGrid.ItemsSource = fileEvents;
        ProcessesGrid.ItemsSource = processes;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
