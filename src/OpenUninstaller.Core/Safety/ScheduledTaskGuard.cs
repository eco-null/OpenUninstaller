using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Safety;

/// <summary>
/// Task Scheduler names are hierarchical paths. They are passed to <c>schtasks.exe</c>, so a task
/// name must never be able to escape the task store or inject an argument. The XML file must also
/// live under the configured Tasks directory.
/// </summary>
public static class ScheduledTaskGuard
{
    public static bool ValidateTaskName(string? taskName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(taskName))
        {
            error = "The scheduled task has no name.";
            return false;
        }

        var name = taskName.Trim();
        if (name.Length > 500)
        {
            error = "The scheduled task name is too long.";
            return false;
        }

        if (name.StartsWith('\\') || name.EndsWith('\\'))
        {
            error = "The scheduled task name may not start or end with a backslash.";
            return false;
        }

        var segments = name.Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                error = "The scheduled task name contains an empty or parent segment.";
                return false;
            }

            if (segment.Any(ch => char.IsControl(ch) || "<>:\"/|?*".Contains(ch)))
            {
                error = "The scheduled task name contains invalid characters.";
                return false;
            }
        }

        return true;
    }

    public static GuardResult ValidateForDeletion(string taskName, string tasksRoot, string xmlPath)
    {
        if (!ValidateTaskName(taskName, out var nameError))
        {
            return GuardResult.Reject(nameError ?? "Invalid scheduled task name.");
        }

        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.GetFullPath(tasksRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullPath = Path.GetFullPath(xmlPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardResult.Reject("The scheduled task path is not valid.");
        }

        if (!PathGuard.IsUnder(fullPath, fullRoot))
        {
            return GuardResult.Reject("The scheduled task XML is outside the Task Scheduler store.");
        }

        if (!File.Exists(fullPath))
        {
            return GuardResult.Reject("The scheduled task XML no longer exists.");
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return GuardResult.Reject("Reparse points are never deleted.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GuardResult.Reject("Could not verify the scheduled task file.");
        }

        return GuardResult.Allow(fullPath);
    }

    public static string XmlPath(string tasksRoot, string taskName)
        => Path.Combine(tasksRoot, taskName.Replace('\\', Path.DirectorySeparatorChar));
}
