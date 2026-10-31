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
        if (args is ["--verify-sorting"]) return SortingChecks.Run();
        if (args is ["--verify-layout"]) return LayoutChecks.Run();
        if (LayoutChecks.Run() != 0) return 1;
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
                var columns = ActualCardColumns(window);
                Console.WriteLine($"Window {size}: requested {width}x{height}, actual {window.Current.BoundingRectangle}; expecting {columns} card column(s).");
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
                        if (page is "Settings" or "Privacy & shredder")
                        {
                            var firstHeading = page == "Settings" ? "Appearance" : "Windows history and app state";
                            var secondHeading = page == "Settings" ? "Background maintenance service" : "Shred files permanently";
                            VerifyCardColumns(pageRoot!, firstHeading, secondHeading, columns);
                        }
                        if (page == "Autoruns") VerifyAutorunModes(pageRoot!);
                        if (page is "Programs" or "Autoruns" or "Store apps")
                            VerifySortPicker(pageRoot!, page switch
                            {
                                "Programs" => "Sort programs",
                                "Autoruns" => "Sort autoruns",
                                _ => "Sort store apps"
                            });
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
            transform.Resize(1920, 1080);
            var wideColumns = ActualCardColumns(window);
            foreach (var page in new[] { "Settings", "Privacy & shredder" })
            {
                Select(Find(window, ControlType.ListItem, page)!);
                var first = page == "Settings" ? "Appearance" : "Windows history and app state";
                var second = page == "Settings" ? "Background maintenance service" : "Shred files permanently";
                VerifyCardColumns(window, first, second, wideColumns);
                if (screenshotDirectory is not null) Capture(window, Path.Combine(screenshotDirectory, $"wide-{page.Split(' ')[0].ToLowerInvariant()}.png"));
                Console.WriteLine($"ok  wide/{page}/{wideColumns}-column(s) at actual window width");
            }
            Select(Find(themePicker, ControlType.ListItem, originalTheme)!);

            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var windowPattern)
                && windowPattern is WindowPattern wp)
            {
                wp.Close();
            }

            process.WaitForExit(10_000);
            Console.WriteLine("UI smoke passed: page/theme/size navigation, responsive cards, Basic/Advanced/history modes and search/selection; no destructive action invoked.");
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

    private static void VerifyCardColumns(AutomationElement root, string first, string second, int columns)
    {
        var a = Find(root, ControlType.Text, first)?.Current.BoundingRectangle
            ?? throw new InvalidOperationException($"Card '{first}' is missing.");
        var b = Find(root, ControlType.Text, second)?.Current.BoundingRectangle
            ?? throw new InvalidOperationException($"Card '{second}' is missing.");
        var sideBySide = Math.Abs(a.Top - b.Top) < 3 && b.Left > a.Left + 100;
        if ((columns > 1) != sideBySide)
            throw new InvalidOperationException($"Expected {columns} column(s) for '{first}': first={a}, second={b}.");
    }

    private static int ActualCardColumns(AutomationElement window)
    {
        // UI Automation resize requests can be capped by the runner's desktop.
        // Use the target's client size and DPI, not the requested physical pixels.
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        if (!GetClientRect(handle, out var client)) throw new InvalidOperationException("Cannot read window client size.");
        var logicalWidth = (client.Right - client.Left) * 96d / GetDpiForWindow(handle);
        const double sidebar = 216, pageMargins = 56, scrollbar = 17;
        return logicalWidth - sidebar - pageMargins - scrollbar >= 980 ? 2 : 1;
    }

    private static void VerifyAutorunModes(AutomationElement page)
    {
        var picker = Find(page, ControlType.List, "Autorun mode")
            ?? throw new InvalidOperationException("Basic/Advanced mode selector is missing.");
        var search = Find(page, ControlType.Edit, "Search autoruns")!;
        foreach (var mode in new[] { "Basic", "Advanced", "History & rollback", "Basic" })
        {
            Select(Find(picker, ControlType.ListItem, mode)!);
            var selected = ((SelectionPattern)picker.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection();
            if (selected.Length != 1 || selected[0].Current.Name != mode)
                throw new InvalidOperationException($"Autorun mode '{mode}' did not activate.");
            if (mode == "History & rollback") VerifySortPicker(page, "Sort autorun history");
        }
        ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("__no_autorun_matches__");
        Select(Find(picker, ControlType.ListItem, "Advanced")!);
        if (((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "__no_autorun_matches__")
            throw new InvalidOperationException("Autorun search was lost when switching modes.");
        ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("");
        Select(Find(picker, ControlType.ListItem, "Basic")!);
    }

    private static void VerifySortPicker(AutomationElement page, string name)
    {
        var picker = Find(page, ControlType.ComboBox, name)
            ?? throw new InvalidOperationException($"Sort picker '{name}' is missing.");
        if (picker.Current.IsOffscreen || !picker.Current.IsEnabled)
            throw new InvalidOperationException($"Sort picker '{name}' is inaccessible.");
        var expand = (ExpandCollapsePattern)picker.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var choices = picker.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (choices.Count < 2) throw new InvalidOperationException($"Sort picker '{name}' has no choices.");
        Select(choices[1]);
        var selected = ((SelectionPattern)picker.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection();
        if (selected.Length != 1 || selected[0].Current.Name != choices[1].Current.Name)
            throw new InvalidOperationException($"Sort picker '{name}' did not change.");
        expand.Expand();
        Select(choices[0]);
        expand.Collapse();
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
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
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
