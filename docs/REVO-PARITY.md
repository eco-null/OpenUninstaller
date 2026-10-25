# Capability parity and deliberate boundaries

OpenUninstaller is an original implementation of the deep-uninstaller workflow. It is not affiliated
with Revo Technologies and contains no Revo code, assets, signatures, or databases.

The table below describes what this project can do today, not what a commercial product does
internally. A capability is marked **Yes** only when it is implemented, covered by tests or a
documented manual check, and has a safe failure mode.

| Capability | Status | Implementation |
| --- | --- | --- |
| Inventory of installed programs | Yes | HKLM/HKCU × 64/32 views, filters, search, icons, JSON/CSV |
| Vendor uninstaller execution | Yes | Registered command, quiet preference, MSI product-code handling, timeouts |
| Forced removal of a stale registration | Yes | Explicit force mode with backup and guard |
| Traced installs | Yes | Registry/file snapshots before and after a selected installer |
| Exact trace leftovers | Yes | Schema 2 keys, values, files and directories |
| Heuristic leftovers | Yes | Vendor keys, install folders, AppData/ProgramData, Program Files, shortcuts |
| App Paths / Classes\Applications | Yes | Missing-target or install-location evidence, scoped guard |
| Registry values in shared keys | Yes (traced) | Value-level review, key-export backup, restore |
| Orphaned services | Yes | Missing-image evidence only; no service control or process termination |
| Scheduled tasks | Yes | XML command evidence, `schtasks` delete, XML backup and restore |
| File associations / COM / drivers | No | Requires a dependency model; deliberately not guessed |
| Backup and restore | Yes | Registry exports, files, task XML, SHA-256 manifest, overwrite control, read-only restore preview |
| Batch uninstall queue | Yes | Tick multiple applications; each keeps its own confirmation and leftover review |
| Ignored leftovers | Yes | Per-application persistent ignore list with reset |
| Scan-to-scan diff | Yes | New, resolved and unchanged candidates against the previous scan |
| Portable traces | Yes | SHA-256 bundle with machine-independent root tokens |
| Reversible autoruns | Yes | Run/RunOnce, Startup folders, automatic/manual services, scheduled tasks; audit log history store, selective rollback and Rollback-all in UI and CLI |
| Boot/system driver toggles | No | Excluded to avoid unbootable machines |
| Junk / cache cleaner | Yes | Temp, browser/shader/developer caches, dumps, recycle bin, report-only update/log data |
| Privacy / evidence cleaner | Yes (fixed list) | RunMRU, TypedPaths, RecentDocs, UserAssist, Recent/jump lists, thumbnail/icon caches, app scan state; no browser history or credentials |
| Unrecoverable delete / shredding | Yes (best effort) | Multi-pass random overwrite, flush, truncate, rename, delete; typed confirmation and dry run; SSD caveat documented |
| Telemetry / network access | No (100% offline) | Strictly offline, zero telemetry, zero network calls, complete privacy |
| Background maintenance service | Yes (optional) | Windows service for retention/expiry and optional allow-listed junk cleanup |
| Hunter mode | Yes | Window → process → executable → installed program; never terminates |
| Multi-user / per-SID traces | No | Per-user data; machine-wide operations need elevation |
| Microsoft Store/MSIX inventory | Yes (read-only) | Per-user registered Appx/MSIX package inventory via `MsixInventoryService`, CLI `oucli msix list` and desktop `Store apps` page |
| Browser extension cleanup | No (Future post-v1) | Deferred to post-v1.0 future planning; browser-specific profile databases |
| Installer / MSI | Yes | Inno Setup 6 installer (`OpenUninstaller-<version>-setup.exe`) with silent flags, per-user default, `/ALLUSERS` mode and service isolation (`docs/INSTALLER.md`) |
| Signed binaries | Scripted | `scripts/sign-release.ps1` for Authenticode signing; SHA-256 checksums on all release assets |

## What "full" means here

The project is complete for its declared scope: it builds, tests, publishes, documents, and safely
performs the operations it offers, including read-only MSIX inventory, an optional maintenance
service, an Inno Setup installer, and built-in UI automation smoke tests. It does **not**
claim byte-for-byte feature parity with a closed commercial product. The missing rows above are
deliberate because each one needs a different evidence or safety model.

The most useful next additions, in order:

1. Right-click "Uninstall with OpenUninstaller" shell integration (opt-in).
2. Deeper association/COM cleanup with a dedicated dependency model.
3. Post-v1 future plan: Browser extension inventory and per-profile cleanup (deferred to post-v1.0).
