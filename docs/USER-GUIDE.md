# User guide

## Install and start

Requirements: Windows 10 1809+ or Windows 11, x64, and the .NET 8 Desktop Runtime (or use the
self-contained build produced by `scripts/publish.ps1`).

```powershell
dotnet build OpenUninstaller.sln -c Release
dotnet run --project src/OpenUninstaller.App
```

The app starts as a standard user. Machine-wide (`HKLM`) cleanup needs administrator rights;
Settings offers a restart-as-administrator button. No service is installed and no data leaves the
machine; the application is 100% offline with zero telemetry and zero external network calls.

## Programs

The Programs page lists both 64-bit and 32-bit uninstall entries for the current user and the
machine. Use the search box to filter by name or publisher, and the checkbox to show system
components and updates (hidden by default).

Selecting a row shows its version, size, architecture, install location and the exact uninstall
command. **Export** writes the current view to JSON or CSV. **Refresh** re-reads the registry.

Tick **Queue** on several rows and choose **Uninstall queued (N)** to review them one after another.
Each application still opens its own confirmation and leftover review; the queue never deletes
anything in the background. **Clear** removes the queue.

## Uninstalling

1. Select a program and choose **Uninstall**.
2. Review the command. If a quiet command exists, enable **Prefer silent uninstall** (useful for
   MSI and NSIS installers; some vendor uninstallers ignore it).
3. Confirm. The vendor uninstaller runs with shell execute, so its own UAC prompt and UI behave as
   the vendor intended.
4. Wait for it to finish. Timeouts and non-zero exit codes are reported, not hidden.
5. OpenUninstaller checks whether the registration entry is still present.
   - Removed: it scans for leftovers.
   - Still registered: it reports that and offers **forced uninstall** of the registration entry.
     Forced mode does not pretend the application was uninstalled; it removes the registration so
     the app can be reinstalled or cleaned manually.
6. Review the leftover list. Each row shows the path, confidence and reason. Protected rows cannot
   be selected.
7. Select items, confirm, and OpenUninstaller backs them up before deleting them. The result lists
   deleted, skipped and failed items.

Use **Install with tracing** before installing an application to capture exact evidence for a later
uninstall. Tracing snapshots the registry and common file roots, runs the installer, snapshots
again, and stores the difference under `%ProgramData%\OpenUninstaller\Traces`. The next uninstall
uses that trace automatically.

A trace can be moved to another machine as a portable `.outrace.zip` bundle. Known Windows roots
are stored as tokens (`%ProgramFiles%`, `%ProgramData%`, `%LOCALAPPDATA%`, and so on) and expanded
on import. The bundle contains a SHA-256 hash of the trace document; a damaged or edited bundle is
rejected. Use `oucli traces export <id> <file>` and `oucli traces import <file>`. Imported traces get
a new local id and are matched by program name or install location.

## Leftover review

| Badge | Meaning |
| --- | --- |
| Exact (traced) | Recorded by an install trace and still present (key, individual value or file) |
| High | Install location or exact normalized product folder |
| Strong | Two name tokens, or one token plus publisher |
| Medium | One distinctive name token |
| Protected | A guard or another installed application prevents removal |

Only Exact and High are preselected. Sort by confidence in your head, not by size: a large folder
is not necessarily wrong. If a row is protected, read the reason; the app will not remove another
application's files.

The review header compares the current scan with the previous one for the same application:
new, resolved and unchanged candidates. **Ignore selected** hides the chosen items from future
scans of that application without deleting them; **Reset ignored** clears the list and rescans.

Registry values are shown as their own rows (`HKLM\...\Key :: ValueName`). A traced value is backed
up with its containing key but only that value is deleted. App Paths/Classes registrations are
shown only when their executable is missing or inside the install location. Services are shown only
when their binary is missing, and scheduled tasks when their command is missing or inside the
install location. None of these are removed unless you select them.

Leftovers can also be scanned without uninstalling from the command line:

```powershell
oucli scan-leftovers "Example App" --json
oucli scan-leftovers "Example App" --delete --yes   # backup + delete selected high-confidence items
```

## Backups

The Backups page lists every leftover deletion newest first. Select a backup to see its manifest
counts. **Preview…** shows every target and whether it would be restored, overwritten, skipped or
blocked; it changes nothing. **Restore** then performs the operation. Existing files are skipped
unless **Overwrite existing files** is checked. **Open folder** reveals the manifest, registry
exports and archive. **Prune old** keeps the newest number configured in Settings (default 20).

## Autoruns

