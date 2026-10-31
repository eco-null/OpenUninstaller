using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenUninstaller.App.Infrastructure;
using OpenUninstaller.App.Views;

namespace OpenUninstaller.UiSmoke;

/// <summary>Measures real page XAML without depending on the desktop's maximum window size.</summary>
internal static class LayoutChecks
{
    public static int Run()
    {
        var result = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new OpenUninstaller.App.App();
                app.InitializeComponent();
                foreach (var page in new UserControl[] { new SettingsView(), new PrivacyView() })
                {
                    foreach (var (width, columns) in new[] { (820d, 1), (1220d, 2), (1700d, 2), (2300d, 2) })
                    {
                        void Layout()
                        {
                            page.Measure(new Size(width, 800));
                            page.Arrange(new Rect(0, 0, width, 800));
                            page.UpdateLayout();
                        }
                        Layout();
                        var panel = FindPanel(page) ?? throw new InvalidOperationException("Missing responsive panel.");
                        if (panel.Children.Count != 2) throw new InvalidOperationException("Expected two semantic groups.");
                        var first = (StackPanel)panel.Children[0];
                        var second = (StackPanel)panel.Children[1];
                        var before = second.TransformToAncestor(panel).Transform(new Point());
                        if ((before.X > 100 ? 2 : 1) != columns)
                            throw new InvalidOperationException($"{page.GetType().Name} at {width}: expected {columns} columns, second={before}.");
                        var extra = new Border { Height = 700 };
                        first.Children.Add(extra);
                        Layout();
                        var after = second.TransformToAncestor(panel).Transform(new Point());
                        // An auto scrollbar may consume up to one scrollbar width when content grows.
                        if (Math.Abs(before.X - after.X) > SystemParameters.VerticalScrollBarWidth
                            || (columns == 2 && Math.Abs(before.Y - after.Y) > 1))
                            throw new InvalidOperationException($"Growing a group relocated its neighboring column: {before} -> {after}.");
                        first.Children.Remove(extra);
                        Console.WriteLine($"PASS {page.GetType().Name}: {width} DIP, {columns} stable column(s)");
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

    private static ResponsiveCardPanel? FindPanel(DependencyObject root)
    {
        if (root is ResponsiveCardPanel panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindPanel(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
