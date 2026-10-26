# Backlog and progress log

This file tracks progress per phase and holds ideas that are not yet scheduled. Detailed tasks
live in [TODO.md](../TODO.md); plans live in [docs/PLAN](PLAN/).

## Progress log

### 2026-10-26 — v1.0.2 backup, restore and cleanup safety

- Hardened restore destination and registry import boundaries, preserved empty backup directories,
  rejected linked trees, and protected active restores from retention pruning.
- Kept recent and unexamined junk descendants and bounded trace bundle manifest reads.
- Added regression tests for the identified failures and Windows filesystem race attempts.

### 2026-10-25 — v0.6.0 packaging, MSIX inventory, shortcut resolution and UI automation

- Added read-only Microsoft Store / MSIX package inventory (`MsixInventoryService`) scanning per-user
  registered Appx and MSIX packages, display name, publisher, architecture and install location.
  Added a dedicated `Store apps` page in the desktop app and `oucli msix list` in the CLI.
- Added native COM shell link target resolution (`ShortcutResolver`, `IShellLinkW`) with zero external
  libraries. Integrated into `LeftoverScanner` to detect leftover shortcuts on Start Menu and Desktop.
- Added Inno Setup 6 installer (`installer/OpenUninstaller.iss`), build script (`scripts/build-installer.ps1`),
  code-signing helper script (`scripts/sign-release.ps1`), and comprehensive guide (`docs/INSTALLER.md`).
- Added zero-dependency UI Automation smoke test project (`tests/OpenUninstaller.UiSmoke`) covering all 8
  main UI pages, integrated into the solution and CI.
- Updated test suite to 110 tests; verified 100% green without destructive actions on host machine.

### 2026-10-25 — v0.5.0 privacy, shredding, telemetry and maintenance service

- Added a Privacy & shredder page. The Windows history cleaner covers a fixed list (RunMRU,
  TypedPaths, RecentDocs, UserAssist, Recent/jump lists, thumbnail/icon caches) plus the app's own
  scan snapshots, ignored ids and logs. No backup is taken by design; the app requires a typed CLEAN.
- Added a multi-pass file shredder for files and folders, with dry run, typed SHRED, guard checks,
  reparse-point skipping and read-only handling. Documented as best effort on SSDs and journals.
- Added opt-in telemetry with local consent and a user-supplied HTTPS endpoint. No endpoint is
  built in; the payload is limited to version, OS build, architecture, a random install id and
  counters.
- Added a shared maintenance pass and an optional Windows maintenance service for backup retention,
  log/scan-history expiry, optional allow-listed junk cleanup and an optional telemetry heartbeat.
- Extended the test suite to 99 tests and updated README, user guide, safety model, parity matrix,
  plan status, TODO and changelog.

**Verification:** Release build with warnings as errors; 99 tests green; CLI smoke
(`privacy scan --json`, `privacy clean --dry-run`, `privacy history`, `telemetry status`,
`telemetry send` while disabled, `service status --json`); a real 2-pass shred of a synthetic temp
file; packaged 0.5.0 zip inspected for the app, CLI and maintenance service. The published app
launched and rendered the Privacy page. No history category, installed application, service or
user file was removed during verification.

### 2026-10-22 — v0.4.0 safe workflow features

- Added a batch uninstall queue on the Programs page. Tick several applications and review each one
  in its existing confirmation and leftover window; nothing is deleted in the background.
- Added a persistent per-application ignore list for leftovers. **Ignore selected** hides items from
  later scans, **Reset ignored** clears the list and rescans.
- Added scan-to-scan history: the review window and `scan-leftovers --diff` report new, resolved and
  unchanged candidates against the previous snapshot.
- Added portable trace bundles with machine-independent root tokens and a SHA-256 trace hash.
  `traces export` and `traces import` reject damaged or edited bundles.
- Added a read-only restore preview in the Backups page and `backups preview`, showing restore,
  overwrite, skip and blocked targets before anything changes.
- Extended the test suite to 88 tests and updated README, user guide, plans, parity matrix,
  algorithms, TODO and changelog.

**Verification:** Release build with warnings as errors; 88 tests green; read-only CLI smoke
(`doctor`, `list`, `junk scan`, `autoruns list`, `scan-leftovers --diff`); synthetic trace bundle
export/import round-trip; desktop pages recaptured. No installed application was uninstalled and no
autorun was toggled.

### 2026-10-22 — v0.3.0 final release pass

- Fixed the status bar layout so the privacy note and version render on opposite sides.
- CLI `version` and `help` now read the assembly version instead of a hard-coded string, so release
  builds cannot drift from `Directory.Build.props`.
- Rebuilt and republished the self-contained x64 app, reran all 81 tests, and repeated the
  read-only CLI smoke (`doctor`, `list`, `junk scan`, `autoruns list`, `scan-leftovers`).
