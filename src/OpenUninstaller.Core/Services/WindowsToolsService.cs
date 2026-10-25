using System.Diagnostics;
using System.Text.Json;
using OpenUninstaller.Core.Models;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Core.Services;

public sealed class WindowsToolsService
{
    private static readonly WindowsTool[] BuiltInTools =
    [
        new()
        {
            Id = "system-restore",
            Name = "System Restore",
            Command = "rstrui.exe",
            Description = "Restore computer to a previous system restore point.",
            Category = "System",
            Glyph = "\uE777",
            RequiresElevation = true
        },
        new()
        {
            Id = "network-diagnostics",
            Name = "Network Diagnostics",
            Command = "msdt.exe",
            Arguments = "/id NetworkDiagnosticsWeb",
            Description = "Scan and troubleshoot local and internet network connectivity.",
            Category = "Network",
            Glyph = "\uE968",
            RequiresElevation = false
        },
        new()
        {
            Id = "network-information",
            Name = "Network Information",
            Command = "cmd.exe",
            Arguments = "/k ipconfig /all",
            Description = "Display complete TCP/IP network configuration, IP address, DHCP, and DNS.",
            Category = "Network",
            Glyph = "\uE839",
            RequiresElevation = false
        },
        new()
        {
            Id = "security-center",
            Name = "Security Center",
            Command = "wscui.cpl",
            Description = "Check Windows Firewall, update status, and malware protection.",
            Category = "Security",
            Glyph = "\uEA18",
            RequiresElevation = false
        },
        new()
        {
            Id = "system-properties",
            Name = "System Properties",
            Command = "sysdm.cpl",
            Description = "View hardware summary, computer name, device manager, and performance settings.",
            Category = "System",
            Glyph = "\uE7F4",
            RequiresElevation = false
        },
        new()
        {
            Id = "system-information",
            Name = "System Information",
            Command = "msinfo32.exe",
            Description = "Comprehensive hardware, peripheral, system component and software environment report.",
            Category = "Diagnostics",
            Glyph = "\uE946",
            RequiresElevation = false
        },
        new()
        {
            Id = "tcpip-netstat",
            Name = "TCP/IP Netstat",
            Command = "cmd.exe",
            Arguments = "/k netstat -a -b",
            Description = "List active TCP connections, listening ports, and owning processes.",
            Category = "Network",
            Glyph = "\uE753",
            RequiresElevation = true
        },
        new()
        {
            Id = "onscreen-keyboard",
            Name = "On-Screen Keyboard",
            Command = "osk.exe",
            Description = "Virtual accessibility keyboard for mouse-driven or touch input.",
            Category = "System",
            Glyph = "\uE765",
            RequiresElevation = false
        },
        new()
        {
            Id = "disk-defragmenter",
            Name = "Disk Defragmenter",
            Command = "dfrgui.exe",
            Description = "Optimize and defragment HDD and SSD storage drives.",
            Category = "Disk & Storage",
            Glyph = "\uE8B7",
            RequiresElevation = true
        },
        new()
        {
            Id = "services",
            Name = "Services",
            Command = "services.msc",
            Description = "Inspect, start, stop, and configure Windows background services.",
            Category = "System",
            Glyph = "\uE90F",
            RequiresElevation = true
        },
        new()
        {
            Id = "shared-folders",
            Name = "Shared Folders",
            Command = "fsmgmt.msc",
            Description = "Manage network file shares, active user sessions, and open files.",
            Category = "Network",
            Glyph = "\uE8B7",
            RequiresElevation = true
        },
        new()
        {
            Id = "group-policy",
            Name = "Group Policy Editor",
            Command = "gpedit.msc",
            Description = "Configure local security policies, system behavior, and user rights.",
            Category = "System",
            Glyph = "\uE71D",
            RequiresElevation = true
        },
        new()
        {
            Id = "windows-components",
            Name = "Add/Remove Windows Components",
            Command = "OptionalFeatures.exe",
            Description = "Enable or disable optional Windows features and subsystem components.",
            Category = "System",
            Glyph = "\uE7B8",
            RequiresElevation = true
        },
        new()
        {
            Id = "malicious-removal-tool",
            Name = "Malicious Software Removal Tool",
            Command = "mrt.exe",
            Description = "Microsoft on-demand scanner to detect and remove prevalent malware.",
            Category = "Security",
            Glyph = "\uE890",
            RequiresElevation = true
        },
        new()
        {
            Id = "backup-and-restore",
            Name = "Windows Backup and Restore",
            Command = "sdclt.exe",
            Description = "Create system image backups and manage Windows backup archives.",
            Category = "System",
            Glyph = "\uE775",
            RequiresElevation = true
        },
        new()
        {
            Id = "task-scheduler",
            Name = "Task Scheduler",
            Command = "taskschd.msc",
            Arguments = "/s",
            Description = "Configure scheduled automated jobs, maintenance routines, and triggers.",
            Category = "System",
            Glyph = "\uE823",
            RequiresElevation = true
        },
        new()
        {
            Id = "check-disk",
            Name = "Check Disk",
            Command = "cmd.exe",
            Arguments = "/k chkdsk.exe",
            Description = "Verify file system metadata integrity and detect disk errors.",
            Category = "Disk & Storage",
            Glyph = "\uE74D",
            RequiresElevation = true
        },
        new()
        {
            Id = "system-file-checker",
            Name = "System File Checker",
            Command = "cmd.exe",
            Arguments = "/k sfc.exe /scannow",
            Description = "Scan and repair damaged, corrupted, or replaced Windows system files.",
            Category = "System",
            Glyph = "\uE9F5",
            RequiresElevation = true
        },
        new()
        {
            Id = "disk-cleanup",
            Name = "Disk Cleanup",
            Command = "cleanmgr.exe",
            Description = "Built-in Windows tool to safely remove temporary and obsolete system files.",
            Category = "Disk & Storage",
            Glyph = "\uE74D",
            RequiresElevation = false
        },
        new()
        {
            Id = "resource-monitor",
            Name = "Resource Monitor",
            Command = "resmon.exe",
            Description = "Real-time process monitoring for CPU, RAM, disk I/O, and network activity.",
            Category = "Diagnostics",
            Glyph = "\uE9D9",
            RequiresElevation = false
        },
        new()
        {
            Id = "dism-health",
            Name = "DISM Component Cleanup",
            Command = "cmd.exe",
            Arguments = "/k dism.exe /Online /Cleanup-Image /RestoreHealth",
            Description = "Deploy Image Servicing: verify and repair Windows system image health.",
            Category = "System",
            Glyph = "\uE895",
            RequiresElevation = true
        },
        new()
        {
            Id = "flush-dns",
            Name = "Flush DNS Cache",
            Command = "cmd.exe",
            Arguments = "/k ipconfig /flushdns",
            Description = "Clear and flush the Windows local DNS resolver cache.",
            Category = "Network",
            Glyph = "\uE753",
            RequiresElevation = false
        },
        new()
        {
            Id = "directx-diagnostics",
            Name = "DirectX Diagnostic",
            Command = "dxdiag.exe",
            Description = "Inspect DirectX graphics display drivers, sound devices, and acceleration.",
            Category = "Diagnostics",
            Glyph = "\uE786",
            RequiresElevation = false
        },
        new()
        {
            Id = "task-manager",
            Name = "Task Manager",
            Command = "taskmgr.exe",
            Description = "Processes, startup impact, and resource performance graphs.",
            Category = "Diagnostics",
            Glyph = "\uE9D9",
            RequiresElevation = false
        },
        new()
        {
            Id = "registry-editor",
            Name = "Registry Editor",
            Command = "regedit.exe",
            Description = "Windows system registry editor. Use with caution.",
            Category = "System",
            Glyph = "\uE71C",
            RequiresElevation = true
        },
        new()
        {
            Id = "programs-features",
            Name = "Programs and Features",
            Command = "appwiz.cpl",
            Description = "Legacy Windows Control Panel application management.",
            Category = "System",
            Glyph = "\uE7B8",
            RequiresElevation = false
        }
    ];

