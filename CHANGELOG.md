# Changelog

All notable changes to this project are documented here. The format follows Keep a Changelog and
the project uses semantic versioning.

## [Unreleased]

### Added

- The setup updates an existing installation in place: it detects the installed version, keeps
  its folder, install mode and shortcut choices, skips the licence page, shows
  "update X to Y" before installing, closes a running OpenUninstaller and refuses to replace a
  newer version. Files dropped from a release no longer linger in `docs\` and `cli\`.
- The setup can add `cli\oucli.exe` to PATH (optional task; removed again on uninstall), offers an
  English and a Turkish wizard, blocks a second copy of setup, requires Windows 10 1809 or later
  and warns before uninstalling while the maintenance service is still installed.
- The maintenance service can be updated in place: `service update --yes` and **Update service**
  in Settings stop it, swap in the current build's files under Program Files (keeping its logs and
  leaving the old copy if copying fails), and start it again. `service status` and Settings show
  when the installed service runs another version. Setup runs this after an update with one
  administrator prompt, and first stops a service installed before 1.0.10 that runs from the
  install folder.
- Releases are cut from GitHub: **Actions › Release › Run workflow** bumps the patch, minor or
  major version, builds, tests and packages it, then commits, tags and publishes the release.

### Fixed

- All-users installs (`/ALLUSERS`) go to `Program Files` instead of the administrator's
  `%LOCALAPPDATA%\Programs` folder.
- The setup and Apps & features entry link to the project repository, and the setup executable
  carries the product version.

## [1.0.10] — 2026-11-08

### Security

- The LocalSystem maintenance service no longer runs from the per-user (writable) install folder.
  `service install` copies it to `%ProgramFiles%\OpenUninstaller\Maintenance` first; `service
  remove` deletes that copy after the service stops.
- The maintenance service no longer follows junctions or renames inside the user-writable
  `%ProgramData%\OpenUninstaller` tree. Backup pruning and log/snapshot expiry delete through
  handles whose resolved path is verified, backup lock files are created inside pinned folders, the
  service no longer creates folders in that tree, and it writes its own logs to
  `%ProgramFiles%\OpenUninstaller\Maintenance\Logs`.
- Approved leftover files and folders are deleted through the same verified handles, so a junction
  swapped in after review cannot redirect an elevated deletion.
- `service status` and the Settings page warn when an existing service still runs from a folder
  outside Program Files (installed by an earlier version); remove and reinstall it.
- The path guard rejects drive-relative (`C:folder`) and root-relative (`\folder`) paths, and
  compares protected roots such as `%TEMP%` in their long form when Windows reports 8.3 names.

### Fixed

- Registry access denials (for example deleting an HKLM leftover without elevation) are reported
  as failed items instead of aborting the whole deletion or forced uninstall.
- Vendor uninstall arguments are passed verbatim; re-quoting split tokens broke commands such as
  `rundll32 "C:\Program Files\…\x.dll",Uninstall`.
- Uninstall commands with bare system executables (`rundll32.exe dfshim.dll,ShArpMaintain …` for
  ClickOnce, `RunDll32 …`) resolve in the Windows system folders instead of being reported missing.
- Approved folders that contain read-only files are deleted completely.
- Restore results report their real duration.
- The maintenance service no longer races its stop handler against disposal and reports failures
  with a service-specific exit code.
- The installer guide names the real CLI command, `service remove --yes`.
- Test-only dependencies pin patched `System.Net.Http` and `System.Text.RegularExpressions`.

## [1.0.9] — 2026-11-04

### Changed

- Offer PATH entries inside an application's own Program Files folder. The Windows folder, the
  Program Files roots and Common Files remain protected.
- Offer single-level `SOFTWARE\<Product>` keys (for example `HKCU\Software\7-Zip`) when the name
  matches the product, is not a vendor root and no other installed application matches it.
- Leftover scans are about four times faster: ownership evidence for installed applications is
  derived once per scan instead of once per inspected entry (≈116 s → 27 s on a real machine).

### Fixed

- Wait for self-relaunching vendor uninstallers (NSIS-style) while the application is still
  registered, instead of reporting a running uninstall as cancelled. A finished uninstall is not
  held up by a lingering child such as a feedback page; nothing is ever terminated.
- Resolve unquoted uninstall commands with spaces (`C:\Program Files\App\unins000.exe /S`) to the
  longest existing path, which also rules out a planted `C:\Program.exe`.
- List per-user applications once; the shared HKCU 32-bit registry view duplicated every entry.
- Stop offering file associations, App Paths and `Classes\Applications` keys twice from the shared
  32-bit HKLM view.
- Remove surrounding quotes from recorded install locations so ownership and shortcut evidence apply.

### Tests

- Add regression tests for each fix (308 core/CLI tests) and stop the test environment leaking
  empty registry keys under `HKCU\Software\OpenUninstaller\Tests`.

## [1.0.8] — 2026-11-02

### Changed

- Expand safe temporary/cache discovery and add a per-file Junk preview with size, modification
  time, selection, filters, cancellation and explicit incomplete-scan warnings.
- Add report-only folder/drive pattern scans and a separate Include-folders pass, with access-age
  filtering, an option to disable it, wildcard masks and environment-aware exclusions.
- Report metadata for locked and hard-linked files without granting deletion permission.
- Expand leftover discovery with protected deeper application-data suggestions, native alphabetic
  name boundaries and Windows Installer identity references.

### Fixed

- Revalidate junk file identity, timestamps and resolved paths through file handles before cleanup;
  preserve reparse, offline, encrypted, hard-link and in-use protections for deletion.
- Enforce leftover ownership/protection rules before backup or deletion; tighten shortcut, PATH,
  firewall and scheduled-task matching, and make scan truncation visible.
- Deduplicate overlapping roots and warnings, reduce repeated ancestor metadata reads during scans,
  and preserve browser Service Worker offline storage.

### Tests and releases

- Expand core/CLI coverage from 227 to 300 tests; add a 10,001-row WPF Junk fixture across two themes
  and three widths. Controlled CLI fixtures verify exact expected sets and sizes.
- Publish versioned release notes automatically from `docs/releases/<version>.md`, verify tag/build
  version agreement and require desktop/Junk UI smoke checks before packaging.

## [1.0.7] — 2026-10-31

### Changed

- Add synchronized sort pickers and column-header sorting to Programs, Autoruns, autorun history
  and Store apps. Sort program sizes and install dates by their underlying values, keeping unknown
  values last and preserving queued applications while reordering.
- Reflow Settings, Privacy, Junk and Windows tools cards across the full content area.
  The default window uses two columns; narrow and wider windows adapt to the available space.
- Organize Settings and Privacy into two stable semantic columns with a centered content width.
  Consolidate related settings and bound long privacy lists to avoid scattered narrow cards.
- Add Basic and Advanced autorun views alongside History & rollback. Basic keeps application
  startup entries, including Microsoft user applications, while hiding Windows system entries,
  kernel drivers and manual-only services. Advanced retains the complete inventory.
- Read packaged applications' declared startup tasks and their current user state; open Windows
  Startup settings to manage them. Respect Windows-disabled Run and Startup-folder entries.
- Enumerate scheduled tasks through Task Scheduler as well as readable task files, preserving
  access to user tasks without requiring direct access to the task store.

## [1.0.6] — 2026-10-31

### Fixed

- Stop Hunter mode from assigning unrelated installed applications through shared name or
  publisher fragments, such as OpenCode being selected for an OpenUninstaller window.
- Resolve ownership using exact executable paths and the most specific install location;
  leave conflicting owners unmatched and recognize identical shared HKCU registry views.
- Normalize environment-variable paths and reject relative or root install locations.

### Documentation

- Replace the previous screenshots with current light/dark captures of all eight screens
  and Hunter mode, using sample data, and expand the README gallery.

### Tests

- Add 26 Hunter regression cases; the core and CLI suite now contains 193 passing tests.

## [1.0.5] — 2026-10-31

### Changed

- Refresh the native desktop interface with coordinated zinc light and dark themes, consistent
  controls and a compact navigation rail.
- Add a resizable application inspector and reclaim list space when no application is selected
  in a compact window.
- Simplify interface copy across all eight screens; remove decorative workspace and footer labels
  while retaining action labels, live results, errors and necessary warnings.
- Update the application and installer icon with an SVG source and nine Windows icon sizes.
- Rewrite the README with current screenshots, corrected installation and CLI paths, and clearer
  capability and recovery boundaries.

### Tests

- Expand desktop UI smoke coverage to eight screens in both themes at standard and compact window
  sizes, including search, selection and checks for removed decorative labels.
- Identify page roots by stable automation IDs instead of explanatory paragraphs.

## [1.0.4] — 2026-10-27

### Changed

- Refresh the desktop interface with coordinated light and dark monochrome themes, compact controls,
  consistent search fields, improved list alignment and clearer status labels.
- Improve scrolling and card layouts, including three Windows tools columns at the default size.
- Update application and setup icons while preserving the original mark.
- Derive the application and CLI version from the build so displayed and package versions agree.

## [1.0.3] — 2026-10-26

### Safety fixes

- Pin and validate restore destinations using Windows handles and transactional NTFS; reject
  junctions, conflicting handles and hard-linked destination files before writing. Each file or
  folder item rolls back on failure. File restore requires supported local NTFS, TxF and permission
  to lock destination directory metadata; encrypted files and unsupported targets are refused.
- Validate registry exports against their declared key, view and value scope before backup and
  restore; value-only restores leave sibling values intact and legacy 32-bit exports remain usable.
- Preserve empty directories in backups and refuse to capture directory trees containing links.
- Skip junk trees with recent, excluded, linked or inaccessible descendants.
- Limit trace bundle manifest size and decompressed entry reads.
- Prevent backup deletion or retention pruning while a restore is active across processes.
- Allow unrelated ancestor directory writers during restore while keeping rename and reparse protections.

## [1.0.1] — 2026-10-26

### Security & Safety Hardening

- **WOW6432Node Registry Hierarchy:** Fixed 32-bit registry key handling under `SOFTWARE\WOW6432Node` so product subkeys can be cleaned while strictly protecting shared vendor root containers (`parts.Length < 4`).
- **AppData\LocalLow Leftover Scanning:** Added full scanning and protection support for `%USERPROFILE%\AppData\LocalLow`, catching leftover files from Unity runtimes, games, and modern apps.
- **Developer Vault Protections:** Hardened `PathGuard` and `JunkService` with explicit safeguards preventing accidental deletion of developer and cloud credential directories (`.kube`, `.docker`, `.gnupg`, `.gcloud`, `.oci`, `.terraform.d`).
- **Drive Root & Profile Safety:** Enforced strict rejection for drive roots (e.g., `C:\`) and user profile parent folders (`C:\Users`).
- **Vendor Suite Isolation:** Expanded `KnownMultiProductVendors` to isolate sub-products within major security (Bitdefender, Kaspersky, ESET, Malwarebytes), audio/creative (Affinity/Serif, Ableton, Steinberg, Corel), and developer suites.
- **Enhanced Test Suite:** Added comprehensive tests for false positive safety, bringing total automated test coverage to 142 passing tests.

## [1.0.0] — 2026-10-25

### Initial Public Release

- **Deep Uninstaller:** Comprehensive registry and filesystem heuristic scanner for orphaned leftovers.
- **Real-Time Installation Tracer:** Schema 2 value-level differential snapshot engine with `.outrace.zip` export/import.
- **Windows Store & MSIX Manager:** Package termination, data reset, manifest inspection, and uninstallation.
- **Evidence Remover:** Multi-pass zero-pattern free space wiper for unallocated disk sectors.
- **23 Windows Tools Dashboard:** Quick launcher for native system diagnostic consoles.
- **Autorun & Service Manager:** Management of Run keys, Startup folders, services, and tasks with audit history and rollback.
- **Junk & Privacy Cleaner:** Safe purging of temporary files, crash dumps, and Windows Explorer history.
- **Cryptographic File Shredder:** Multi-pass unrecoverable file wiping.
- **100% Offline & Private:** Zero telemetry, tracking, or network connections.
- **Fail-Safe Safety Architecture:** Hard-coded `PathGuard` and `RegistryGuard` protection with mandatory pre-deletion backups.

## [0.6.0] — 2026-10-25

### Added

- Read-only Microsoft Store / MSIX package inventory (`MsixInventoryService`) enumerating per-user
  registered Appx and MSIX packages, display metadata, architecture, publisher and install folders.
  Exposed via a dedicated `Store apps` page in the desktop app and `oucli msix list` in the CLI.
- Native COM-based shell link resolver (`ShortcutResolver`) using `IShellLinkW` / `IPersistFile`
  with zero external dependencies. Integrated into `LeftoverScanner` to resolve `.lnk` shortcut targets
  and detect high-confidence leftover shortcuts.
- Inno Setup 6 installer (`installer/OpenUninstaller.iss`) and build script (`scripts/build-installer.ps1`).
  Supports per-user unprivileged setup by default, administrative `/ALLUSERS` mode, silent deployment
  flags, and integrity verification. Documented in `docs/INSTALLER.md`.
- Authenticode signing helper script (`scripts/sign-release.ps1`) for signing executables and installers.
- UI Automation smoke test project (`tests/OpenUninstaller.UiSmoke`) using `System.Windows.Automation` to
  launch, navigate, and verify all 8 pages without third-party drivers or destructive actions.
- Service and task modification audit history with rollback view (`AutorunHistoryStore`, `AutorunsViewModel`, `AutorunsView`).
  Maintains an audit trail of disabled/enabled autoruns and services in `%LocalAppData%\OpenUninstaller\autoruns-history.json`,
  with granular single-item rollback and one-click "Rollback all" in both the desktop UI and CLI (`oucli autoruns history`, `oucli autoruns rollback <id|all>`).

### Removed

- Permanently removed all telemetry services and network-calling code (`TelemetryService`, `TelemetrySettings`, `oucli telemetry`). OpenUninstaller is now strictly 100% offline with zero telemetry and zero external network connections.

### Changed

- Raised project version to 0.6.0 across all assemblies.
- Added `OpenUninstaller.UiSmoke` to `OpenUninstaller.sln` and CI workflows.
- Added installer compilation and SHA256 checksum generation to release workflows.

## [0.5.0] — 2026-10-25

### Added

- Privacy & shredder page and CLI. A fixed, auditable list covers the Windows Run dialog (RunMRU),
  File Explorer address-bar history (TypedPaths), RecentDocs, UserAssist launch counters, the per-user
  Recent folder and jump lists, thumbnail/icon caches, and OpenUninstaller's scan snapshots, ignored
  ids and operation logs. Typed confirmation (`CLEAN`), per-category selection, dry run and a
  read-only scan are included.
- Multi-pass file shredder for files and folders (`shred`). Random overwrite per pass, write-through
  flush, truncate, rename and delete; write-protected files are handled; reparse points are skipped;
  protected roots are refused by `PathGuard`. Dry run and typed `SHRED` confirmation in the app.
- Opt-in telemetry (`telemetry status|preview|send|configure`). Off by default with no built-in
  endpoint. The payload contains only schema/app version, OS build, CPU architecture, a random
  install id, a timestamp and caller-supplied counters. HTTPS is required except for loopback
  development.
- Shared maintenance pass (`maintenance run`): backup retention, log expiry, scan-history expiry,
  optional allow-listed junk cleanup and an optional telemetry heartbeat.
- Optional Windows maintenance service (`OpenUninstaller.Service.exe`). Dependency-free service
  control host via `advapi32`, installed and removed with `sc.exe`; runs one pass at startup and
  then follows `maintenance.json`. `service status|install|remove|start|stop` in the CLI and a
  Settings card in the app.
- Settings now show telemetry consent state and service status with install/remove and run-now
  actions; the status bar reflects the telemetry choice.

### Changed

- Version raised to 0.5.0; the publish script now ships a `service/` folder and the release manifest
  records its executable.
- README, user guide, safety model, capability matrix, task list and changelog describe the new
  destructive operations and the opt-in network/service behavior.
- The CLI help lists privacy, shred, telemetry, maintenance and service commands.

### Safety

- Shredding and privacy cleanup deliberately skip the backup step so the removed data is not copied
  elsewhere. Both are gated by an explicit flag/typed word and clearly labelled irreversible.
- Privacy targets are hard-coded: no program scan can add a registry key, folder or browser history
  store to the list. Reparse points are skipped and only files under the fixed roots are removed.
- Telemetry never includes paths, program names, user/machine names or scan results, and remains
  disabled until consent and a validated endpoint are saved.
- Service installation requires an elevated process, a written `--yes` confirmation in the CLI, and
  reports access errors instead of silently prompting. Its junk cleanup reuses the same guards.

### Verification

- Release build with warnings-as-errors; 99 tests green (10 new for shredding, privacy, telemetry
  id stability, retention and service query).
- CLI smoke: `privacy scan --json`, `privacy clean --dry-run`, `privacy history`, `telemetry
  status`, `telemetry send` (disabled, no request), `service status --json`, and a real 2-pass
  shred of a synthetic temp file (the only file removed during verification).
- The self-contained 0.5.0 package was built and inspected: it contains the desktop app, CLI and
  `service/OpenUninstaller.Service.exe`; the published app launched and rendered the Privacy page.
- No history category, installed application, service or user file was removed during verification.

## [0.4.0] — 2026-10-22

### Added

- Batch uninstall queue on the Programs page. Tick several applications and review each one in its
  existing confirmation and leftover window; nothing is deleted in the background.
- Persistent per-application ignored-leftover list. Ignored items stay hidden from later scans until
  the list is reset from the review window.
- Scan history and diff: new, resolved and unchanged candidates against the previous scan, shown in
  the review header and exposed by `scan-leftovers --diff` (JSON included).
- Portable trace bundles: `traces export` / `traces import`, with machine-independent root tokens
  and a SHA-256 trace hash. Damaged or edited bundles are rejected.
- Read-only restore preview in the Backups page and `backups preview`, listing restore, overwrite,
  skip and blocked targets before anything changes.
- `scan-leftovers --ignore` to persist preselected candidates without deleting them.

### Changed

- CLI help documents the new trace and backup commands and the scan diff/ignore flags.
- Version raised to 0.4.0 across the app, CLI and publish manifest.

### Safety

- Ignoring is state only; it never deletes a file or key and can be reset.
- Imported trace bundles are hash-checked and path-token validated; UNC, device, relative and
  traversal paths are rejected.
- Restore preview never writes; the actual restore revalidates manifest hashes and guards.
- The batch queue reuses the single-application workflow and keeps every confirmation and review.

### Verification

- Release build with warnings-as-errors; 88 tests green.
- Read-only CLI smoke: `doctor`, `list`, `junk scan`, `autoruns list`, `scan-leftovers --diff`.
- Synthetic trace bundle export/import round-trip, including imported id and expanded paths.
- Desktop pages recaptured; no installed application was uninstalled during verification.

## [0.3.0] — 2026-10-21

### Added

- Autoruns now includes Windows services and scheduled tasks alongside Run/RunOnce values and
  Startup folders. Disabling a service stores its original `Start` mode and sets it to Disabled;
  enabling restores that mode after checking it was not changed in the meantime.
- Scheduled tasks can be disabled and enabled through `schtasks` without deleting their definition.
  Task names are validated through the existing `ScheduledTaskGuard` before every command.
- Boot and system drivers (`Start` 0 and 1) are excluded from the autorun list so the tool cannot
  make a machine unbootable from a general-purpose toggle.
- Live search/filter on the Autoruns page by name, command, source, location or publisher.
- GitHub release automation: tag-triggered build/test/package workflow with SHA-256 sidecar, issue
  and pull-request templates, Dependabot configuration, Code of Conduct, support guide and
  `docs/GITHUB.md`.
- `docs/CAPABILITIES.md` documents implemented capabilities and safety boundaries without
  claiming affiliation with any commercial product.

### Changed

- Autorun source labels and confirmation text now distinguish registry values, Startup files,
  services and tasks.
- README, user guide, plan and task list describe the expanded autorun coverage and release flow.

### Fixed

- `oucli version` and `oucli help` read the assembly informational version instead of a
  hard-coded string, so they can no longer report an older release than the app.
- The status bar places the privacy note and version on opposite sides instead of joining them.
- Desktop screenshots in `docs/assets` were recaptured from the final 0.3.0 build.

### Safety

- Disabling a service only changes its `Start` value; the service is never stopped and its image is
  never touched. Enabling refuses to overwrite a start mode that changed while disabled.
- A scheduled task is changed only through `schtasks` with a validated task name; its XML file is
  not edited by hand.
- The disabled store keeps the original command, location and task/service identity so entries can
  be restored or identified after a reboot.

### Verification

- Release build with warnings as errors and the complete xUnit suite.
- Read-only CLI smoke tests (`doctor`, `list --json`, `junk scan`, `autoruns list`) and screenshot
  checks of the desktop pages. No application was uninstalled during verification.

## [0.2.0] — 2026-10-21

### Added

- Trace schema 2: registry keys created by an installer and values added to existing keys are
  separate evidence. Values are reviewed individually, backed up through a key export, deleted with
  `DeleteValue`, and restored by importing the export.
- App Paths and `Classes\Applications` leftover detection with a scoped direct-child guard. A
  registration is offered only when its executable is missing or inside the install location.
- Orphaned service detection under `HKLM\SYSTEM\CurrentControlSet\Services` with a scoped guard.
  Only services whose image is missing are offered; the Service Control Manager is never stopped.
- Scheduled-task detection from `%WINDIR%\System32\Tasks` XML, backed-up task XML, deletion through
  `schtasks /Delete`, and restore through `schtasks /Create /XML`.
- Registry-value rows and task/service rows in the leftover review and CLI JSON output.
- Backup manifest schema 2 records covered value names and scheduled-task metadata; schema 1
  manifests remain readable.
- Junk categories for Delivery Optimization, npm/pip/NuGet/Yarn caches and live-kernel reports.
- Report-only servicing logs and prefetch categories.

### Changed

- Legacy schema 1 traces protect their registry key candidates because the trace cannot distinguish
  a new key from a value added to a shared key. Re-run `trace-install` for value-level evidence.
- Junk category table and user guide now name the report-only categories explicitly.
- Version bumped to 0.2.0.

### Safety

- App Paths, service and registry-value deletion are only possible through narrow, caller-scoped
  guard flags; the containers themselves remain protected.
- A value set is exported once per key. If a value disappears between scan and backup, the whole
  group is skipped rather than deleting an unreviewed sibling.
- Scheduled-task names are validated against traversal and shell metacharacters before `schtasks`.

### Verification

- Release build with warnings-as-errors; 77 tests green.
- Read-only CLI smoke: `doctor`, `list --json`, `junk scan --json`, `scan-leftovers --json`.
- No application was uninstalled during verification.

## [0.1.0] — 2026-10-20

### Added

- Registry inventory of installed applications across HKLM/HKCU and both registry views, with
  search, filters, sorting, icons, export to JSON/CSV, and stable ids.
- Uninstall command resolution for quoted paths, environment variables, MSI product codes, silent
  switches, timeouts and exit-code reporting.
- Post-uninstall verification and explicit forced uninstall of a stale registration entry.
- Registry and file snapshotting, install tracing, trace storage and lookup.
- Exact trace-based leftover detection and a scored heuristic engine for registry keys, app data,
  Program Files, ProgramData, Start Menu shortcuts and desktops.
- Safety guards for paths and registry keys, including reparse points, protected roots, UNC and
  traversal rejection.
- Backup store with manifests, `reg.exe` exports, zip archives, SHA-256 hashes, retention and
  restore.
- WPF desktop app with dark/light themes, programs inventory, uninstall workflow, leftover review,
  autoruns, junk cleaner, backups, settings and Hunter mode.
- Junk cleaner for user/Windows temp, every Chrome/Edge/Brave/Vivaldi/Opera/Firefox profile cache,
  thumbnails, crash dumps/WER, shader caches, WinINet cache and the recycle bin, plus report-only
  Windows Update and Windows.old categories.
- Reversible autorun disable/enable for Run/RunOnce values and Startup folders.
- Hunter mode that maps a window to its process and installed application without injection.
- CLI with JSON output and confirmation gates for inventory, uninstall, leftovers, traces, backups,
  autoruns, junk, hunter and doctor.
- xUnit suite, publish script, CI workflow, user guide, safety model, algorithm reference and
  screenshots.

### Fixed during 0.1.0 verification

- Settings page crash caused by a two-way binding to a read-only diagnostics property.
- Invisible theme selector caused by the default WPF ComboBox template; replaced with a themed
  segmented selector.
- Missing dark window chrome and application icon.
- Registry `scan-leftovers` now protects paths used by other installed applications.
- Junk scan reports categories incrementally instead of leaving the page blank on a large machine.

### Known limitations

- Windows only; no Microsoft Store/MSIX management.
- Traces are per-machine and not portable.
- Scheduled tasks, services, registry values and `HKLM\SYSTEM` cleanup are out of scope for v0.1.
- Release artifacts are unsigned and no installer is included.
