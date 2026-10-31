using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace OpenUninstaller.UiSmoke;

/// <summary>
/// Read-only UI smoke test using the built-in UI Automation API (no WinAppDriver dependency).
/// It launches the app, visits every navigation page, verifies the page title rendered and closes
/// the window. It never invokes uninstall, cleanup or service actions.
/// </summary>
internal static class Program
{
    private static readonly (string Nav, string AutomationId)[] Pages =
    [
        ("Programs", "ProgramsPage"),
        ("Autoruns", "AutorunsPage"),
        ("Junk files", "JunkPage"),
        ("Privacy & shredder", "PrivacyPage"),
        ("Store apps", "StoreAppsPage"),
        ("Backups", "BackupsPage"),
        ("Windows tools", "WindowsToolsPage"),
        ("Settings", "SettingsPage")
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
            var window = WaitForWindow(process.Id, TimeSpan.FromSeconds(30));
            if (window is null)
            {
                Console.Error.WriteLine("The main window did not appear.");
                return 1;
            }

            var screenshotDirectory = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
            if (screenshotDirectory is not null) Directory.CreateDirectory(screenshotDirectory);
            var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
            var themePicker = Find(window, ControlType.List, "Color theme")
                ?? throw new InvalidOperationException("The shell theme selector is missing.");
            var originalTheme = ((SelectionPattern)themePicker.GetCurrentPattern(SelectionPattern.Pattern))
                .Current.GetSelection().FirstOrDefault()?.Current.Name ?? "Dark";
            foreach (var (width, height, size) in new[] { (1440d, 860d, "desktop"), (1040d, 620d, "compact") })
            {
                transform.Resize(width, height);
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    Select(Find(themePicker, ControlType.ListItem, theme)
                        ?? throw new InvalidOperationException($"Theme '{theme}' is missing."));
                    foreach (var (page, automationId) in Pages)
                    {
                        var item = Find(window, ControlType.ListItem, page)
                            ?? throw new InvalidOperationException($"Navigation entry '{page}' is missing.");
                        Select(item);
                        var pageRoot = WaitFor(() => window.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)));
                        var title = pageRoot is null ? null : WaitFor(() => Find(pageRoot, ControlType.Text, page));
                        foreach (var removedLabel in new[] { "Local workspace", "Windows application manager", "100% Offline · No telemetry · Zero external connections" })
                            if (Find(window, ControlType.Text, removedLabel) is not null)
                                throw new InvalidOperationException($"Removed shell label is still present: {removedLabel}");
                        if (title is null || title.Current.IsOffscreen)
                            throw new InvalidOperationException($"'{page}' did not render at {size}/{theme}.");
                        if (process.HasExited) throw new InvalidOperationException($"The app exited on '{page}'.");
                        if (screenshotDirectory is not null)
                        {
                            // Wait for an initial inventory scan to complete before capturing its result.
                            var deadline = DateTime.UtcNow.AddSeconds(20);
                            while (DateTime.UtcNow < deadline && window.FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar)) is { } progress
                                && !progress.Current.IsOffscreen) Thread.Sleep(100);
                            Capture(window, Path.Combine(screenshotDirectory,
                                $"{size}-{theme.ToLowerInvariant()}-{page.Split(' ')[0].ToLowerInvariant()}.png"));
                        }
                        Console.WriteLine($"ok  {size}/{theme}/{page}");
                    }
                    Select(Find(window, ControlType.ListItem, "Programs")!);
                    var search = WaitFor(() => Find(window, ControlType.Edit, "Search programs"))!;
                    ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("__ui_smoke_no_such_application_64f28__");
                    var empty = WaitFor(() => Find(window, ControlType.Text, "No applications to show"));
                    if (empty is null || empty.Current.IsOffscreen)
                        throw new InvalidOperationException("The no-results state did not render.");
                    ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("");
                    Console.WriteLine($"ok  {size}/{theme}/search-empty-and-reset");
                    var row = WaitFor(() => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
                    if (row is not null)
                    {
                        Select(row);
                        if (Find(window, ControlType.Button, "Uninstall\u2026") is null)
                            throw new InvalidOperationException("Selecting a program did not expose inspector actions.");
                        if (screenshotDirectory is not null)
                            Capture(window, Path.Combine(screenshotDirectory, $"{size}-{theme.ToLowerInvariant()}-programs-selected.png"));
                        Console.WriteLine($"ok  {size}/{theme}/selection-inspector");
                    }
                }
            }
            Select(Find(themePicker, ControlType.ListItem, originalTheme)!);

            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var windowPattern)
                && windowPattern is WindowPattern wp)
            {
                wp.Close();
            }

            process.WaitForExit(10_000);
            Console.WriteLine("UI smoke passed: 8 pages x 2 themes x 2 sizes, plus search empty/reset and selection inspector; no destructive action invoked.");
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

    private static AutomationElement? Find(AutomationElement parent, ControlType type, string name) =>
        parent.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name)));

    private static AutomationElement? WaitFor(Func<AutomationElement?> find)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            var element = find();
            if (element is not null && !element.Current.IsOffscreen) return element;
            Thread.Sleep(80);
        }
        return null;
    }

    private static void Select(AutomationElement element)
    {
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(180);
    }

    private static void Capture(AutomationElement window, string path)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        GetWindowRect(handle, out var rect);
        var dc = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!PrintWindow(handle, memory, 2)) throw new InvalidOperationException("Window capture failed.");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private static AutomationElement? WaitForWindow(int processId, TimeSpan timeout)
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
                        new PropertyCondition(AutomationElement.NameProperty, "OpenUninstaller"),
                        new PropertyCondition(AutomationElement.ProcessIdProperty, processId)));
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
