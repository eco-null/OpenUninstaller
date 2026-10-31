# OpenUninstaller — complete task list

Legend: `[x]` done and verified · `[~]` partially done · `[ ]` not done.
This file is the single source of truth for what remains. Update it with every merged change.
Last updated: 2026-10-31 (v1.0.5 desktop interface and documentation refresh).

## v1.0.5 — Desktop interface and documentation

- [x] Coordinate light/dark zinc themes, shared controls and native icon assets
- [x] Add resizable application details and compact-window adaptation
- [x] Remove redundant interface copy and decorative status labels
- [x] Refresh README, screenshots and design documentation
- [x] Verify all eight screens in both themes and two window sizes, including search and selection

## Phase 1 — Foundations

- [x] Create solution, Core/CLI/WPF/test projects (`net8.0-windows`)
- [x] Directory.Build.props: version, nullable, warnings-as-errors, deterministic builds
- [x] Zero third-party packages in production projects
- [x] Result models and error conventions (`OperationResult`, warnings, per-item outcomes)
- [x] Ports: registry store, filesystem, process runner, clock, operation log, elevation
- [x] Host context: machine paths, AppData/ProgramData roots, version, admin state
- [x] Operation logger writing JSONL to `%ProgramData%\OpenUninstaller\Logs`
- [x] CLI argument parser and exit-code contract (0/1/2/3/4/5/6)
- [x] `doctor` command (OS, admin, paths writable, counts)
- [x] MIT licence, README, CONTRIBUTING, SECURITY, CHANGELOG

## Phase 2 — Inventory and uninstall

- [x] Enumerate HKLM/HKCU × 64/32 uninstall keys
- [x] Filter system components, updates, patches; optional reveal toggle
- [x] Parse DisplayIcon, EstimatedSize, InstallDate; stable SHA-based ids
- [x] Inventory search, sorting, CSV/JSON export
- [x] Uninstall command parser (quoted paths, args, env expansion)
- [x] MSI product-code rewriting; quiet command preference
- [x] Run with shell execute, capture exit code, timeout, reboot exit codes
- [x] Post-uninstall verification (removed / still registered / replaced)
- [x] Forced uninstall of the registration entry, guard-checked
- [x] CLI `list`, `uninstall`, `force-uninstall`
- [x] Unit tests: scanner, filters, command resolution, runner

## Phase 3 — Leftovers and tracing

- [x] Registry snapshotter with depth/entry caps and cancellation
- [x] File snapshotter with reparse-point skip and caps
- [x] Snapshot diff (added keys, added values, added files)
- [x] Trace model, JSON store under `%ProgramData%\OpenUninstaller\Traces`
- [x] `trace-install` runner (snapshot → install → snapshot → save)
- [x] Trace lookup by name and install location
- [x] Trace schema 2: new keys and values added to pre-existing keys are separate evidence
- [x] Value-level trace leftovers with per-value deletion, key export backup and restore
- [x] Schema 1 traces protect ambiguous registry keys instead of deleting whole keys
- [x] Trace-based exact leftover scan
- [x] Heuristic registry scan (vendor branches, both views, combined vendor\product scoring)
- [x] Heuristic file scan (install dir, ProgramData/AppData/Public, LocalAppData\Programs, Program Files, Start Menu, desktop)
- [x] Nested vendor\product folder detection; publisher-only folders are never offered as a whole
- [x] Cross-application ownership protection (skip for heuristics, protected for traces)
- [x] Token normalization and confidence scoring (100/90/70/40/20)
- [x] De-duplication and child suppression
- [x] App Paths / Classes\Applications heuristic with a scoped direct-child guard
- [x] Orphaned `HKLM\SYSTEM` service detection with a scoped service guard
- [x] Scheduled-task detection from task XML, `schtasks` deletion and XML backup/restore
- [x] CLI `scan-leftovers`, `traces list`, `traces delete`
- [x] Unit tests: snapshots, diff, trace round-trip, scoring, values, registrations, services, tasks

## Phase 4 — Safety, backup, restore

- [x] PathGuard rules (system roots, reparse points, `..`, UNC, own folders)
- [x] RegistryGuard rules (hives, protected roots, minimum depth, uninstall entry flag)
- [x] Backup store with manifest, registry exports, file zips, SHA-256
- [x] Deletion pipeline: guard → backup → delete → verify → report
- [x] Restore service (registry import + file extraction, overwrite flag)
- [x] Retention pruning (keep newest N, never during restore)
- [x] CLI `backups list|restore|prune`, `--dry-run`, `--yes` gates
- [x] Unit tests: every guard rule, backup/restore round-trip, retention
- [x] Documented safety model for users (`docs/SAFETY.md`)

