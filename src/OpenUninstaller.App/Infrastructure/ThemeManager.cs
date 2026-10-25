using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenUninstaller.App.Infrastructure;

public static class ThemeManager
{
    private const int UseImmersiveDarkMode = 20;

    public static string CurrentTheme { get; private set; } = "Dark";

    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var normalized = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        CurrentTheme = normalized;
        var uri = new Uri($"Themes/{normalized}.xaml", UriKind.Relative);
        var dictionaries = app.Resources.MergedDictionaries;
        var replaced = false;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? string.Empty;
            if (source.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || source.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[i] = new ResourceDictionary { Source = uri };
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            dictionaries.Insert(0, new ResourceDictionary { Source = uri });
        }

        foreach (Window window in app.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = CurrentTheme == "Dark" ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows builds do not support the attribute; the standard chrome remains.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The window may not have a native handle yet; the standard chrome remains.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
