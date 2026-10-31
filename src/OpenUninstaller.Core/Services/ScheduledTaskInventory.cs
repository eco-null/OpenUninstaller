using System.Runtime.InteropServices;

namespace OpenUninstaller.Core.Services;

/// <summary>Read-only Task Scheduler enumeration using the caller's existing permissions.</summary>
internal static class ScheduledTaskInventory
{
    public static IReadOnlyList<(string Name, string Xml)> Read(List<string> warnings)
    {
        var result = new List<(string Name, string Xml)>();
        object? service = null;
        object? root = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return result;
            service = Activator.CreateInstance(type);
            if (service is null) return result;
            ((dynamic)service).Connect();
            root = ((dynamic)service).GetFolder(@"\");
            ReadFolder(root, result, warnings, 0);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or System.Security.SecurityException)
        { warnings.Add($"Task Scheduler: {ex.Message}"); }
        finally { Release(root); Release(service); }
        return result;
    }

    private static void ReadFolder(object folder, List<(string Name, string Xml)> result, List<string> warnings, int depth)
    {
        if (depth > 32 || result.Count >= 5000) return;
        object? tasks = null;
        object? folders = null;
        try
        {
            tasks = ((dynamic)folder).GetTasks(1); // Include disabled and hidden tasks.
            var count = Math.Min((int)((dynamic)tasks).Count, 5000 - result.Count);
            for (var index = 1; index <= count; index++)
            {
                object? task = null;
                try
                {
                    task = ((dynamic)tasks).Item(index);
                    string name = ((dynamic)task).Path;
                    string xml = ((dynamic)task).Xml;
                    if (xml.Length <= 1_000_000) result.Add((name.TrimStart('\\'), xml));
                }
                catch (COMException) { /* A protected task must not hide its readable siblings. */ }
                finally { Release(task); }
            }
            folders = ((dynamic)folder).GetFolders(0);
            var folderCount = Math.Min((int)((dynamic)folders).Count, 5000);
            for (var index = 1; index <= folderCount && result.Count < 5000; index++)
            {
                object? child = null;
                try { child = ((dynamic)folders).Item(index); ReadFolder(child, result, warnings, depth + 1); }
                catch (COMException) { }
                finally { Release(child); }
            }
        }
        catch (COMException ex) { warnings.Add($"Task Scheduler folder: {ex.Message}"); }
        finally { Release(folders); Release(tasks); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
