# OpenUninstaller

<div align="center">

<img src="src/OpenUninstaller.App/Assets/app.png" alt="OpenUninstaller icon" width="128">

<h1>OpenUninstaller</h1>

<p><strong>A high-performance, fully open-source Windows application manager, deep uninstaller, and system optimization utility.</strong></p>

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-blue.svg)](#system-requirements)
[![Framework: .NET 8 / 9](https://img.shields.io/badge/Framework-.NET%208.0%20%7C%209.0-purple.svg)](https://dotnet.microsoft.com/)
[![Tests: 117 Passing](https://img.shields.io/badge/Tests-117%20Passing-brightgreen.svg)](#automated-testing)
[![Offline: 100%](https://img.shields.io/badge/Telemetry-Zero%20%2F%20100%25%20Offline-success.svg)](#100-offline--privacy-guarantee)
[![Clean Architecture](https://img.shields.io/badge/Architecture-Clean%20%2F%20Zero%20Dependencies-informational.svg)](#architecture)

<p>
  <a href="#features">Features</a> •
  <a href="#quick-start">Quick Start</a> •
  <a href="#installation">Installation</a> •
  <a href="#command-line-interface-oucli">CLI Reference</a> •
  <a href="#architecture">Architecture</a> •
  <a href="#safety-first-architecture">Safety Guarantees</a> •
  <a href="#contributing">Contributing</a> •
  <a href="#license">License</a>
</p>

</div>

---

## Overview

When standard Windows programs are uninstalled via the default "Add/Remove Programs" applet or vendor uninstallers, significant remnants are frequently left behind: orphaned registry keys, orphaned services, scheduled tasks, hidden `%APPDATA%` and `%LOCALAPPDATA%` trees, caches, and dead shortcut links.

**OpenUninstaller** is an independent, 100% free, MIT-licensed deep uninstaller and system maintenance suite designed from the ground up for Windows 10 and 11. It executes vendor uninstallers, performs heuristic and differential leftover scans, creates verified pre-deletion backups, and safely eliminates approved remnants.

### Core Highlights

- **Leftover Scan:** Find and review application remnants using installation traces and heuristic matching, with confidence scores and safety guards.
- **100% Offline & Private:** Zero telemetry, zero analytics, zero update beacons, and zero network calls. All operations occur strictly locally.
- **Fail-Safe Protection (`PathGuard` & `RegistryGuard`):** Strict, hard-coded kernel and system protection guards prevent accidental modification or deletion of operating system files or registry keys.
- **Mandatory Pre-Deletion Backups:** Every file and registry key scheduled for deletion is automatically archived into a timestamped, compressed backup with SHA-256 manifests and one-click rollback.
- **Zero Third-Party Runtime Dependencies:** Pure, modern C# 13 and .NET 8/9 with WPF. No third-party packages or bloat in runtime assemblies.

---

## Features

| Feature | Description |
| :--- | :--- |
| **Leftover Scan** | Scan for remaining files, folders, registry entries, shortcuts, services, and scheduled tasks using trace evidence and heuristic matching. Review candidates before backed-up deletion. |
| **Deep Clean Uninstaller** | Comprehensive registry and filesystem scanning to locate and eliminate orphaned vendor keys, folders, AppData, ProgramData, and shortcuts. |
| **Installation Tracer** | Real-time registry and filesystem differential snapshotting (Schema 2 value-level precision) before and after running setup executables. Export and import portable `.outrace.zip` packages. |
| **Windows Apps & MSIX Manager** | Native management of modern Windows Store, UWP, and MSIX packages. Support for app termination, data reset, package removal, and manifest location discovery. |
| **Startup & Autorun Manager** | Inspect and toggle Run/RunOnce registry entries, Startup folders, services, and scheduled tasks. Full audit logging with reversible rollback history. |
| **Junk & Temporary File Cleaner** | Scan and clean temporary directories, application caches, crash dumps, and pattern remnants without affecting active user data. |
| **Privacy & History Cleaner** | Clean Windows Explorer run history, recent documents, typed paths, and thumbnail caches with typed safety confirmation. |
| **Evidence Remover (Free Space Wiper)** | Securely wipe unallocated free disk space with multi-pass zero-pattern sector overwriting to prevent recovery of previously deleted confidential data. |
| **23 Built-in Windows Tools** | Quick-launcher dashboard for essential native Windows administration and diagnostic consoles (Disk Management, Services, Event Viewer, Group Policy, etc.). |
| **Hunter Mode** | Interactive floating crosshair desktop widget. Drag and drop onto any open window to immediately reveal its executable, owning process, and uninstall package. |
| **File Shredder** | Cryptographic multi-pass file and directory shredder (overwrite, flush, truncate, rename, delete) for unrecoverable data destruction. |
| **Backup & Rollback Engine** | Full pre-deletion backup archives with SHA-256 manifests, `.reg` export files, and one-click rollback restore functionality. |
| **Optional Maintenance Service** | An optional, self-contained Windows Service for automated backup retention, audit log rotation, and scan history housekeeping. |

### Leftover Scan

After an uninstall, OpenUninstaller scans for remaining application data. When an installation trace is available, the scan combines exact trace evidence with heuristic matches. Results show confidence and reasons, protect guarded items, and let you review candidates before deletion. You can also ignore items in future scans and compare results with the previous scan. Deletion requires confirmation and a backup.

The CLI can scan without uninstalling: `oucli scan-leftovers "Example Application" --json`. Add `--diff` to compare with the previous scan. See [Leftover and Junk Algorithms](docs/ALGORITHMS.md) and the [User Guide](docs/USER-GUIDE.md#leftover-review) for details.

---

## Quick Start

### Build from Source

```powershell
# Clone the repository
git clone https://github.com/eco-null/openuninstaller.git
cd openuninstaller

# Build in Release mode
dotnet build OpenUninstaller.sln -c Release

# Run the Desktop Application (WPF GUI)
dotnet run --project src/OpenUninstaller.App

# Or run the Command Line Interface (CLI)
dotnet run --project src/OpenUninstaller.Cli -- help
```

---

## Installation

### 1. Windows Installer (Recommended)
Download the latest `OpenUninstaller-<version>-setup.exe` from the [Releases](https://github.com/eco-null/openuninstaller/releases) page. Built with Inno Setup, it supports:
- Standard per-user installation (no admin elevation required for regular use)
- Machine-wide installation (`/ALLUSERS=1`)
- Unattended silent installation (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`)

### 2. Portable Standalone Archive
Download `OpenUninstaller-v<version>-win-x64.zip`. Extract anywhere and run `OpenUninstaller.App.exe` or `oucli.exe`. No runtime installation or prerequisites required.

### 3. System Requirements
- **OS:** Windows 10 (version 1809+) or Windows 11 (x64)
- **Runtime:** .NET 8.0 or .NET 9.0 Desktop Runtime (included in self-contained builds)
- **Privileges:** Standard user privileges for user-scoped apps; Administrator elevation required for machine-wide (`HKLM`) leftover removal and the optional maintenance service.

---

## Command Line Interface (`oucli`)

OpenUninstaller includes `oucli.exe`, a complete command-line interface supporting scripting and automation with `--json` output.

```powershell
# Diagnostics and environment check
oucli doctor

# List installed software
oucli list
oucli list --json --filter "Visual Studio"

# Scan an application for leftover remnants (dry run preview)
oucli scan-leftovers "Example Application"

# Full uninstall: run uninstaller, scan leftovers, back up, and purge approved items
oucli uninstall "Example Application" --silent --yes

# Traced Installation
oucli trace-install --installer ".\setup.exe" --name "NewApp"
oucli traces list --json
oucli traces export <trace-id> .\newapp.outrace.zip
oucli traces import .\newapp.outrace.zip

# Windows Store & MSIX Package Management
oucli store list --json
oucli store uninstall <package-full-name> --yes

# Startup & Autorun Management
oucli autoruns list --json
oucli autoruns disable <entry-id> --yes
oucli autoruns rollback <entry-id> --yes

# Wipe Unallocated Free Space
oucli wipe-free-space --drive C --yes

# Built-in Windows Administrative Tools
oucli windows-tools list
oucli windows-tools launch services

# Junk Cleaner
oucli junk scan --json
oucli junk clean --category temp --yes

# Privacy History Cleaner
oucli privacy scan --json
oucli privacy clean run-history recent-items --yes

# Secure File Shredder
oucli shred .\confidential-file.dat --passes 3 --yes

# Backups & Rollback
oucli backups list --json
oucli backups restore <backup-id> --yes

# Maintenance Service Management
oucli service status
oucli service install --yes
```

> **Note:** Destructive commands strictly require the `--yes` confirmation flag to prevent unintentional changes.

---

## Architecture

OpenUninstaller follows a decoupled Clean Architecture pattern with zero third-party dependencies in its core assemblies:

```
┌────────────────────────────────────────────────────────┐
│                      Presentation                      │
│   OpenUninstaller.App (WPF)  │  OpenUninstaller.Cli    │
└───────────────────────────┬──┴─────────────────────────┘
                            │
┌───────────────────────────▼────────────────────────────┐
│                  OpenUninstaller.Core                  │
│  • Services: Scanning, Uninstallation, Tracing, Junk   │
│  • Safety: PathGuard, RegistryGuard, System Restores   │
│  • Abstractions: IRegistry, IFileSystem, IProcessRunner │
│  • Storage: Backups, Traces, Autorun History, Logs     │
└───────────────────────────┬────────────────────────────┘
                            │
┌───────────────────────────▼────────────────────────────┐
│               Host Operating System                    │
│   Windows Registry  │  NTFS Filesystem  │  Win32 APIs  │
└────────────────────────────────────────────────────────┘
```

- **OpenUninstaller.Core**: Domain models, heuristic scanning engines, installation trace recorder, safety guards, backup/rollback pipelines, and shredding logic.
- **OpenUninstaller.App**: Desktop WPF interface supporting system dark/light theme switching, high DPI display scaling, and asynchronous background operations.
- **OpenUninstaller.Cli (`oucli`)**: Terminal CLI tool tailored for system administrators, automated deployment pipelines, and power users.
- **OpenUninstaller.Service**: Optional standalone Windows Service host executing local background maintenance, log rotation, and backup retention.

---

## Safety-First Architecture

Unlike aggressive cleaning tools that can corrupt system files or destabilize the operating system, OpenUninstaller operates under non-negotiable safety rules:

1. **Strict Guard Validation (`PathGuard` & `RegistryGuard`):**
   - Protected system folders (`Windows`, `System32`, `WinSxS`, `assembly`, drive roots, user root folders) are permanently blocked from deletion.
   - Core operating system registry hives and critical keys (`SAM`, `SECURITY`, `SYSTEM\CurrentControlSet\Control`, `Explorer\Shell Folders`, etc.) can never be altered or deleted.
2. **Mandatory Pre-Deletion Backups:**
   - Every file, folder, and registry key scheduled for removal is backed up into a compressed `.zip` archive alongside a SHA-256 manifest and `.reg` scripts prior to deletion.
   - If a backup operation fails, deletion is immediately aborted.
3. **Conservative Confidence Scoring:**
   - Heuristics categorize candidates into `Exact`, `High`, `Medium`, or `Low` confidence. Only `High` and `Exact` items are pre-selected by default.
4. **100% Offline Guarantee:**
   - The codebase contains no network sockets, web clients, telemetry SDKs, or cloud endpoints.

---

## Automated Testing

OpenUninstaller includes an extensive automated test suite with 100% isolated virtualized test fixtures:

```powershell
# Run unit and integration tests
dotnet test OpenUninstaller.sln -c Release
```

Tests run against private temporary directories and sandboxed registry subtrees (`HKCU\Software\OUTests\<guid>`), guaranteeing zero side effects on the host machine.

---

## Documentation

- [Capabilities & Safety Matrix](docs/CAPABILITIES.md)
- [Safety Model](docs/SAFETY.md)
- [Leftover and Junk Algorithms](docs/ALGORITHMS.md)
- [Architecture & Subsystems](docs/PLAN/01-architecture.md)
- [User Guide](docs/USER-GUIDE.md)
- [Release & Publishing Guide](docs/GITHUB.md)
- [Contributing Guidelines](CONTRIBUTING.md)
- [Contributor Agreement & PR Rules](CONTRIBUTOR.md)
- [Security Policy](SECURITY.md)
- [Changelog](CHANGELOG.md)

---

## Contributing

We welcome community contributions! Please review [CONTRIBUTOR.md](CONTRIBUTOR.md) and [CONTRIBUTING.md](CONTRIBUTING.md) before submitting pull requests.

---

## License

This project is licensed under the [MIT License](LICENSE).
