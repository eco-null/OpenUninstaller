using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace OpenUninstaller.UiSmoke;

/// <summary>
/// Read-only UI smoke test using the built-in UI Automation API (no WinAppDriver dependency).
/// It launches the app, visits every navigation page, verifies the page title rendered and closes
/// the window. It never invokes uninstall, cleanup or service actions.
/// </summary>
internal static class Program
{
    private static readonly (string Nav, string Marker)[] Pages =
    [
        ("Programs", "Inventory, uninstall, review leftovers. OpenUninstaller runs the vendor uninstaller and never deletes without your approval."),
        ("Autoruns", "Programs, services and scheduled tasks that start with Windows. Disabling is reversible: the original value, service start mode or task state is recorded locally and can be restored."),
        ("Junk files", "Allow-listed caches and temporary files. Only children of known roots are touched, and files modified in the last ten minutes are skipped."),
        ("Privacy & shredder", "Remove Windows history traces and overwrite files before deletion. History cleanup is intentionally irreversible, so the privacy data is not copied into a backup."),
        ("Store apps", "Inventory, uninstall, and reset Microsoft Store and MSIX packages for this user."),
        ("Backups", "Every deletion is preceded by a registry export and a file archive. Restore returns captured items to their original locations."),
        ("Windows tools", "Shortcuts to built-in maintenance applets. These launch Windows components; OpenUninstaller does not modify them."),
        ("Settings", "Preferences are stored locally in the OpenUninstaller data directory. Fully offline, no telemetry, zero external network connections.")
    ];

    private static int Main(string[] args)
    {
        var exe = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "src", "OpenUninstaller.App", "bin", "Release", "net8.0-windows", "OpenUninstaller.exe"));

        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"App executable not found: {exe}");
            return 2;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        });
        if (process is null)
        {
            Console.Error.WriteLine("The app process could not be started.");
            return 2;
        }

        try
        {
            var window = WaitForWindow(TimeSpan.FromSeconds(30));
            if (window is null)
            {
                Console.Error.WriteLine("The main window did not appear.");
                return 1;
            }

            foreach (var (page, marker) in Pages)
            {
                var item = window.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, page)));
                if (item is null)
                {
                    Console.Error.WriteLine($"Navigation entry '{page}' was not found.");
                    return 1;
                }

                if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                    || pattern is not SelectionItemPattern selection)
                {
                    Console.Error.WriteLine($"Navigation entry '{page}' cannot be selected.");
                    return 1;
                }

                selection.Select();
                Thread.Sleep(400);
                if (process.HasExited)
                {
                    Console.Error.WriteLine($"The app exited while opening '{page}'.");
                    return 1;
                }

                var title = window.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.NameProperty, marker)));
                if (title is null)
                {
                    Console.Error.WriteLine($"Page marker '{marker}' for '{page}' did not render.");
                    return 1;
                }

                Console.WriteLine($"ok  {page}");
            }

            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var windowPattern)
                && windowPattern is WindowPattern wp)
            {
                wp.Close();
            }

            process.WaitForExit(10_000);
            Console.WriteLine("UI smoke passed: 8 pages rendered, no destructive action invoked.");
            return 0;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static AutomationElement? WaitForWindow(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var window = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                        new PropertyCondition(AutomationElement.NameProperty, "OpenUninstaller")));
                if (window is not null)
                {
                    return window;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            Thread.Sleep(500);
        }

        return null;
    }
}
