# Capability Specification and Boundaries

OpenUninstaller is a 100% offline, zero-telemetry, clean and independent open-source deep-uninstaller application for Windows.

The table below describes the full scope of capabilities implemented in OpenUninstaller. A capability is marked **Yes** only when it is implemented, covered by automated tests, and operates with a fail-safe safety model.

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
| File associations / COM / drivers | Yes | Scoped CLSID and ProgID inspection with strict safety guards |
| Backup and restore | Yes | Registry exports, files, task XML, SHA-256 manifest, overwrite control, read-only restore preview |
| Batch uninstall queue | Yes | Tick multiple applications; each keeps its own confirmation and leftover review |
| Ignored leftovers | Yes | Per-application persistent ignore list with reset |
| Scan-to-scan diff | Yes | New, resolved and unchanged candidates against the previous scan |
| Portable traces | Yes | SHA-256 bundle with machine-independent root tokens |
| Reversible autoruns | Yes | Run/RunOnce, Startup folders, automatic/manual services, scheduled tasks; audit log history store, selective rollback and Rollback-all in UI and CLI |
| Boot/system driver toggles | No | Excluded to protect OS integrity and avoid unbootable states |
| Junk / cache cleaner | Yes | Temp, browser/shader/developer caches, dumps, recycle bin, report-only update/log data |
| Privacy / evidence cleaner | Yes | RunMRU, TypedPaths, RecentDocs, UserAssist, Recent/jump lists, thumbnail/icon caches |
| Evidence remover | Yes | Multi-pass wiping of unallocated free disk space with sector zeroing |
| Windows management tools | Yes | Quick launcher for 23 native Windows diagnostic and administrative tools |
| Unrecoverable delete / shredding | Yes | Multi-pass random overwrite, flush, truncate, rename, delete; typed confirmation and dry run |
| Telemetry / network access | No (100% offline) | Strictly offline, zero telemetry, zero network calls, complete privacy |
| Background maintenance service | Yes (optional) | Windows service for retention/expiry and optional allow-listed junk cleanup |
| Hunter mode | Yes | Window → process → executable → installed program resolution |
| Multi-user / per-SID traces | No | Per-user data; machine-wide operations require elevation |
| Microsoft Store / MSIX management | Yes | Full inventory, uninstallation, package data reset, process termination, and location navigation |
| Browser extension cleanup | No (Future post-v1) | Deferred to post-v1.0 future planning; browser-specific profile databases |
| Installer / MSI | Yes | Inno Setup 6 installer (`OpenUninstaller-<version>-setup.exe`) with silent flags, per-user default, `/ALLUSERS` mode and service isolation (`docs/INSTALLER.md`) |
| Signed binaries | Scripted | `scripts/sign-release.ps1` for Authenticode signing; SHA-256 checksums on all release assets |

## Scope and Philosophy

The project is complete for its declared scope: it builds, tests, publishes, documents, and safely performs all operations. It is completely independent, clean, and open-source under the MIT license. Every operation requires explicit confirmation, provides rollback backups, and validates paths against strict safety guards.