- Recaptured all six desktop pages and refreshed `docs/assets`.
- Regenerated `OpenUninstaller-0.3.0-win-x64.zip` and its SHA-256 checksum from the final binaries.
  No application was installed or uninstalled during verification.

### 2026-10-21 — v0.3.0 autoruns, services and tasks, GitHub release readiness

- Autoruns now covers automatic/manual Windows services and scheduled tasks. Disabling a service
  records its original start mode and sets it to Disabled without stopping it; disabling a task
  uses `schtasks` and keeps its definition. Boot/system drivers are excluded. Enabling restores the
  stored state and refuses conflicting changes.
- Added live search/filter to the Autoruns page and clearer confirmation text per source.
- Added tag-triggered release workflow, `package-release.ps1`, issue forms, PR template,
  Dependabot, Code of Conduct, support guide, `docs/GITHUB.md` and `docs/CAPABILITIES.md`.
- Updated README, user guide, plan, TODO and changelog for v0.3.0.

**Verification:** Release build with warnings-as-errors; complete xUnit suite; read-only CLI
smoke tests and desktop screenshot checks. No application was uninstalled during this pass.

### 2026-10-21 — v0.2.0 complete leftover/junk algorithms

- Trace schema 2 separates keys created by an installer from values added to pre-existing keys.
  Traced values are reviewed and deleted individually; the containing key is exported and kept.
- Schema 1 traces are handled safely by protecting ambiguous registry key candidates.
- Added App Paths and `Classes\Applications` detection with a scoped direct-child guard.
- Added orphaned service detection; only services whose image is missing are offered, and the
  Service Control Manager is never queried or stopped.
- Added scheduled-task detection from task XML, `schtasks` deletion, XML backup and `schtasks`
  restore.
- Extended junk coverage: Delivery Optimization, npm/pip/NuGet/Yarn caches, LiveKernelReports,
  report-only servicing logs and prefetch.
- Updated ALGORITHMS, SAFETY, USER-GUIDE, PLAN/03, PLAN/05, TODO and this log.

**Verification:** Release build with warnings-as-errors; 77 tests green; CLI `doctor`, `list`,
`junk scan` and `scan-leftovers` smoke-tested read-only. No application was uninstalled during
this pass.

### 2026-10-20 — v0.1.0 (Phases 1–7)

- Scaffolded the solution: Core, CLI, WPF app, xUnit tests, CI, publish script.
- Implemented inventory across HKLM/HKCU and both registry views with update/component filters.
- Implemented uninstall execution with MSI rewriting, quiet preference, exit codes, timeouts and
  post-uninstall verification.
- Implemented registry/file snapshots, install tracing, exact and heuristic leftover detection with
  confidence scoring.
- Implemented guards, backup store (manifest + `.reg` exports + zip archives), restore and
  retention.
- Implemented autorun manager, junk cleaner, hunter resolver and backups browser.
- Implemented the WPF shell and all planned pages/workflows with a dark theme.
- Wrote tests for every core service, plus CLI and UI smoke verification.
- Audited published assemblies with REA managed-PE inspection.

**Verification:** `dotnet test` green; Release build with warnings-as-errors; CLI smoke on the build
machine; UI launches and renders the real inventory; REA identifies the app assembly as managed
x86_64 and lists the expected P/Invoke boundaries.

### 2026-10-20 — final v0.1 verification pass

- Expanded the leftover engine: nested vendor\product detection, no publisher-folder deletion,
  cross-application ownership protection for heuristics and traces, Program Files/`Programs` roots.
- Expanded the junk engine: every Chromium/Firefox/Opera profile, crash dumps/WER, system dumps,
  shader caches, WinINet, report-only Windows.old, browser-running warnings, incremental UI rows.
- Fixed the Settings page crash (TwoWay binding to a read-only property) and replaced the invisible
  default ComboBox with a themed Dark/Light selector.
- Added dark window chrome, an application icon and navigation automation names.
- Added `ALGORITHMS.md`, `SAFETY.md`, `USER-GUIDE.md`, `CONTRIBUTING.md`, `SECURITY.md`,
  `CHANGELOG.md`, `.gitignore`, `scripts/publish.ps1` and a Windows CI workflow.
- Recaptured six UI pages after manual navigation; binding diagnostics log is empty.
- Tests: 65 passing. No uninstall was executed during UI verification; the machine's installed
  applications were only listed and scanned read-only.

**Known gaps:** see TODO.md optional items and the limitations section of the README.

## Now (v0.6)

