# OpenUninstaller Installation Guide

This document describes installation options, silent deployment parameters, service isolation architecture, and release integrity verification for OpenUninstaller.

## Overview

OpenUninstaller installers are built with [Inno Setup 6](https://jrsoftware.org/isinfo.php). The package bundles:
- **OpenUninstaller Desktop (`OpenUninstaller.exe`)**: WPF user interface (.NET 8 self-contained x64).
- **OpenUninstaller CLI (`cli\oucli.exe`)**: Command-line interface for headless scanning, uninstallation, backups, and maintenance.
- **OpenUninstaller Service (`service\OpenUninstaller.Service.exe`)**: Optional Windows Service for scheduled background maintenance.
- Complete offline documentation (`docs\`).

OpenUninstaller requires zero third-party dependencies or external runtimes. Everything needed to run is included in the self-contained package.

---

## Installation Modes

### Per-User Installation (Default)
By default, the installer runs with standard user privileges (`PrivilegesRequired=lowest`):
- **Install Directory**: `%LOCALAPPDATA%\Programs\OpenUninstaller`
- **Shortcuts**: Start Menu shortcuts in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\OpenUninstaller`
- **Elevation**: Does not trigger UAC elevation prompt during setup.

### All-Users Installation (Administrative)
To install machine-wide for all users, launch the installer as Administrator or pass the `/ALLUSERS` switch:
- **Install Directory**: `%ProgramFiles%\OpenUninstaller`
- **Shortcuts**: Start Menu shortcuts for all users.

---

## Command-Line & Silent Parameters

The installer supports standard Inno Setup command-line switches:

| Parameter | Description |
| --- | --- |
| `/SILENT` | Displays the setup wizard progress bar without interactive prompts. |
| `/VERYSILENT` | Installs in the background with no wizard or progress window displayed. |
| `/SUPPRESSMSGBOXES` | Suppresses confirmation message boxes during silent installations. |
| `/NORESTART` | Prevents any system reboot requests upon setup completion. |
| `/DIR="C:\Path\To\Install"` | Overrides the default target installation directory. |
| `/TASKS="desktopicon"` | Creates a desktop shortcut during installation. |
| `/TASKS="!desktopicon"` | Skips creating a desktop shortcut. |
| `/ALLUSERS` | Forces machine-wide installation (requires elevation). |
| `/CURRENTUSER` | Forces per-user installation without requiring administrative privileges. |
| `/LOG="C:\Path\install.log"` | Writes a detailed installation log file for debugging. |

### Example: Silent Enterprise Deployment
```cmd
OpenUninstaller-0.6.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="desktopicon"
```

---

## Maintenance Service Isolation

The optional background Windows Service (`OpenUninstaller.Service`) is **deliberately NOT registered or started by the installer**:

1. **Principle of Least Privilege**: OpenUninstaller never runs background daemons or services without explicit administrative consent.
2. **User Agency**: The application functions completely standalone without the background service.
3. **Transparent Opt-In**: If an administrator wants scheduled daily background maintenance (retention pruning, cache trimming, and optional metrics), the service must be explicitly installed using:

```cmd
oucli service install --yes
oucli service start
```

To verify, stop, or uninstall the service at any time:
```cmd
oucli service status
oucli service stop
oucli service uninstall --yes
```

---

## Upgrade and Uninstallation

### Upgrades
Running a newer installer over an existing installation replaces the binary payload safely:
- User data, traces, backups, logs, and custom settings stored in `%LOCALAPPDATA%\OpenUninstaller` are **never overwritten or deleted** during upgrades.

### Uninstallation
Uninstalling OpenUninstaller removes all registered application binaries, start menu shortcuts, and uninstall keys:
- Use **Windows Settings > Installed apps** or run `unins000.exe` in the application directory.
- For silent uninstallation:
  ```cmd
  "%LOCALAPPDATA%\Programs\OpenUninstaller\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
  ```
- **Service note**: If the optional Windows Service was installed, stop and uninstall it with `oucli service uninstall --yes` prior to removing the application binaries.
- Backups and traces in `%LOCALAPPDATA%\OpenUninstaller` are preserved so accidental uninstallation does not destroy recoverable registry/file backups.

---

## Integrity & Code Signing

Official release artifacts provide SHA256 checksums alongside every release build:
- Verify checksums before execution:
  ```powershell
  Get-FileHash .\OpenUninstaller-0.6.0-setup.exe -Algorithm SHA256
  ```
- Compare the resulting hash against `OpenUninstaller-0.6.0-setup.exe.sha256`.

Release workflow builds are unsigned. Maintainers can sign builds separately using the signing helper and a suitable Authenticode certificate.
