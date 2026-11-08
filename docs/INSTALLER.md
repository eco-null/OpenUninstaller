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
To install machine-wide for all users, choose **Install for all users** in the first setup dialog or pass the `/ALLUSERS` switch:
- **Install Directory**: `%ProgramFiles%\OpenUninstaller`
- **Shortcuts**: Start Menu shortcuts for all users.

### Setup Options
- **Language**: the wizard follows the Windows display language (English or Turkish); `/LANG=english` or `/LANG=turkish` forces one.
- **Desktop shortcut** (`desktopicon`, selected by default).
- **Add oucli to PATH** (`addtopath`, off by default): adds `<install folder>\cli` to the user PATH (per-user install) or the system PATH (all-users install). Uninstalling, or clearing the task during an update, removes exactly that entry.
- **Requirements**: Windows 10 version 1809 or later, or Windows 11, on x64. Only one copy of setup can run at a time.

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
| `/TASKS="desktopicon,addtopath"` | Also adds the `oucli` folder to PATH. |
| `/LANG=turkish` | Runs the wizard in Turkish (`english` is the other option). |
| `/CLOSEAPPLICATIONS` | Closes a running OpenUninstaller without asking when it blocks an update. |
| `/ALLUSERS` | Forces machine-wide installation (requires elevation). |
| `/CURRENTUSER` | Forces per-user installation without requiring administrative privileges. |
| `/LOG="C:\Path\install.log"` | Writes a detailed installation log file for debugging. |

### Example: Silent Enterprise Deployment
```cmd
OpenUninstaller-<version>-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="desktopicon"
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

The service runs as LocalSystem, so it never runs from the per-user application folder
(`%LOCALAPPDATA%\Programs\OpenUninstaller` is writable without elevation). `service install`
copies the service files to `%ProgramFiles%\OpenUninstaller\Maintenance`, which only
administrators can modify, and registers that copy. `service remove` deletes the copy again.
The service writes its own logs to `Maintenance\Logs` in that folder, and only deletes inside
`%ProgramData%\OpenUninstaller` through verified handles, so a junction placed there cannot
redirect its work. If `service status` reports an update (another version, or a folder outside
Program Files because an earlier version installed it), run `service update --yes`: it stops the
service, swaps in this version's files under Program Files (the old copy stays in place if copying
fails), keeps `Maintenance\Logs`, points the registration at the new copy and starts it again.

To verify, update, stop, or remove the service at any time:
```cmd
oucli service status
oucli service update --yes
oucli service stop
oucli service remove --yes
```

---

## Upgrade and Uninstallation

### Upgrades
Run the newer `OpenUninstaller-<version>-setup.exe`; no uninstall is needed. Every release uses the same setup identity, so setup recognises the installed version and updates it in place:
- It keeps the existing install folder, per-user or all-users mode, wizard language and task choices, skips the licence page, and shows "update X to Y" on the Ready page.
- A running OpenUninstaller is closed through the Windows Restart Manager before files are replaced (silent installs: add `/CLOSEAPPLICATIONS`).
- The `docs` and `cli` folders are replaced as a whole, so files dropped from a release do not linger.
- Running the same version again repairs the installation. Setup refuses to install an older version over a newer one (exit code 1 in silent mode); uninstall first to downgrade.
- Settings, backups, traces and logs in `%ProgramData%\OpenUninstaller`, and disabled startup items in `%LOCALAPPDATA%\OpenUninstaller`, are **never overwritten or deleted** by setup.
- If the optional maintenance service is installed, setup updates it too: after copying the files it runs `oucli service update --yes` with one administrator (UAC) prompt, and the Ready page says so beforehand. A service registered before 1.0.10 runs from the install folder; setup stops it first (same prompt type) so its files can be replaced, then moves it under Program Files. If the prompt is declined, setup finishes and explains how to update the service later. Silent per-user installs never show a UAC prompt: they skip this step (run `oucli service update --yes` elevated afterwards), and a pre-1.0.10 service that is still running makes them fail instead. Silent installs started as administrator update the service without a prompt.

The same command line updates silently:
```cmd
OpenUninstaller-<version>-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
```

### Uninstallation
Uninstalling OpenUninstaller removes all registered application binaries, start menu shortcuts, and uninstall keys:
- Use **Windows Settings > Installed apps** or run `unins000.exe` in the application directory.
- For silent uninstallation:
  ```cmd
  "%LOCALAPPDATA%\Programs\OpenUninstaller\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
  ```
- **Service note**: If the optional Windows Service was installed, stop and remove it with `oucli service remove --yes` prior to removing the application binaries. The uninstaller warns and lets you cancel while the service is still registered.
- The PATH entry added by the `addtopath` task is removed.
- Settings, backups and traces in `%ProgramData%\OpenUninstaller`, and disabled startup items in `%LOCALAPPDATA%\OpenUninstaller`, are preserved so accidental uninstallation does not destroy recoverable registry/file backups. Delete those folders manually to remove all data.

---

## Integrity & Code Signing

Official release artifacts provide SHA256 checksums alongside every release build:
- Verify checksums before execution:
  ```powershell
  Get-FileHash .\OpenUninstaller-<version>-setup.exe -Algorithm SHA256
  ```
- Compare the resulting hash against `OpenUninstaller-<version>-setup.exe.sha256`.

Release workflow builds are unsigned. Maintainers can sign builds separately using the signing helper and a suitable Authenticode certificate.
