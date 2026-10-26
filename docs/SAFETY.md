# Safety model

OpenUninstaller is a privileged-adjacent tool: it can call vendor uninstallers and delete registry
keys and files. The design tries to make every destructive action explicit, reviewable and
reversible where possible.

## Guarantees

1. **No silent deletion.** Scanning never deletes. Leftover deletion requires item-level selection
   and a confirmation. Junk cleaning requires category selection and a confirmation.
2. **Backups first, with two explicit exceptions.** Every leftover registry key, registry value,
   service definition, scheduled task and file is exported or archived before removal. If the backup
   fails, the item is skipped. There is no "continue without backup" option. The two exceptions are
   **shredding** and **privacy history cleanup**, where a backup would recreate the data being
   removed; both require a separate typed confirmation and are labelled irreversible.
3. **Two independent guards.** `PathGuard` and `RegistryGuard` are the only code allowed to approve
   a deletion target. A service must not delete before a guard returns `Allowed`.
4. **Traces beat guesses.** A recorded install trace is exact evidence. Heuristic matches are shown
   with a confidence score and only high-confidence items are preselected.
5. **Other applications are protected.** A leftover candidate owned by another installed
   application (shared vendor folder, shared install root, shared icon executable) is skipped or
   marked protected.
6. **100% offline; zero network calls.** The product makes no network calls, contains no update
   checker, and contains zero telemetry services. It operates entirely locally and offline.
7. **No background service by default.** Nothing runs when the UI is closed unless the optional
   maintenance service was explicitly installed from an elevated Settings page. Its only actions
   are retention/expiry and an optional allow-listed junk cleanup.
8. **No process killing or forced unlocking.** Locked files are reported, not forced.
9. **Ignoring is not deletion.** An ignored leftover is only hidden from later scans of the same
   application; no file or key is touched, and the list can be reset at any time.
10. **Preview before restore.** Restore preview is read-only. The restore rechecks the manifest,
    archive hash and guards before writing.
11. **Portable traces are evidence, not instructions.** Imported bundles are SHA-256 checked and
    still pass every guard before an item can be selected for deletion.
12. **Privacy targets are fixed.** The cleaner can only touch a hard-coded list of Explorer history
    keys, Recent-folder files, thumbnail caches and the app's own state. No program scan can add a
    target, and a reparse point is never followed or deleted.
13. **Shredding is guarded and explicit.** Every shred target passes `PathGuard`; drive roots,
    top-level folders, protected roots, OpenUninstaller's own data and reparse points are refused.
    The CLI requires `--yes`, the app requires typing `SHRED`, and a dry run is available.

## PathGuard rules

A path is refused when any of these is true:

- empty, relative, UNC, device path, contains `..`, or longer than 30,000 characters;
- a drive root or a top-level folder (`C:\Something` is the shallowest allowed target);
- exactly a protected root: system drive, Windows, Program Files, Program Files (x86),
  ProgramData, user profile, AppData, LocalAppData, Public, desktops, Start Menu roots, or `%TEMP%`;
- anywhere under `%WINDIR%`;
- inside OpenUninstaller's own data or application directory;
- a reparse point (junction or symbolic link), or its attributes cannot be verified for a recursive
  delete.

Registry keys are refused when they are not HKLM/HKCU, are too shallow, are the uninstall root, are
inside Microsoft/Windows/SYSTEM protected branches, or are autorun keys. An uninstall registration
entry may only be removed by the explicit forced-uninstall path.

There are three narrow, caller-scoped exceptions, each limited to an exact kind of direct child:

- an `App Paths` or `Classes\Applications` executable key, only after the scanner proved its target
  is missing or inside the application's install location;
- an orphaned service key directly under `SYSTEM\CurrentControlSet\Services`, only after the
  scanner proved its image is missing;
- a single registry value added by a schema 2 trace to a pre-existing key. The containing key is
  never deleted.

The containers themselves (`App Paths`, `Applications`, `Services`) and their descendants remain
protected. These flags are never exposed as a generic user option.

Junk items have a stricter rule: they must be strictly inside the allow-listed category root. The
category root itself is never a valid target.

Scheduled-task XML lives under `%WINDIR%\System32\Tasks`, which `PathGuard` protects. It is never
deleted through `PathGuard`: `ScheduledTaskGuard` validates the task name (no traversal, separators
or shell metacharacters) and the XML path, the XML is copied into the backup archive, and the task
is removed through `schtasks.exe /Delete`. Restore uses `schtasks.exe /Create /XML`.

## Backup and restore

A leftover deletion creates a backup directory under
`%ProgramData%\OpenUninstaller\Backups\<timestamp>-<name>` containing:

- `manifest.json` with every requested item, guard decision, hashes, sizes and results, including
  the individual value names covered by a key export;
- `.reg` exports of registry subtrees, produced with `reg.exe export`;
- a zip archive of copied files, directories and scheduled-task XML;
- a completeness flag and SHA-256 hashes for verification.

Restore imports registry files and extracts the archive. Existing files are skipped unless
"overwrite" is selected. A restore failure is reported per item; it never deletes anything.
**Preview** lists each target as restore, overwrite, skip or blocked without changing anything.
Retention keeps the newest N backups (default 20) and refuses to prune during a restore.