- [x] Persist "ignored leftovers" per application so repeated scans stay clean (v0.4)
- [x] Leftover diff view between two scans of the same application (v0.4)
- [x] Trace portability: export/import a trace bundle with hashes and machine-independent roots (v0.4)
- [x] Restore preview (v0.4)
- [x] Batch uninstall queue with per-application review (v0.4)
- [x] Fixed-list Windows history cleaner with typed confirmation (v0.5)
- [x] Multi-pass file shredder with dry run and best-effort caveat (v0.5)
- [x] 100% offline privacy guarantee: permanently purged all telemetry and external network calling code (v0.6)
- [x] Optional maintenance service for retention/expiry and optional junk cleanup (v0.5)
- [x] MSIX / Microsoft Store application inventory (read-only listing, v0.6)
- [x] Resolve .lnk targets for shortcut evidence (ShortcutResolver via IShellLinkW, v0.6)
- [x] Inno Setup installer and signed release artifacts (scripts/build-installer.ps1, scripts/sign-release.ps1, v0.6)
- [x] UI automation smoke tests in CI (tests/OpenUninstaller.UiSmoke, v0.6)
- [x] Service/task history and a rollback view (AutorunHistoryStore, UI history tab, oucli autoruns history/rollback, v0.6)

## Next

- [ ] Right-click "Uninstall with OpenUninstaller" shell integration (opt-in)
- [ ] Multi-user trace store (per-SID traces, admin view)
- [ ] Scheduled "junk scan" reminder in the app (the optional service already handles retention)
- [ ] Localization infrastructure (en, tr, de, es) with RTL support
- [ ] Browser profile history/cookie cleanup with a separate database model

## Post-v1.0 / Someday / ideas

- [ ] Post-v1: Browser extension inventory and per-browser profile cleanup
- [ ] Signed trace database shared by the community (opt-in, strictly offline verification)
- [ ] Portable mode storing traces/backups next to the executable
- [ ] Installer snapshot comparison ("what did this update add?")
- [ ] Read-only integrity check that reports unsigned/unusual uninstall commands
- [ ] Command palette (Ctrl+K) for every action
- [ ] Plugin API for custom scanners with a strict capability model

## Won't do

- Any telemetry, tracking, analytics, or network calls. The application is strictly 100% offline.
- Bypassing licensing or trial-reset features of other products.
- Killing processes or unlocking files automatically during uninstall.
- A background service that installs without an explicit elevated action or does more than the
  documented retention, expiry and junk allow-list.
- Uploading user traces, paths, program names or application lists.
- Targeting or modifying other third-party uninstaller products.

## Decision log

| Date | Decision | Why |
| --- | --- | --- |
| 2026-10-25 | Telemetry permanently removed | OpenUninstaller is strictly 100% offline and privacy-first; all telemetry services purged |
| 2026-10-25 | Browser extension deferred to post-v1 | Keep core application clean, focused, and ready for publication without third-party extension friction |
| 2026-10-22 | Privacy history cleaning is irreversible and skips backups | A backup of the history would defeat the purpose; typed confirmation replaces the backup gate |
| 2026-10-22 | Shredding is labelled best effort, not a certified wipe | SSD wear-levelling and journals make guarantees impossible; the app states the limit |
| 2026-10-22 | Telemetry ships off with no vendor endpoint | Consent must be explicit and the operator must be the user; the app never invents a collector |
| 2026-10-22 | The maintenance service is optional and uses a dependency-free SCM host | Keeps the zero-package policy and avoids always-on privileges without explicit install |
| 2026-10-21 | Service/task autoruns store the original start mode/task name, not a copy of the service | The service database is system-owned; one `Start` value is enough to make a toggle reversible |
| 2026-10-21 | Exclude boot/system drivers from autoruns (Start 0/1) | A general-purpose toggle must not be able to make a machine unbootable |
| 2026-10-21 | Release artifacts are built by tag workflow, never uploaded from a workstation | Reproducible inputs, tests before packaging and a checksum beside the zip |
| 2026-10-21 | Trace schema 2 separates new keys from added values | Deleting a shared key to remove one value would destroy sibling evidence |
| 2026-10-21 | Schema 1 key candidates stay protected | The old evidence cannot prove the key was new |
| 2026-10-21 | App Paths/Applications and service guards are scoped direct-child flags | Keeps Microsoft/SYSTEM protection while allowing one reviewed registration |
| 2026-10-21 | Offer only orphaned services and only tasks with executable evidence | A live service or a name-only task is not safe scan evidence |
| 2026-10-21 | Backup task XML and recreate with `schtasks /Create` | Restore should use the Task Scheduler, not copy a cache file blindly |
| 2026-10-20 | .NET 8 + WPF, no third-party packages | Trust for an elevated tool; Windows-only is acceptable |
| 2026-10-20 | Trace-first leftovers, heuristic as fallback | Exact evidence beats guessing; matches the deep-uninstaller promise |
| 2026-10-20 | Snapshot-diff instead of a proprietary signature database | Transparent, local, and works without a cloud service |
| 2026-10-20 | Never auto-delete; item-level review | Safety and reversibility are the product |
| 2026-10-20 | `reg.exe` for registry backup/restore | Battle-tested export format that users can inspect |
| 2026-10-20 | Uninstaller runs with shell execute | UAC and MSI repair flows behave exactly as the vendor expects |
