using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using OpenUninstaller.Core.Abstractions;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Safety;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

/// <summary>
/// Lists Run/RunOnce values and Startup-folder items and can disable them reversibly. Disabled
/// registry data is copied to OpenUninstaller's own registry store; disabled Startup files are
/// moved to the local data directory. Nothing is permanently removed by disabling.
/// </summary>
public sealed class AutorunService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IRegistryStore _registry;
    private readonly AppPaths _paths;
    private readonly IOperationLog _log;
    private readonly IProcessRunner _runner;
    private readonly AutorunHistoryStore _history;

    public AutorunService(
        IRegistryStore registry,
        AppPaths paths,
        IOperationLog log,
        IProcessRunner runner,
        AutorunHistoryStore? history = null)
    {
        _registry = registry;
        _paths = paths;
        _log = log;
        _runner = runner;
        _history = history ?? new AutorunHistoryStore(paths);
    }

    public AutorunScanResult Scan()
    {
        var result = new AutorunScanResult();
        foreach (var source in RegistrySources())
        {
            IReadOnlyList<string> names;
            try
            {
                names = _registry.GetValueNames(source.Location);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                result.Warnings.Add($"{source.Location.DisplayPath}: {ex.Message}");
                continue;
            }

            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                var value = _registry.GetValue(source.Location, name);
                var command = value?.AsString();
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var approval = StartupApproval(source.Location.Hive, name,
                    source.Location.View == RegistryView.Registry32 ? "Run32" : "Run");
                result.Entries.Add(new AutorunEntry
                {
                    Id = Hashing.StableId(source.Kind.ToString(), source.Location.DisplayPath, name),
                    Name = name,
                    Command = command,
                    Source = source.Kind,
                    IsEnabled = approval.Enabled,
                    StartupApprovalLocation = approval.Location,
                    StartupApprovalValueName = name,
                    RegistryLocation = source.Location,
                    LocationDisplay = source.Location.DisplayPath,
                    Publisher = TryGetPublisher(command)
                });
            }
        }

        foreach (var folder in StartupFolders())
        {
            if (!Directory.Exists(folder.Path))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{folder.Path}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var approval = StartupApproval(folder.Kind == AutorunSourceKind.CommonStartupFolder
                    ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, name, "StartupFolder");
                result.Entries.Add(new AutorunEntry
                {
                    Id = Hashing.StableId(folder.Kind.ToString(), folder.Path, name),
                    Name = name,
                    Command = file,
                    Source = folder.Kind,
                    IsEnabled = approval.Enabled,
                    StartupApprovalLocation = approval.Location,
                    StartupApprovalValueName = name,
                    FilePath = file,
                    LocationDisplay = folder.Path,
                    Publisher = TryGetPublisher(file)
                });
            }
        }

        AddServiceEntries(result);
        AddScheduledTaskEntries(result);
        new PackagedStartupReader(_registry, _paths).AddEntries(result);
        AddDisabledEntries(result);

        var unique = new Dictionary<string, AutorunEntry>(StringComparer.Ordinal);
        foreach (var entry in result.Entries.OrderByDescending(e =>
                     e.Source is AutorunSourceKind.DisabledService
                         or AutorunSourceKind.DisabledScheduledTask
                         or AutorunSourceKind.DisabledRegistry
                         or AutorunSourceKind.DisabledStartupFolder))
        {
            if (!unique.ContainsKey(entry.Id))
            {
                unique[entry.Id] = entry;
            }
        }

        result.Entries.Clear();
        result.Entries.AddRange(unique.Values);
        for (var index = 0; index < result.Entries.Count; index++)
        {
            var entry = result.Entries[index];
            var executable = entry.ExecutablePath ?? AutorunIdentity.ExecutablePath(entry.Command);
            var publisher = entry.Publisher;
            if (entry.Source is AutorunSourceKind.MachineService or AutorunSourceKind.DisabledService
                && string.Equals(Path.GetFileName(executable), "svchost.exe", StringComparison.OrdinalIgnoreCase)
                && entry.RegistryLocation is { } service)
            {
                var parameters = service with { Path = service.Path + @"\Parameters" };
                try
                {
                    if (_registry.GetValue(parameters, "ServiceDll")?.AsString() is { } serviceDll)
                    {
                        executable = Environment.ExpandEnvironmentVariables(serviceDll.Trim('"'));
                        publisher = AutorunIdentity.Publisher(executable);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { result.Warnings.Add($"Service identity: {entry.Name}: {ex.Message}"); }
            }
            entry = entry with { ExecutablePath = executable, Publisher = publisher };
            result.Entries[index] = entry with { IsWindowsComponent = AutorunIdentity.IsWindowsComponent(entry, _paths) };
        }
        result.Entries.Sort((a, b) =>
        {
            var enabled = b.IsEnabled.CompareTo(a.IsEnabled);
            return enabled != 0 ? enabled : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return result;
    }

    public AutorunEntry? Find(string id) => Scan().Entries.FirstOrDefault(e => e.Id == id);

    public bool Disable(string id, out string? error)
    {
        error = null;
        var entry = Find(id);
        if (entry is null)
        {
            error = "Autorun entry not found.";
            return false;
        }

        if (!entry.IsEnabled)
        {
            error = "The entry is already disabled.";
            return false;
        }

        if (entry.Source == AutorunSourceKind.PackagedStartupTask)
        {
            error = "Manage this Windows app in Windows Settings > Apps > Startup.";
            return false;
        }

        try
        {
            if (entry.Source == AutorunSourceKind.MachineService)
            {
                if (entry.RegistryLocation is not { } serviceLocation)
                {
                    error = "The service has no registry location.";
                    return false;
                }

                var start = _registry.GetValue(serviceLocation, "Start")?.AsInt32() ?? 3;
                if (start == 4)
                {
                    error = "The service is already disabled.";
                    return false;
                }

                var payload = new DisabledAutorunPayload
                {
                    Name = entry.Name,
                    Command = entry.Command,
                    Kind = entry.Source.ToString(),
                    Hive = serviceLocation.Hive.ToString(),
                    View = serviceLocation.View.ToString(),
                    Path = serviceLocation.Path,
                    ServiceStart = start,
                    DisabledAt = DateTimeOffset.UtcNow
                };
                var store = DisabledStoreLocation(entry.Id);
                _registry.SetValue(store, "Payload", RegistryValueKind.String, JsonSerializer.Serialize(payload, JsonOptions));
                try
                {
                    _registry.SetValue(serviceLocation, "Start", RegistryValueKind.DWord, 4);
                }
                catch
                {
                    _registry.DeleteKey(store, recursive: true);
                    throw;
                }

                _log.Write("autorun", "disable-service", entry.Name, "completed", entry.LocationDisplay);
                RecordHistory(entry, AutorunHistoryAction.Disabled, $"Original start mode: {start}");
                return true;
            }

            if (entry.Source == AutorunSourceKind.ScheduledTask)
            {
                if (!ScheduledTaskGuard.ValidateTaskName(entry.TaskName, out var taskNameError))
                {
                    error = taskNameError;
                    return false;
                }

                var payload = new DisabledAutorunPayload
                {
                    Name = entry.Name,
                    Command = entry.Command,
                    Kind = entry.Source.ToString(),
                    TaskName = entry.TaskName,
                    FilePath = entry.FilePath,
                    DisabledAt = DateTimeOffset.UtcNow
                };
                var store = DisabledStoreLocation(entry.Id);
                _registry.SetValue(store, "Payload", RegistryValueKind.String, JsonSerializer.Serialize(payload, JsonOptions));

                if (!RunSchtasks($"/Change /TN \"{entry.TaskName}\" /DISABLE", out var taskError))
                {
                    _registry.DeleteKey(store, recursive: true);
                    error = taskError;
                    return false;
                }

                _log.Write("autorun", "disable-task", entry.Name, "completed", entry.LocationDisplay);
                RecordHistory(entry, AutorunHistoryAction.Disabled, $"Task: {entry.TaskName}");
                return true;
            }

            if (entry.RegistryLocation is { } location)
            {
                var value = _registry.GetValue(location, entry.Name);
                if (value is null || value.Data is not (string or string[]))
                {
                    error = "Only string registry values can be disabled.";
                    return false;
                }

                var payload = new DisabledAutorunPayload
                {
                    Name = entry.Name,
                    Command = entry.Command,
                    Kind = entry.Source.ToString(),
                    Hive = location.Hive.ToString(),
                    View = location.View.ToString(),
                    Path = location.Path,
                    ValueKind = value.Kind.ToString(),
                    DisabledAt = DateTimeOffset.UtcNow
                };

                var store = DisabledStoreLocation(entry.Id);
                _registry.SetValue(store, "Payload", RegistryValueKind.String, JsonSerializer.Serialize(payload, JsonOptions));
                _registry.DeleteValue(location, entry.Name);
            }
            else if (!string.IsNullOrWhiteSpace(entry.FilePath))
            {
                System.IO.Directory.CreateDirectory(_paths.DisabledStartupDirectory);
                var target = Path.Combine(
                    _paths.DisabledStartupDirectory,
                    entry.Id + "_" + Path.GetFileName(entry.FilePath));
                File.Move(entry.FilePath, target);
                var payload = new DisabledAutorunPayload
                {
                    Name = entry.Name,
                    Command = entry.Command,
                    Kind = entry.Source.ToString(),
                    FilePath = entry.FilePath,
                    DisabledPath = target,
                    DisabledAt = DateTimeOffset.UtcNow
                };
                var store = DisabledStoreLocation(entry.Id);
                _registry.SetValue(store, "Payload", RegistryValueKind.String, JsonSerializer.Serialize(payload, JsonOptions));
            }
            else
            {
                error = "The entry has no supported storage location.";
                return false;
            }

            _log.Write("autorun", "disable", entry.Name, "completed", entry.LocationDisplay);
            RecordHistory(entry, AutorunHistoryAction.Disabled, entry.LocationDisplay);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            _log.Write("autorun", "disable", entry.Name, "failed", ex.Message);
            return false;
        }
    }

    public bool Enable(string id, out string? error)
    {
        error = null;
        var entry = Find(id);
        if (entry is null)
        {
            error = "Autorun entry not found.";
            return false;
        }

        if (entry.IsEnabled)
        {
            error = "The entry is already enabled.";
            return false;
        }

        if (entry.Source == AutorunSourceKind.PackagedStartupTask)
        {
            error = "Manage this Windows app in Windows Settings > Apps > Startup.";
            return false;
        }

        if (entry.StartupApprovalLocation is { } approval && entry.StartupApprovalValueName is { } valueName
            && _registry.GetValue(approval, valueName)?.Data is byte[] { Length: >= 12 } data
            && data[0] is 3 or 7)
        {
            try
            {
                var enabledData = new byte[data.Length];
                enabledData[0] = (byte)(data[0] & ~1);
                _registry.SetValue(approval, valueName, RegistryValueKind.Binary, enabledData);
                RecordHistory(entry, AutorunHistoryAction.Enabled, "Enabled in Windows Startup approval.");
                _log.Write("autorun", "enable-startup-approved", entry.Name, "completed", entry.LocationDisplay);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        var store = DisabledStoreLocation(id);
        var raw = _registry.GetValue(store, "Payload")?.AsString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (entry.Source == AutorunSourceKind.ScheduledTask)
            {
                if (!ScheduledTaskGuard.ValidateTaskName(entry.TaskName, out var taskNameError))
                {
                    error = taskNameError;
                    return false;
                }

                if (!RunSchtasks($"/Change /TN \"{entry.TaskName}\" /ENABLE", out var taskError))
                {
                    error = taskError;
                    return false;
                }

                _log.Write("autorun", "enable-task-external", entry.Name, "completed", entry.LocationDisplay);
                RecordHistory(entry, AutorunHistoryAction.Enabled, $"Task: {entry.TaskName}");
                return true;
            }

            error = entry.Source == AutorunSourceKind.MachineService
                ? "This service was disabled outside OpenUninstaller, so its original start mode is "
                  + "not stored. Set the start mode in Windows Services and refresh."
                : "The disabled entry's backup data is missing.";
            return false;
        }

        DisabledAutorunPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DisabledAutorunPayload>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            error = "The disabled entry's backup data is corrupt.";
            return false;
        }

        if (payload is null)
        {
            error = "The disabled entry's backup data is missing.";
            return false;
        }

        try
        {
            if (payload.ServiceStart is { } originalStart)
            {
                if (!Enum.TryParse<RegistryHive>(payload.Hive, true, out var serviceHive)
                    || !Enum.TryParse<RegistryView>(payload.View, true, out var serviceView)
                    || string.IsNullOrWhiteSpace(payload.Path))
                {
                    error = "The disabled service has an invalid registry location.";
                    return false;
                }

                var serviceLocation = new RegistryLocation(serviceHive, serviceView, payload.Path);
                var current = _registry.GetValue(serviceLocation, "Start")?.AsInt32();
                if (current is null)
                {
                    error = "The service definition no longer exists.";
                    return false;
                }

                if (current != 4 && current != originalStart)
                {
                    error = "The service start mode changed while it was disabled; refusing to overwrite it.";
                    return false;
                }

                _registry.SetValue(serviceLocation, "Start", RegistryValueKind.DWord, originalStart);
            }
            else if (!string.IsNullOrWhiteSpace(payload.TaskName))
            {
                if (!ScheduledTaskGuard.ValidateTaskName(payload.TaskName, out var taskNameError))
                {
                    error = taskNameError;
                    return false;
                }

                if (!RunSchtasks($"/Change /TN \"{payload.TaskName}\" /ENABLE", out var taskError))
                {
                    error = taskError;
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(payload.FilePath) && !string.IsNullOrWhiteSpace(payload.DisabledPath))
            {
                if (File.Exists(payload.FilePath))
                {
                    error = "The original Startup item already exists; refusing to overwrite it.";
                    return false;
                }

                if (!File.Exists(payload.DisabledPath))
                {
                    error = "The disabled Startup file is missing.";
                    return false;
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(payload.FilePath)!);
                File.Move(payload.DisabledPath, payload.FilePath);
            }
            else
            {
                if (!Enum.TryParse<RegistryHive>(payload.Hive, true, out var hive)
                    || !Enum.TryParse<RegistryView>(payload.View, true, out var view))
                {
                    error = "The disabled entry has an invalid registry location.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(payload.Path))
                {
                    error = "The disabled entry has no registry path.";
                    return false;
                }

                var location = new RegistryLocation(hive, view, payload.Path);
                if (_registry.GetValue(location, payload.Name) is not null)
                {
                    error = "The original registry value already exists; refusing to overwrite it.";
                    return false;
                }

                var kind = Enum.TryParse<RegistryValueKind>(payload.ValueKind, true, out var parsedKind)
                           && parsedKind is RegistryValueKind.String or RegistryValueKind.ExpandString
                    ? parsedKind
                    : RegistryValueKind.String;
                _registry.SetValue(location, payload.Name, kind, payload.Command);
            }

            _registry.DeleteKey(store, recursive: true);
            _log.Write("autorun", "enable", payload.Name, "completed", entry.LocationDisplay);
            RecordHistory(entry, AutorunHistoryAction.Enabled, entry.LocationDisplay);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            _log.Write("autorun", "enable", payload.Name, "failed", ex.Message);
            return false;
        }
    }

    public bool Enable(AutorunEntry entry, out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Enable(entry.Id, out error);
    }

    public IReadOnlyList<AutorunHistoryEntry> GetHistory()
    {
        var records = _history.GetAll();
        var scan = Scan();
        var disabledMap = scan.Entries.Where(e => !e.IsEnabled).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var list = new List<AutorunHistoryEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            seenIds.Add(record.EntryId);
            var isCurrentDisabled = disabledMap.TryGetValue(record.EntryId, out var currentEntry);
            var canRollback = isCurrentDisabled && (
                _registry.KeyExists(DisabledStoreLocation(record.EntryId))
                || (currentEntry?.Source == AutorunSourceKind.ScheduledTask && !string.IsNullOrWhiteSpace(currentEntry.TaskName)));

            list.Add(new AutorunHistoryEntry
            {
                Id = record.Id,
                EntryId = record.EntryId,
                Name = record.Name,
                Command = record.Command,
                Source = record.Source,
                Action = record.Action,
                Timestamp = record.Timestamp,
                PreviousState = record.PreviousState,
                NewState = record.NewState,
                Detail = record.Detail,
                CanRollback = canRollback
            });
        }

        var disabledRoot = new RegistryLocation(RegistryHive.CurrentUser, RegistryView.Registry64, _paths.DisabledAutorunsRegistryPath);
        if (_registry.KeyExists(disabledRoot))
        {
            try
            {
                foreach (var subKey in _registry.GetSubKeyNames(disabledRoot))
                {
                    if (!seenIds.Contains(subKey))
                    {
                        var entry = scan.Entries.FirstOrDefault(e => string.Equals(e.Id, subKey, StringComparison.OrdinalIgnoreCase));
                        if (entry is not null)
                        {
                            list.Add(new AutorunHistoryEntry
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                EntryId = entry.Id,
                                Name = entry.Name,
                                Command = entry.Command,
                                Source = entry.Source,
                                Action = AutorunHistoryAction.Disabled,
                                Timestamp = DateTimeOffset.UtcNow,
                                PreviousState = "Enabled",
                                NewState = "Disabled",
                                Detail = "Disabled backup in OpenUninstaller",
                                CanRollback = true
                            });
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return list;
    }

    public bool Rollback(string entryId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(entryId))
        {
            error = "Entry ID is required for rollback.";
            return false;
        }

        var entry = Find(entryId);
        if (entry is null)
        {
            error = $"Autorun entry with ID '{entryId}' was not found.";
            return false;
        }

        if (entry.IsEnabled)
        {
            error = $"Autorun entry '{entry.Name}' is already enabled; no rollback needed.";
            return false;
        }

        if (!_registry.KeyExists(DisabledStoreLocation(entryId))
            && !(entry.Source == AutorunSourceKind.ScheduledTask && !string.IsNullOrWhiteSpace(entry.TaskName)))
        {
            error = $"Entry '{entry.Name}' was not disabled by OpenUninstaller and has no backup payload to roll back.";
            return false;
        }

        if (!Enable(entryId, out error))
        {
            return false;
        }

        RecordHistory(entry, AutorunHistoryAction.RolledBack, "Restored via rollback");
        _log.Write("autorun", "rollback", entry.Name, "completed", entry.LocationDisplay);
        return true;
    }

    public AutorunRollbackResult RollbackAll()
    {
        var scan = Scan();
        var candidates = scan.Entries.Where(e => !e.IsEnabled && _registry.KeyExists(DisabledStoreLocation(e.Id))).ToList();
        var result = new AutorunRollbackResult();

        if (candidates.Count == 0)
        {
            result.Messages.Add("No OpenUninstaller-disabled autorun entries to roll back.");
            return result;
        }

        foreach (var entry in candidates)
        {
            if (Enable(entry.Id, out var err))
            {
                result.SucceededCount++;
                result.Messages.Add($"Restored {entry.Name}");
                RecordHistory(entry, AutorunHistoryAction.RolledBack, "Restored via rollback all");
                _log.Write("autorun", "rollback-all-item", entry.Name, "completed", entry.LocationDisplay);
            }
            else
            {
                result.FailedCount++;
                result.Messages.Add($"Failed to restore {entry.Name}: {err}");
                _log.Write("autorun", "rollback-all-item", entry.Name, "failed", err ?? "unknown error");
            }
        }

        return result;
    }

    private void RecordHistory(AutorunEntry entry, AutorunHistoryAction action, string? details)
    {
        try
        {
            _history.Record(new AutorunHistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                EntryId = entry.Id,
                Name = entry.Name,
                Command = entry.Command,
                Source = entry.Source,
                Action = action,
                Timestamp = DateTimeOffset.UtcNow,
                PreviousState = action == AutorunHistoryAction.Disabled ? "Enabled" : "Disabled",
                NewState = action == AutorunHistoryAction.Disabled ? "Disabled" : "Enabled",
                Detail = details
            });
        }
        catch
        {
        }
    }

    private void AddServiceEntries(AutorunScanResult result)
    {
        var root = _paths.ServicesRoot;
        if (!_registry.KeyExists(root))
        {
            return;
        }

        IReadOnlyList<string> services;
        try
        {
            services = _registry.GetSubKeyNames(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            result.Warnings.Add($"Services: {ex.Message}");
            return;
        }

        foreach (var service in services.Take(5000))
        {
            var location = new RegistryLocation(root.Hive, root.View, $"{root.Path}\\{service}");
            var displayName = _registry.GetValue(location, "DisplayName")?.AsString();
            var imagePath = _registry.GetValue(location, "ImagePath")?.AsString();
            var start = _registry.GetValue(location, "Start")?.AsInt32() ?? 3;
            var serviceType = _registry.GetValue(location, "Type")?.AsInt32() ?? 16;
            if (start is 0 or 1)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(displayName) ? service : displayName;
            if (name.StartsWith("@oem", StringComparison.OrdinalIgnoreCase) && name.LastIndexOf(';') is var separator && separator > 0)
                name = name[(separator + 1)..].Trim();
            var command = string.IsNullOrWhiteSpace(imagePath) ? name : imagePath;

            result.Entries.Add(new AutorunEntry
            {
                Id = Hashing.StableId(AutorunSourceKind.MachineService.ToString(), location.DisplayPath, service),
                Name = name,
                Command = command,
                Source = AutorunSourceKind.MachineService,
                IsApplicationStartup = !string.IsNullOrWhiteSpace(imagePath) && (serviceType & 0x30) != 0 && start is 2 or 4,
                IsEnabled = start != 4,
                RegistryLocation = location,
                LocationDisplay = location.DisplayPath,
                Publisher = TryGetPublisher(command)
            });
        }
    }

    private void AddScheduledTaskEntries(AutorunScanResult result)
    {
        var existingTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemTasksDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        if (string.Equals(Path.GetFullPath(_paths.ScheduledTasksDirectory), systemTasksDirectory, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var task in ScheduledTaskInventory.Read(result.Warnings))
            {
                if (existingTasks.Add(task.Name)) AddTaskEntry(result, task.Name, task.Xml, null);
            }
        }
        if (!Directory.Exists(_paths.ScheduledTasksDirectory))
        {
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = Directory.EnumerateFiles(_paths.ScheduledTasksDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(5000).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"Scheduled tasks: {ex.Message}");
            return;
        }

        if (files.Count == 0 && existingTasks.Count == 0)
        {
            result.Warnings.Add(
                "Scheduled tasks: no task files were readable. Run as administrator to list them.");
        }

        foreach (var file in files)
        {
            var taskName = Path.GetRelativePath(_paths.ScheduledTasksDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '\\');
            if (existingTasks.Contains(taskName)) continue;
            string xml;
            try
            {
                if (new FileInfo(file).Length > 1_000_000)
                {
                    continue;
                }

                xml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            AddTaskEntry(result, taskName, xml, file);
        }
    }

    private void AddTaskEntry(AutorunScanResult result, string taskName, string xml, string? file)
    {
            var (command, arguments, enabled) = ReadTaskXml(xml);
            var commandLine = string.Join(" ", new[] { command, arguments }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

            result.Entries.Add(new AutorunEntry
            {
                Id = Hashing.StableId(
                    AutorunSourceKind.ScheduledTask.ToString(), _paths.ScheduledTasksDirectory, taskName),
                Name = taskName,
                Command = commandLine,
                Source = AutorunSourceKind.ScheduledTask,
                IsEnabled = enabled,
                FilePath = file,
                TaskName = taskName,
                LocationDisplay = $"Task Scheduler: {taskName}",
                Publisher = TryGetPublisher(command ?? string.Empty)
            });
    }

    private static (string? Command, string? Arguments, bool Enabled) ReadTaskXml(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var exec = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Exec");
            var command = exec?.Elements().FirstOrDefault(element => element.Name.LocalName == "Command")?.Value.Trim();
            var arguments = exec?.Elements().FirstOrDefault(element => element.Name.LocalName == "Arguments")?.Value.Trim();
            var enabledElement = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Settings")?
                .Elements().FirstOrDefault(element => element.Name.LocalName == "Enabled");
            var enabled = !string.Equals(enabledElement?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            return (command, arguments, enabled);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return (null, null, true);
        }
    }

    private bool RunSchtasks(string arguments, out string? error)
    {
        error = null;
        var system = Environment.SystemDirectory;
        var executable = string.IsNullOrWhiteSpace(system) ? "schtasks.exe" : Path.Combine(system, "schtasks.exe");
        ProcessResult run;
        try
        {
            run = _runner
                .RunAsync(executable, arguments, false, TimeSpan.FromSeconds(30), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }

        if (!run.Started || run.ExitCode != 0)
        {
            error = run.Error ?? $"schtasks exited with {run.ExitCode}.";
            return false;
        }

        return true;
    }

    private void AddDisabledEntries(AutorunScanResult result)
    {
        var root = new RegistryLocation(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            _paths.DisabledAutorunsRegistryPath);

        IReadOnlyList<string> ids;
        try
        {
            ids = _registry.GetSubKeyNames(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            result.Warnings.Add($"Disabled autoruns: {ex.Message}");
            return;
        }

        foreach (var id in ids)
        {
            var location = new RegistryLocation(root.Hive, root.View, $"{root.Path}\\{id}");
            var raw = _registry.GetValue(location, "Payload")?.AsString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                var payload = JsonSerializer.Deserialize<DisabledAutorunPayload>(raw, JsonOptions);
                if (payload is null)
                {
                    continue;
                }

                if (string.Equals(payload.Kind, AutorunSourceKind.MachineService.ToString(), StringComparison.Ordinal))
                {
                    if (!Enum.TryParse<RegistryHive>(payload.Hive, true, out var serviceHive)
                        || !Enum.TryParse<RegistryView>(payload.View, true, out var serviceView)
                        || string.IsNullOrWhiteSpace(payload.Path))
                    {
                        result.Warnings.Add($"Disabled service record '{id}' has an invalid location.");
                        continue;
                    }

                    var serviceLocation = new RegistryLocation(serviceHive, serviceView, payload.Path);
                    result.Entries.Add(new AutorunEntry
                    {
                        Id = id,
                        Name = payload.Name,
                        Command = payload.Command,
                        Source = AutorunSourceKind.DisabledService,
                        IsEnabled = false,
                        RegistryLocation = serviceLocation,
                        LocationDisplay = serviceLocation.DisplayPath,
                        Publisher = TryGetPublisher(payload.Command)
                    });
                    continue;
                }

                if (string.Equals(payload.Kind, AutorunSourceKind.ScheduledTask.ToString(), StringComparison.Ordinal))
                {
                    result.Entries.Add(new AutorunEntry
                    {
                        Id = id,
                        Name = payload.Name,
                        Command = payload.Command,
                        Source = AutorunSourceKind.DisabledScheduledTask,
                        IsEnabled = false,
                        FilePath = payload.FilePath,
                        TaskName = payload.TaskName,
                        LocationDisplay = string.IsNullOrWhiteSpace(payload.TaskName)
                            ? "Task Scheduler"
                            : $"Task Scheduler: {payload.TaskName}",
                        Publisher = TryGetPublisher(payload.Command)
                    });
                    continue;
                }

                var isStartup = !string.IsNullOrWhiteSpace(payload.DisabledPath);
                result.Entries.Add(new AutorunEntry
                {
                    Id = id,
                    Name = payload.Name,
                    Command = payload.Command,
                    Source = isStartup ? AutorunSourceKind.DisabledStartupFolder : AutorunSourceKind.DisabledRegistry,
                    IsEnabled = false,
                    LocationDisplay = payload.Path ?? payload.FilePath ?? "",
                    Publisher = TryGetPublisher(payload.Command)
                });
            }
            catch (JsonException)
            {
                result.Warnings.Add($"Ignored corrupt disabled autorun record '{id}'.");
            }
        }
    }

    private RegistryLocation DisabledStoreLocation(string id)
        => new(RegistryHive.CurrentUser, RegistryView.Registry64, $"{_paths.DisabledAutorunsRegistryPath}\\{id}");

    private IEnumerable<(AutorunSourceKind Kind, RegistryLocation Location)> RegistrySources()
    {
        yield return (AutorunSourceKind.UserRun, new RegistryLocation(
            RegistryHive.CurrentUser, RegistryView.Registry64,
            _paths.AutorunRegistryPath + @"\Run"));
        yield return (AutorunSourceKind.UserRunOnce, new RegistryLocation(
            RegistryHive.CurrentUser, RegistryView.Registry64,
            _paths.AutorunRegistryPath + @"\RunOnce"));
        yield return (AutorunSourceKind.MachineRun, new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64,
            _paths.AutorunRegistryPath + @"\Run"));
        yield return (AutorunSourceKind.MachineRun, new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry32,
            _paths.AutorunRegistryPath + @"\Run"));
        yield return (AutorunSourceKind.MachineRunOnce, new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry64,
            _paths.AutorunRegistryPath + @"\RunOnce"));
        yield return (AutorunSourceKind.MachineRunOnce, new RegistryLocation(
            RegistryHive.LocalMachine, RegistryView.Registry32,
            _paths.AutorunRegistryPath + @"\RunOnce"));
    }

    private IEnumerable<(AutorunSourceKind Kind, string Path)> StartupFolders()
    {
        yield return (AutorunSourceKind.UserStartupFolder, Path.Combine(
            _paths.AppData, @"Microsoft\Windows\Start Menu\Programs\Startup"));
        yield return (AutorunSourceKind.CommonStartupFolder, Path.Combine(
            _paths.ProgramData, @"Microsoft\Windows\Start Menu\Programs\Startup"));
    }

    private (RegistryLocation? Location, bool Enabled) StartupApproval(RegistryHive sourceHive, string name, string category)
    {
        foreach (var hive in sourceHive == RegistryHive.CurrentUser
                     ? new[] { RegistryHive.CurrentUser } : new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            var location = new RegistryLocation(hive, RegistryView.Registry64, $"{_paths.StartupApprovedRegistryPath}\\{category}");
            try
            {
                if (_registry.GetValue(location, name)?.Data is byte[] { Length: >= 12 } data && data[0] is 2 or 3 or 6 or 7)
                    return (location, (data[0] & 1) == 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return (null, true);
    }

    private static string? TryGetPublisher(string command)
        => AutorunIdentity.Publisher(AutorunIdentity.ExecutablePath(command));

    private sealed record DisabledAutorunPayload
    {
        public required string Name { get; init; }
        public required string Command { get; init; }
        public required string Kind { get; init; }
        public string? Hive { get; init; }
        public string? View { get; init; }
        public string? Path { get; init; }
        public string? ValueKind { get; init; }
        public string? FilePath { get; init; }
        public string? DisabledPath { get; init; }
        public string? TaskName { get; init; }
        public int? ServiceStart { get; init; }
        public DateTimeOffset DisabledAt { get; init; }
    }
}