File and folder restores use Windows Transactional NTFS (TxF) to lock the destination directory
metadata and write through verified file handles. Ordinary Windows share modes do not prevent
attribute-only handles from changing reparse metadata. Each file or folder item commits separately;
an error or cancellation rolls back that item's changes. Restore fails closed if TxF is unavailable,
the target is encrypted, the filesystem is unsupported, permissions do not permit the metadata lock,
or another handle conflicts. Existing files with multiple hard links are refused. The preview cannot
predict every such runtime conflict. Microsoft documents [TxF locking](https://learn.microsoft.com/en-us/windows/win32/fileio/txf-basic-concepts)
and its [filesystem limitations](https://learn.microsoft.com/en-us/windows/win32/fileio/programming-considerations-for-transacted-fileio-);
Microsoft also warns that [TxF may not be available in future Windows releases](https://learn.microsoft.com/en-us/windows/win32/fileio/transactional-ntfs-portal).

Portable trace bundles contain a normalized trace document and a manifest with its SHA-256. Import
rejects missing entries, hash mismatches, unknown path tokens, relative paths, UNC/device paths and
traversal. Known roots are expanded against the importing machine; every resulting path still goes
through `PathGuard` if it is later selected for deletion.

Junk cleaning is an exception: caches are disposable and are not backed up. The UI states this and
requires confirmation.

Shredding and privacy history cleanup are the other exceptions. A shred backup would preserve the
very bytes the user asked to destroy, and a history backup would preserve the history. Both paths
are therefore irreversible, use a typed confirmation instead of a checkbox, and never fall back to a
normal delete when the overwrite fails.

## Privacy and shredding details

Privacy categories are fixed in code: RunMRU, TypedPaths, RecentDocs, UserAssist, the per-user Recent
folder (including jump lists), `thumbcache_*.db`/`iconcache_*.db`, and OpenUninstaller's own scan
snapshots, ignored ids and logs. Registry values/subkeys are removed through the registry API; files
are deleted directly (not recycled). Locked thumbnail files are reported and skipped; Explorer is
never killed or restarted. Browser histories, cookies, passwords and account data are not touched.

The shredder writes random bytes over the full file length for every pass, flushes with
`FileOptions.WriteThrough`, truncates to zero, renames to a random name and deletes it. Directories
are walked without following reparse points and removed bottom-up only when empty. A failed
overwrite leaves the file in place and is reported; it is never silently deleted. Even a successful
run cannot defeat SSD wear-levelling, filesystem journals, volume snapshots or pre-existing backups.

## Telemetry and the service

Telemetry is opt-in, off by default and has no vendor endpoint. The app validates an absolute HTTP(S)
URL and rejects plain HTTP except for loopback development. The payload has a schema version, event
name, app version, OS version/build, CPU architecture, a random install id, a timestamp and integer
counters. It never includes paths, file names, program names, user names, machine names or scan
results. The receiving endpoint can see the source IP address; that is inherent to any network call.

The maintenance service is installed only after an elevated, explicit action (`sc.exe create`) and
can be removed from Settings or `oucli service remove --yes`. It runs as LocalSystem, performs one
pass at startup and then follows `maintenance.json`. Its junk cleanup uses `JunkService` with the
same allow-list and recent-file skip as the UI. It never installs a driver, scheduled task or hidden
updater.

## Elevation

The process starts as the invoking user (`asInvoker`). Reading HKLM and some junk roots may fail
without elevation; the app reports what it cannot read and offers a restart-as-administrator
button. The app never requests elevation silently and never runs a hidden elevated helper.

## What can still go wrong

- A vendor uninstaller can be malicious or broken. OpenUninstaller runs it with shell execute and
  cannot make it safe.
- A heuristic match can be wrong if an unrelated product shares a name or folder. Review the reason
  and confidence before selecting it; use "clear selection" if unsure.
- A live application may recreate files while they are being deleted. Those files are reported as
  failures or reappear; this is normal.
- A backup of a very large directory may fail due to disk space. The deletion is refused in that
  case.
- An orphaned service can still have a process running from a deleted image. OpenUninstaller does
  not stop processes; the definition is removed, but the process may continue until it exits or the
  machine restarts.
- Scheduled tasks are recreated from their XML. If the task store is corrupted or the XML depends on
  credentials that changed, restore can fail; the failure is reported item by item.
- Schema 1 traces protect registry key candidates because they cannot distinguish a new key from a
  value added to a shared key. Re-trace to get value-level evidence.
- Registry export does not capture ACLs or every value type in every Windows build. Restore is a
  best-effort recovery tool, not a substitute for a system image.
- A privacy clean removes history that some applications use for convenience (recent files, jump
  lists, launch counts). It is not reversible and the removed convenience data cannot be restored.
- Shredding cannot guarantee physical erasure on SSDs, copy-on-write filesystems, journaled volumes
  or backed-up storage. Treat it as best-effort, not certification.
- The service runs as LocalSystem. Installing it grants the app's maintenance code machine-wide
  file access; only install it from a build you trust and remove it when no longer needed.

## Reporting a safety issue

See [SECURITY.md](../SECURITY.md). Please do not open a public issue for a bypass of a guard.
