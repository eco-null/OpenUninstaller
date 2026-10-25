# Plan 05 — Supporting tools

Status: **implemented in v0.1; junk categories expanded in v0.2; services and tasks added in v0.3; restore preview added in v0.4; privacy cleaner, shredder, opt-in telemetry and the optional maintenance service added in v0.5**

## Autorun manager

Sources:

| Kind | Location |
| --- | --- |
| Registry Run | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| Registry RunOnce | same, `RunOnce` |
| Registry Run (machine) | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` (+ 32-bit view) |
| Registry RunOnce (machine) | same, `RunOnce` (+ 32-bit view) |
| Startup folder (user) | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup` |
| Startup folder (all users) | `%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup` |
| Windows service | `HKLM\SYSTEM\CurrentControlSet\Services\*` (`Start` 2/3/4; 0/1 drivers excluded) |
| Scheduled task | `%WINDIR%\System32\Tasks\**` (XML `Exec` command and `Enabled`) |

`AutorunEntry`: id, name, command, source, location (hive/view/path or folder), enabled, publisher
hint (resolved exe company if available).

**Disable** is reversible and does not delete data:

- Registry: value data and kind are copied to
  `HKCU\Software\OpenUninstaller\DisabledAutoruns\<id>` (JSON payload), then the original value is
  deleted. A marker `DisabledAt` and `OriginalLocation` are stored.
- Startup file: moved to `%LOCALAPPDATA%\OpenUninstaller\DisabledStartup\<id>_<filename>`.
- Service: the original `Start` value is stored in the payload and the live value is set to `4`
  (Disabled). Boot and system drivers (`Start` 0/1) are not listed. The Service Control Manager is
  never asked to stop the service.
- Scheduled task: the task name is validated by `ScheduledTaskGuard`, the payload records the task
  name and XML path, and `schtasks /Change /DISABLE` disables it. The XML definition is untouched.

**Enable** reverses the operation and removes the disabled record. If the original value or startup
file was recreated in the meantime, enabling fails with a conflict instead of overwriting. For a
service, enabling restores the stored start mode and refuses if the current mode is neither
Disabled nor the stored original. For a task, `schtasks /Change /ENABLE` is used.

A service or task disabled outside OpenUninstaller has no payload, so no original state exists.
Such entries are still visible (together with their live state), but enabling them reports the
missing backup rather than guessing. Leftover removal of orphaned services and tasks remains a
separate, evidence-driven operation; see [03-leftover-engine.md](03-leftover-engine.md) and
[ALGORITHMS.md](../ALGORITHMS.md).

## Junk cleaner

Categories and roots (all allow-listed):

| Category | Roots | Notes |
| --- | --- | --- |
| User temporary files | `%TEMP%`, `%LOCALAPPDATA%\Temp` | skip files younger than 10 minutes, skip in use |
| Windows temporary files | `%WINDIR%\Temp` | requires admin |
| Browser caches | every existing Chrome, Edge, Brave, Vivaldi, Chromium, Opera and Firefox profile | warns when a browser process is running; locked files are reported |
| Thumbnail & icon cache | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db`, `iconcache_*.db` | recreated automatically |
| Crash dumps and WER | `%LOCALAPPDATA%\CrashDumps`, user/machine Windows Error Reporting queues | locked files reported |
| System crash dumps | `%WINDIR%\Minidump`, `%WINDIR%\LiveKernelReports` | requires admin |
| Graphics shader caches | D3DSCache, NVIDIA DX/GL/Compute, AMD Dx/Dxc/GL, Intel ShaderCache | rebuilt on demand |
| Windows internet cache | `%LOCALAPPDATA%\Microsoft\Windows\INetCache` | WinINet/WinHTTP |
| Delivery Optimization cache | `%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache` | requires admin; may be locked |
| Developer package caches | npm, pip, NuGet v3/plugins and Yarn caches | rebuilt on demand; project sources untouched |
| Windows servicing logs | `%WINDIR%\Logs\CBS`, `DISM`, `WindowsUpdate` | report only; needed for diagnostics |
| Prefetch | `%WINDIR%\Prefetch` | report only; removing it slows startup |
| Windows Update download cache | `%WINDIR%\SoftwareDistribution\Download` | report only |
| Previous Windows installation | `%SystemDrive%\Windows.old` | report only; use Storage settings |
| Recycle bin | Shell API | emptied only with explicit confirmation |

Scan reports category, item count, bytes and warnings. Each category is reported to the UI as soon
as it is measured. Cleaning:

- deletes files/small directories, never the category root itself;
- skips files modified in the last 10 minutes;
- reports locked files as skipped;
- supports `--dry-run`;
- never touches restore points, Windows.old, or other users' profiles;
- does not back up caches (leftover deletion is the backed-up operation).

## Hunter mode

1. Overlay window follows the cursor, highlights the window under it, shows process/exe.
2. Click → resolve `hwnd` → `GetWindowThreadProcessId` → process → executable path.
3. Match to an installed program:
   - executable inside `InstallLocation` (strongest);
   - `DisplayIcon` same executable;
   - normalized process name matches name tokens;
   - publisher matches company name (weak).
4. Show a card with the matched program and a shortcut to its uninstall flow; offer the executable
   path and PID for transparency. Never terminates the process.

Implemented with P/Invoke (`GetCursorPos`, `WindowFromPoint`, `GetWindowThreadProcessId`,
`GetWindowTextW`, `IsWindowVisible`), no third-party hooks. The overlay is `Topmost`,
`AllowsTransparency`, and closes with Esc.

## Backups browser

Lists backups from the manifest store, newest first: name, created date, registry entries, file
count, total size, complete flag. Actions: open folder, view manifest, restore selected, delete
backup (guarded to the backup root).

## Windows tools panel

Shortcuts to built-in maintenance applets: Programs and Features, Disk Cleanup, Startup Apps
(Task Manager), Services, System Restore, Windows Update, Registry Editor (hidden behind a warning,
read-only intent). These are links, not reimplementations.

## CLI surface for tools

```
autoruns list [--json]          # includes services and scheduled tasks
autoruns disable <id> --yes
autoruns enable <id> --yes
junk scan [--json]
junk clean <category...> --yes [--dry-run]
backups list [--json]
backups restore <id> [--overwrite] --yes
backups prune --keep 20 --yes
windows-tools list
```