    private readonly AppPaths _paths;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public WindowsToolsService(AppPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<WindowsTool> GetBuiltInTools() => BuiltInTools;

    public IReadOnlyList<WindowsTool> GetAllTools()
    {
        var list = new List<WindowsTool>(BuiltInTools);
        list.AddRange(LoadCustomTools());
        return list;
    }

    public IReadOnlyList<WindowsTool> LoadCustomTools()
    {
        var filePath = CustomToolsFilePath();
        if (!File.Exists(filePath))
        {
            return [];
        }

        lock (_lock)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var items = JsonSerializer.Deserialize<List<WindowsTool>>(json);
                return items ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void SaveCustomTools(IReadOnlyList<WindowsTool> tools)
    {
        var filePath = CustomToolsFilePath();
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        lock (_lock)
        {
            var json = JsonSerializer.Serialize(tools, JsonOptions);
            File.WriteAllText(filePath, json);
        }
    }

    public void AddCustomTool(WindowsTool tool)
    {
        var current = LoadCustomTools().ToList();
        current.RemoveAll(t => t.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase));
        current.Add(tool with { IsCustom = true });
        SaveCustomTools(current);
    }

    public bool RemoveCustomTool(string toolId)
    {
        var current = LoadCustomTools().ToList();
        var removed = current.RemoveAll(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            SaveCustomTools(current);
        }

        return removed;
    }

    public ProcessStartInfo CreateStartInfo(WindowsTool tool)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool.Command,
            UseShellExecute = true
        };

        if (!string.IsNullOrWhiteSpace(tool.Arguments))
        {
            startInfo.Arguments = tool.Arguments;
        }

        return startInfo;
    }

    private string CustomToolsFilePath() => Path.Combine(_paths.DataDirectory, "custom-tools.json");
}