## Phase 5 — Tools

- [x] Autorun sources: Run/RunOnce (user/machine), Startup folders
- [x] Autorun sources: automatic/manual Windows services and scheduled tasks (v0.3)
- [x] Reversible disable/enable with conflict detection
- [x] Autoruns search/filter by name, command, source, location and publisher
- [x] Boot/system drivers excluded from autorun toggles
- [x] Junk categories: user/Windows temp, every Chromium/Firefox/Opera profile cache, thumbnails, crash dumps/WER, system dumps/live-kernel reports, shader caches, WinINet, Delivery Optimization, developer package caches, recycle bin
- [x] Report-only Windows Update cache, Windows.old, servicing logs and prefetch; browser-running warning
- [x] Junk scan with size/count, recent-file skip, locked-file reporting, incremental category reporting
- [x] Junk clean with dry run and allow-list
- [x] Hunter resolver: hwnd → PID → exe → installed program (path/token/icon match)
- [x] CLI `autoruns`, `junk`, `hunter`, `windows-tools`
- [x] Unit tests: autorun round-trip, junk allow-list, hunter matching

## Phase 6 — Desktop UI

- [x] MVVM shell, navigation, status bar, dark/light theme
- [x] Programs page: search, filter, sort, details, icons
- [x] Uninstall workflow: confirm → run → review → result, with cancellation
- [x] Leftover review tree with confidence badges and protected items
- [x] Autoruns page with toggles and search filter
- [x] Junk page with categories and dry run
- [x] Backups page with restore
- [x] Settings page and About
- [x] Hunter mode overlay
- [x] Admin detection and "restart elevated" action
- [x] Startup smoke test (process remains alive)
- [x] Settings binding crash fixed; theme selector replaced with a themed segmented control
- [x] Dark window chrome and application icon
- [x] Navigation AutomationProperties names for UI automation/accessibility

## Phase 7 — Release and verification

- [x] xUnit suite green on Windows/.NET 8
- [x] Build Release with warnings as errors
- [x] CLI smoke: `doctor`, `list --json`, `junk scan --json`, `autoruns list --json`,
      `scan-leftovers --json` (scan-only, no uninstall)
- [x] v0.4 CLI smoke: `scan-leftovers --diff`, synthetic trace bundle export/import
- [x] UI smoke: launch, screenshot, verify inventory renders
- [x] REA managed-inspection audit of published assemblies
- [x] publish.ps1 (self-contained x64) + CI workflow
- [x] Tag-triggered release workflow with zip + SHA-256 (v0.3)
- [x] Issue forms, PR template, Dependabot, Code of Conduct and support guide (v0.3)
- [x] README, USER-GUIDE, SAFETY, ALGORITHMS, BACKLOG, screenshots
- [x] CONTRIBUTING, SECURITY, CHANGELOG, .gitignore
- [x] GITHUB.md release checklist and CAPABILITIES matrix (v0.3)
- [x] Release build, 88 tests, CLI smoke (no application was uninstalled), UI pages screenshotted
- [x] v0.3 package and release workflow dry-run (`package-release.ps1`, zip + SHA-256)
- [x] Final release pass: republished after UI fixes, full test suite green, CLI version/help
      verified, six pages recaptured, self-contained zip and SHA-256 regenerated
- [x] v0.4.0 package and release workflow dry-run after the new workflow features
- [x] v0.5.0 verification: 99 tests, release build, CLI privacy/telemetry/service smoke, package
- [x] Code-signing helper script (`scripts/sign-release.ps1`, documented in `docs/GITHUB.md`)
- [x] UI smoke tests via built-in UI Automation (`tests/OpenUninstaller.UiSmoke`)
- [x] Microsoft Store (MSIX) inventory source (`MsixInventoryService`, `StoreAppsView`, `oucli msix list`)
- [x] Optional: scheduled task and service autorun sources (v0.3)
- [x] Optional: registry-value leftover model for traced values in shared keys
- [x] Resolve .lnk targets for shortcut evidence (`ShortcutResolver` via native `IShellLinkW`)

