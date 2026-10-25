using System.Runtime.InteropServices;
using System.Text;

namespace OpenUninstaller.Core.Support;

/// <summary>
/// Windows-aware command line tokenizer built on <c>CommandLineToArgvW</c>, with a deterministic
/// fallback for malformed input. Product code never builds a shell string from these tokens; the
/// resolver passes the executable and arguments to the process API separately.
/// </summary>
public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var trimmed = commandLine.Trim();
        try
        {
            var argv = CommandLineToArgvW(trimmed, out var count);
            if (argv != IntPtr.Zero && count > 0)
            {
                try
                {
                    var result = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                        result[i] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                    }

                    return result;
                }
                finally
                {
                    LocalFree(argv);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Fall through to the simple parser below.
        }

        return SimpleSplit(trimmed);
    }

    private static List<string> SimpleSplit(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