The Autoruns page lists Run/RunOnce values for the user and machine, both Startup folders,
automatic/manual Windows services and scheduled tasks. Use the filter box to search by name,
command, source, location or publisher. Boot and system drivers are excluded on purpose.

**Disable** is reversible and never deletes an entry:

- A Run/RunOnce value and its kind are copied to OpenUninstaller's own store, then the original
  value is removed.
- A Startup file is moved to `%LOCALAPPDATA%\OpenUninstaller\DisabledStartup`.
- A service keeps its original `Start` mode in the store; only the `Start` value becomes `4`
  (Disabled). The service is not stopped and its executable is not touched.
- A scheduled task is disabled through `schtasks /Change /DISABLE`; its definition stays in Task
  Scheduler.

**Enable** restores the stored value, file, start mode or task state. It refuses to overwrite an
entry that was recreated in the meantime, and a service enable fails if its start mode changed while
it was disabled. Machine-wide services and tasks need administrator rights; the app reports the
access error rather than silently prompting.

A service or task that was disabled outside OpenUninstaller has no stored original state. It is
shown as disabled, but enabling a service cannot guess its previous start mode. The leftover review
can still offer an orphaned service or task for backed-up removal when it has executable evidence.

## Services and tasks in leftovers

Leftover review is separate from the Autoruns page. It offers a service only when its image is
missing and a task only when its command is missing or inside the install location. Removal is
backed up (service registry export or task XML) and always requires your selection.

## Junk files

Choose categories and optionally enable **Dry run**, then **Clean selected**. Categories include
user and Windows temp, every browser profile's cache, thumbnails, crash dumps/WER, shader caches,
WinINet cache, Delivery Optimization, developer package caches and the recycle bin. Files modified
in the last ten minutes are skipped, and locked files are reported. **Windows Update download
cache**, **Windows.old**, **servicing logs** and **prefetch** are report-only. The recycle bin asks
for confirmation. Report-only rows still show their size so you can decide whether to use the
Windows tools instead.

Junk deletion is permanent and is not backed up. Leftover deletion is the operation with backups.

## Privacy & shredder

This page has two independent tools.

**Shred files permanently.** Add files or a folder, choose 1, 3 or 7 overwrite passes, and optionally
enable **Dry run** to see what would happen. A real run asks you to type `SHRED`. Each file is
overwritten with random bytes for every pass, flushed to disk, truncated, renamed and deleted; the
recycle bin is bypassed. Read-only files are handled, reparse points (junctions/symlinks) are
skipped, and OpenUninstaller's own data, Windows folders and profile roots are refused. Folder
targets are walked without following links and empty directories are removed afterwards.

Shredding is best effort. SSD wear-levelling, filesystem journals, volume snapshots and existing
backups can still retain copies. Use full-disk encryption and a certified sanitisation tool when a
formal guarantee is required.

**Windows history.** **Scan again** lists a fixed set of traces:

| Category | What it removes |
| --- | --- |
| Run dialog history | Values under `HKCU\...\Explorer\RunMRU` |
| Explorer address bar history | Values under `HKCU\...\Explorer\TypedPaths` |
| Recent documents | Values and subkeys under `HKCU\...\Explorer\RecentDocs` |
| Application launch history | Subkeys under `HKCU\...\Explorer\UserAssist` |
| Recent items and jump lists | Files under `%APPDATA%\Microsoft\Windows\Recent` |
| Thumbnail and icon cache | `thumbcache_*.db` and `iconcache_*.db` in the Explorer cache folder |
| OpenUninstaller scan history | Scan snapshots, ignored leftover ids and operation logs |

Select categories and choose **Clean selected history**; a real run asks you to type `CLEAN`. These
categories are intentionally **not backed up**, because a backup would copy the very history you are
removing. Locked thumbnail files are reported and skipped; Explorer is never restarted. Browser
history, cookies, passwords and account data are out of scope.

## Store apps

The Store apps page provides a read-only inventory of Microsoft Store and MSIX packages registered
for the current user. It enumerates package names, display labels, publisher names, architecture,
version, install locations, and manifest paths.

Because package manifests, deployment states, and Appx databases are managed by the Windows OS
Package Manager, OpenUninstaller treats Store apps as strictly **read-only**: packages and their
app data are never modified or removed.

## Windows tools

Shortcuts to Programs and Features, Disk Cleanup, Task Manager, Startup Apps, Services, System
Restore and Registry Editor. These launch the Windows components themselves; OpenUninstaller does
not modify them. Registry Editor is behind an extra confirmation.

## Hunter mode