## Phase 8 — Safe workflow features (v0.4)

- [x] Batch uninstall queue: tick programs, review each in its own confirmation/leftover window
- [x] Persist ignored leftovers per application; hide from later scans and reset on demand
- [x] Scan-to-scan history and diff (new, resolved, unchanged) in UI and `scan-leftovers --diff`
- [x] Portable trace bundle export/import with SHA-256 and machine-independent root tokens
- [x] Restore preview in the Backups page and `backups preview`
- [x] Unit tests for ignored state, scan history, trace bundles and restore preview

## Phase 9 — Privacy, shredding and maintenance (v0.5)

- [x] Fixed-list Windows history cleaner with scan, per-category selection, dry run and typed CLEAN
- [x] Multi-pass file/folder shredder with guard checks, dry run, typed SHRED and read-only handling
- [x] Shared maintenance pass: backup retention, log/scan-history expiry, optional junk cleanup
- [x] Optional Windows maintenance service, `sc.exe` install/remove, Settings card and CLI controls
- [x] Privacy & shredder desktop page, navigation entry and status-bar offline state
- [x] Permanently removed telemetry services and network-calling code (strictly 100% offline)
- [x] Tests for shredding, privacy categories, retention and service query

## Phase 10 — Packaging, MSIX inventory, shortcut resolution and UI automation (v0.6)

- [x] Read-only MSIX / Microsoft Store package inventory (`MsixInventoryService`, `StoreAppsView`, `oucli msix list`)
- [x] Native COM `IShellLinkW` shortcut target resolution (`ShortcutResolver`, integrated into `LeftoverScanner`)
- [x] Inno Setup 6 installer (`installer/OpenUninstaller.iss`, `scripts/build-installer.ps1`, `docs/INSTALLER.md`)
- [x] Authenticode code-signing helper script (`scripts/sign-release.ps1`)
- [x] Zero-dependency UI Automation smoke test project (`tests/OpenUninstaller.UiSmoke`) covering all 8 views
- [x] CI & release workflow integration for UI smoke and installer packaging
- [x] Service/task modification history and rollback view (`AutorunHistoryStore`, `oucli autoruns history|rollback`, WPF History tab)

## Known gaps (v0.6)

- Service/task autorun disabling needs administrator rights for machine-wide entries. A service or
  task disabled outside OpenUninstaller has no stored original state, so it cannot be restored by
  the app.
- Registry values are cleaned only when a schema 2 trace proves they were added; modified values and
  values inside protected Microsoft trees are not offered.
- Services are removed only when their image is missing; they are not stopped.
- Scheduled tasks with COM handlers or outside the XML task store are not covered.
- File associations outside `Classes\Applications`, COM registration, drivers, `PATH` entries and
  firewall rules are not cleaned.
- Portable traces normalize known Windows roots; paths outside them stay absolute and remain
  guard-checked. File content hashes are not part of a trace.
- Ignored leftovers are keyed by application id/name; a renamed application starts fresh.
- Scan history keeps one snapshot per application display name.
- The batch queue is reviewed one application at a time, not an unattended mass removal.
- Restore preview is advisory; the restore revalidates everything before writing.
- Privacy cleaning intentionally omits browser history, cookies, saved passwords, session stores
  and account data; those are browser-owned and need a separate profile database model.
- Shredding is best effort; SSD wear-levelling, filesystem journals, snapshots and existing backups
  can retain copies. It is not a certified sanitisation standard.
- Thumbnail/icon cache files held open by Explorer are skipped; Explorer is not restarted.
- Zero telemetry: strictly 100% offline, privacy-first, and zero network calls. No telemetry services exist.
- The maintenance service needs administrator rights to install/remove and runs as LocalSystem.

## Cross-cutting invariants (must stay true)

- [x] 100% offline: zero telemetry and zero external network calls under all conditions
- [x] No automatic destructive action without explicit approval
- [x] Every deletion is preceded by a backup except typed-confirmation shredding and privacy cleanup
- [x] All filesystem mutations go through `PathGuard`; registry mutations through the service allow-list or `RegistryGuard`
- [x] Privacy targets are hard-coded and cannot be added by a scan
- [x] No service is installed without an elevated, explicit install action
- [x] No third-party runtime dependency
- [x] Original clean-room code and assets only; completely independent implementation
- [x] The tool never modifies another uninstaller's installation
