# Plan 04 — Safety, backup and restore

Status: **implemented in v0.2**

## Threat model

OpenUninstaller is a user-invoked tool that may run elevated and deletes registry keys and files.
Risks considered:

| Risk | Mitigation |
| --- | --- |
| Deleting unrelated system data | Allow-list of roots; explicit guard rules; per-item review |
| Following a directory junction to a system folder | Reparse points are never traversed or deleted recursively; guard resolves attributes |
| Vendor uninstaller misbehaves | Command is shown verbatim; the tool never injects into or patches the uninstaller |
| Elevated arbitrary code | No network by default, no auto-update, no shell string concatenation, no unsigned remote payloads; telemetry needs opt-in and a user-supplied endpoint |
| Backup loss | Backup manifest with absolute targets and hashes, restore verification |
| Log leakage | Logs contain paths and command lines only; no tokens or environment dumps |
| Killing the wrong service | OpenUninstaller never stops services or terminates processes automatically |
| Shredding the wrong path | Typed `SHRED` confirmation, dry run, `PathGuard` per target, reparse points refused |
| Privacy history being recovered | History categories are deleted without a backup by design; the UI states this and requires typed `CLEAN` |

## Guard rules (non-negotiable)

`PathGuard.ValidateForDeletion(path)` rejects:

- empty, whitespace, non-absolute paths;
- drive roots (`C:\`) and UNC roots;
- `%WINDIR%` and everything under it;
- `%ProgramFiles%` and `%ProgramFiles(x86)%` themselves, and their direct children that are not
  part of a scanned leftover (allow-list is computed by the scanner, not by the user);
- `%SystemDrive%\Users` itself, `%USERPROFILE%` itself, `%APPDATA%` itself, `%LOCALAPPDATA%` itself;
- the root of every scanned location (e.g. `C:\ProgramData`);
- paths containing `..` after normalization;
- reparse points (junction/symlink) when the delete is recursive;
- OpenUninstaller's own install, backup and log directories.

`RegistryGuard.ValidateForDeletion(location)` rejects:

- hives other than HKLM/HKCU;
- `HKLM\SOFTWARE`, `HKLM\SYSTEM`, `HKLM\SAM`, `HKLM\SECURITY`, `HKCU\Software`;
- any path with fewer than three components;
- the Uninstall roots themselves (allowed only with the explicit `allowUninstallEntry` flag);
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion` and children;
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run*` (handled by the autorun service with its
  own disable/restore mechanism, never by raw deletion from a leftover scan).

The guards return a reason string. Every rejected item appears in the review list as "protected"
and cannot be selected.

## Backup format

```
%ProgramData%\OpenUninstaller\Backups\
└─ 20261020-163500-example-app-a1b2c3\
   ├─ manifest.json
   ├─ registry\
   │  ├─ 001-HKLM-SOFTWARE-VendorExample.reg
   │  └─ 002-HKCU-Software-VendorExample.reg
   └─ files\
      └─ <mirrored path>.zip   (one archive per backup)
```

`manifest.json`:

```json
{
  "schemaVersion": 2,
  "id": "20261020-163500-example-app-a1b2c3",
  "createdAt": "2026-10-20T16:35:00Z",
  "programName": "Example App",
  "appVersion": "0.2.0",
  "registry": [
    { "hive": "HKLM", "view": "Registry64", "path": "SOFTWARE\\Vendor\\Example",
      "exportFile": "registry\\001-....reg", "sha256": "...", "values": [] }
  ],
  "files": [
    { "originalPath": "C:\\ProgramData\\Example", "archiveEntry": "Example/",
      "kind": "directory", "sha256": "..." },
    { "originalPath": "C:\\Windows\\System32\\Tasks\\Example\\Updater",
      "archiveEntry": "C/Windows/System32/Tasks/Example/Updater",
      "kind": "scheduled-task", "taskName": "Example\\Updater", "sha256": "..." }
  ]
}
```

- Registry exports are produced by `reg.exe export <key> <file> /y`; the file is hashed after export.
- Files are archived with `ZipFile.CreateFromDirectory` preserving relative names under a per-target
  prefix. Empty directories are recorded but not archived.
- A backup is committed only if every selected item was captured. Partial backups keep the
  successfully captured items with `complete: false`; deletion proceeds only for captured items.
- Retention: keep the newest 20 backups and any backup younger than 30 days; older ones are pruned
  by the settings service (never while a restore is running).

## Restore

1. Read and validate the manifest (schema version, hashes).
2. Registry: `reg import <file>` per entry; verify the key exists afterwards.
3. Files: extract archive entries back to their original absolute paths only if the path still
   passes the guard. Existing files are overwritten only with `overwrite: true` (UI checkbox, CLI
   `--overwrite`).
4. Report per-entry success/failure; never roll back a partially restored backup automatically.

## UI safeguards

- Uninstall confirmation lists the exact command and the registry entry.
- Review screen defaults to high-confidence items only; "select all" is available but shows a
  warning count.
- Destructive buttons are `DangerButton` styled and require a second confirmation below the list.
- The admin badge is visible at all times; elevation is offered, never silently requested.
- Every completed operation links to its log and backup.

## CLI safeguards

- `--yes` required for `uninstall`, `force-uninstall`, `restore` cleanup, `junk clean`, and
  `backups prune`.
- `--dry-run` for `junk clean` and leftover deletion prints the plan and exits 0.
- Exit codes: 0 success, 1 error, 2 confirmation required, 3 ambiguous target, 4 not found,
  5 elevation required, 6 cancelled.

## What we explicitly do not do

- No automatic deletion of "leftovers" without a user decision.
- No service/process termination, no file-in-use unlocking, no registry ACL changes.
- No modification of another uninstaller product's installation.
- No restore points or shadow copy manipulation (backup is our own, transparent store).