Start Hunter mode and point at a window. The overlay shows the process, executable and best matching
installed application. Click to open that application's uninstall flow. Hunter mode never terminates
a process or injects code.

## Settings

- **Theme**: Dark or Light, applied immediately.
- **Prefer quiet uninstall**: default for new uninstall commands.
- **Backups to keep**: retention count for pruning.
- **Permissions**: elevation state and restart action.
- **Local data**: open backups, traces and logs.
- **Trace bundles**: export every local trace to a folder as portable `.outrace.zip` files, or import
  a bundle from another machine. Import validates the SHA-256 manifest and path tokens.
- **Privacy & offline guarantee**: OpenUninstaller is 100% offline, privacy-first, with zero
  telemetry and no network connections.
- **Background maintenance service**: shows whether the optional service is installed and running.
  Installing or removing it needs administrator rights. **Run maintenance now** performs one pass
  without installing anything.
- **Diagnostics**: run an environment check and read the report.

Settings are stored in `%ProgramData%\OpenUninstaller\ui-settings.json` and the service schedule in
`maintenance.json` in the same directory.

## Maintenance service

The service is optional and is not installed by the portable zip. Run OpenUninstaller as
administrator, open **Settings → Background maintenance service**, and choose **Install service**.
It runs one pass at startup and then every interval from `%ProgramData%\OpenUninstaller\maintenance.json`:

- prune backups to the configured count;
- delete operation logs and scan snapshots older than the configured retention;
- optionally clean allow-listed junk categories (default: off);
- append a summary to `Logs\service.log` and the operation JSONL log.

It runs as LocalSystem, which is why machine-wide junk locations work without a second prompt. Junk
cleaning still uses the fixed allow-list and guards. **Remove service** stops and deletes the service
definition; backups, logs and settings are kept. The CLI equivalents are `service install --yes`,
`service remove --yes`, `service start`, `service stop` and `service status`, from an elevated shell.

## Command line

The CLI (`oucli.exe`) mirrors the app:

```text
oucli list [filter] [--json] [--csv] [--all]
oucli uninstall <name|id> [--silent] [--yes] [--dry-run] [--json]
oucli force-uninstall <name|id> --yes
oucli scan-leftovers <name|id> [--json] [--diff] [--ignore] [--delete --yes] [--dry-run]
oucli trace-install --installer <path> [--name <name>] [--publisher <name>]
oucli traces list|delete <id> [--json] [--yes]
oucli traces export <id> <file> | import <file> [--json]
oucli backups list [--json]
oucli backups preview <id> [--json] [--overwrite]
oucli backups restore <id> [--overwrite] --yes
oucli backups prune --keep <n> --yes
oucli autoruns list [--json]
oucli autoruns disable|enable <id> --yes
oucli autoruns history [--json]
oucli autoruns rollback <id|all> [--yes]
oucli junk scan [--json]
oucli junk clean <category...> [--dry-run] [--yes] [--json]
oucli privacy scan [--json]
oucli privacy clean <category...> [--dry-run] [--yes] [--json]
oucli privacy history [--clear --yes]
oucli shred <path...> [--passes N] [--dry-run] [--yes] [--json]
oucli maintenance run [--json]
oucli service status | install|remove --yes | start|stop
oucli hunter [--json]
oucli doctor [--json]
oucli windows-tools list
```

Run `oucli help` for the current exit codes and exact flags. Destructive commands require `--yes`;
`--json` output is intended for scripting. Logs are JSONL under
`%ProgramData%\OpenUninstaller\Logs`.

## Troubleshooting

**The uninstaller does nothing or returns an error.** The vendor command may require elevation or
may have been uninstalled itself. Check the command preview, try running it manually, then use
forced uninstall to remove the registration and cleanup.

**A leftover is protected.** Read the reason. Common causes are a Windows-owned path, a reparse
point, or another installed application sharing the folder. Do not try to bypass the guard.

**A file cannot be deleted.** It is in use. Close the application, refresh, and retry. The cleaner
never kills processes or forces locks.

**The junk scan is slow.** Browser and shader caches can contain hundreds of thousands of files.
Categories appear as they finish; the first full scan on a large machine can take several seconds.

**Restore skipped a file.** Enable "Overwrite existing files" or close the application that holds
the file.

## Uninstall OpenUninstaller

Remove the maintenance service first if you installed it: run OpenUninstaller as administrator and
choose **Remove service** in Settings, or run `oucli service remove --yes` from an elevated shell.
Then close the app and delete its folder. Optionally delete `%ProgramData%\OpenUninstaller` (traces,
backups, settings, logs). It does not install a driver or scheduled task.
