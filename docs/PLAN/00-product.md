# Plan 00 — Product definition

Status: **implemented in v0.5**
Owner: core team

## Problem

Windows applications are responsible for their own removal. Their registered uninstallers often
leave registry keys, configuration folders, caches and shortcuts behind, and the operating system
offers no review step. Users are left with an uninstaller that either does too little (Programs and
Features) or does too much without permission (aggressive "cleaners").

## Goal

Deliver a trustworthy, fully open-source deep uninstaller for Windows that:

1. shows an accurate inventory of installed applications;
2. runs the application's own uninstaller rather than reimplementing it;
3. discovers leftovers with evidence (trace or heuristic), showing confidence;
4. never deletes anything without an explicit, item-level decision;
5. backs up everything first and can restore it;
6. is inspectable: MIT code, no telemetry or network calls unless the user opts in with their own
   endpoint, and no service unless the user explicitly installs the optional maintenance service.

## Non-goals

- Bypassing licence checks, trial resets, or DRM. Out of scope permanently.
- Modifying another vendor's installed product (including Revo Uninstaller) automatically.
- Store/MSIX removal, driver removal, Windows component servicing.
- A cloud trace database. Traces stay local.
- Anti-tamper or self-protection mechanisms.
- Browser history, cookie, password or session-database cleanup. The history cleaner covers
  Windows Explorer traces only; browser data needs a separate profile model.

## Users

- **Desktop power user** who wants a clean removal and a review step.
- **Support technician** who needs logs, backups and a scriptable CLI.
- **Developer** who wants to trace what their own installer wrote.

## Feature scope, mapped to public deep-uninstaller capabilities

| Capability | v0.5 | Notes |
| --- | --- | --- |
| Installed program list | Yes | HKLM/HKCU, 64/32-bit views, sorting, search, export |
| Uninstall with leftover scan | Yes | Registered command, silent preference, MSI |
| Forced uninstall | Yes | Removes registration and matched traces; requires confirmation |
| Traced installs | Yes | Snapshot before/after a chosen installer |
| Backup and restore | Yes | Registry exports, files, JSON manifest, SHA-256, restore preview |
| Batch uninstall queue | Yes | Queued applications keep their own confirmation and review |
| Ignored leftovers and scan diff | Yes | Per-application ignore list and previous-scan comparison |
| Portable traces | Yes | SHA-256 bundle with machine-independent path tokens |
| Autorun manager | Yes | Run/RunOnce (user+machine), Startup folders, services, scheduled tasks |
| Junk cleaner | Yes | Temp, browser/shader/developer caches, dumps, recycle bin; allow-listed |
| Registry-value leftovers | Yes (traced) | Values added to shared keys are reviewed individually |
| App Paths / service / task leftovers | Yes | Scoped evidence; orphaned services and executable tasks only |
| Privacy history cleaner | Yes | Fixed Explorer/app list; typed CLEAN; no backup by design |
| File shredder | Yes | Multi-pass best effort; typed SHRED; dry run; guard-checked |
| Opt-in telemetry | Yes | Off by default; user-supplied HTTPS endpoint; anonymous payload |
| Maintenance service | Yes (optional) | Retention/expiry, optional junk cleanup, optional heartbeat |
| Hunter mode | Yes | Window → process → owning application |
| Multi-user traces | No | Traces are local to the machine and account |
| Browser history/cookie cleanup | No | Needs a separate browser-profile database model |
| Browser extension cleanup | No | Roadmap |
| Right-click shell integration | No | Roadmap |

## Trust commitments

1. **No silent deletion.** Every removal is preceded by a review (UI) or `--yes` (CLI).
2. **Evidence over guessing.** Trace matches are labelled exact; heuristic matches are labelled by
   confidence and are never preselected unless high confidence.
3. **Reversible by default.** Registry keys and files are backed up before deletion; the backup
   manifest is verifiable. Shredding and privacy history cleanup are the two labelled, typed-
   confirmation exceptions because a backup would recreate the data being removed.
4. **Bounded.** Snapshots and scans have hard caps and cancellation; no unbounded recursion.
5. **Inspectable.** No obfuscation, no network calls by default, deterministic JSON outputs.
6. **Respectful of other software.** OpenUninstaller does not target, patch or remove other
   uninstaller products. It only acts on the entry and leftovers the user selected.

## Success criteria

- `dotnet build` and `dotnet test` pass on a clean Windows machine with .NET 8.
- Inventory returns real applications on the build machine.
- A synthetic application installed under HKCU can be listed, uninstalled via a stub command,
  scanned, backed up, deleted and restored, with tests covering each step.
- UI launches, shows the inventory, and completes the uninstall review flow.
- No operation outside the allow-list can be performed through the public API.
- 100% of destructive paths have a test proving the guard rejects unsafe input.

## Milestones

- **M1 — Core**: models, inventory, uninstall runner, logging. (shipped)
- **M2 — Leftovers**: snapshots, tracing, heuristic scan, review model. (shipped)
- **M3 — Safety**: backup/restore, path guard, allow-lists. (shipped)
- **M4 — Tools**: autoruns, junk, hunter. (shipped)
- **M5 — UI**: MVVM shell, dashboard, flows. (shipped)
- **M6 — Release**: CLI polish, CI, docs, packaging. (shipped)
- **M7 — Privacy and maintenance**: history cleaner, shredder, opt-in telemetry, optional service.
  (shipped)
