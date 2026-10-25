# Leftover and junk algorithms

This document describes exactly what OpenUninstaller looks for, how it scores a match, and what it
refuses to touch. It is the reference for reviewers and for anyone changing the scanners.

The governing rule is simple:

> A scan only produces **candidates**. Nothing is deleted until the user selects it, reviews the
> evidence, and approves the deletion. Every leftover deletion is backed up first.

## 1. Leftover scan

`LeftoverScanner.ScanAsync` has two evidence modes. A trace, when one exists for the application, is
always used; heuristics are still added because installers do not record every write. Both modes
only read the machine.

### 1.1 Trace evidence (exact)

`TraceInstaller` snapshots the registry and well-known file roots before and after an installer
runs. The persisted trace contains only the difference:

- registry keys created by the installer;
- values added to keys that already existed;
- files and non-empty directories created by the installer;
- truncation flags if a snapshot cap was reached.

A trace item is reported only if it still exists at scan time. It is scored `Exact (100)` and is
preselected **only if the safety guards allow deletion**.

Trace schema 2 stores two separate lists:

```text
RegistryKeys    keys that did not exist before the installer
RegistryValues  (key, value name) pairs added to a key that already existed
```

This distinction is essential. A shared key such as a file-association key or a vendor settings key
may contain values from several applications. Schema 2 reports each added value as its own
`RegistryValue` item and never offers the containing key for recursive deletion.

Trace schema 1 (created by v0.1) merged both kinds into one list. The original evidence cannot be
reconstructed, so schema 1 registry keys are shown **protected** with an explanation; files remain
usable. Re-running `trace-install` creates schema 2.

Modified values (same name, different data) are not treated as evidence. A value that existed before
the installer may have been changed by the installer, but it may also have been changed by another
process. The trace therefore records added values only.

Traces do not store registry value data. Deleting a traced value is backed up by exporting the
containing key; restore imports that export.

### 1.2 Name normalisation

`NameNormalizer` builds the matching vocabulary:

1. Remove version-like text (`v1.2`, `2024.1`, `1.2.3`).
2. Lowercase, split on non-alphanumerics, drop tokens shorter than three characters.
3. Drop generic words: `pro`, `free`, `trial`, `setup`, `installer`, `app`, `software`, `windows`,
   `edition`, `portable`, `suite`, `manager`, publisher suffixes (`inc`, `ltd`, `llc`, `gmbh`), and
   similar.
4. Remove publisher tokens from the product tokens when at least one distinctive token remains.
   `Contoso Widget Pro` from `Contoso Inc.` becomes `widget`.
5. Add tokens from the recorded install folder name (`Widget`).

If no distinctive product token remains, the heuristic scan is skipped with a warning. This avoids
matching an application called only "Pro" or "Setup".

### 1.3 Heuristic registry keys

Scanned roots:

```text
HKLM\SOFTWARE\<Vendor>
HKCU\SOFTWARE\<Vendor>
```

Both 64-bit and 32-bit views are inspected. Vendor branches are skipped for a fixed exclusion list:
`Microsoft`, `Classes`, `Clients`, `Policies`, `RegisteredApplications`, `Wow6432Node`, `Windows`,
`Windows NT`, `Intel`, and OpenUninstaller's own branch.

A vendor branch is opened when its name contains a product token **or** a publisher token. Each
direct product key is scored using the combined `Vendor Product` name, which catches
`HKCU\Software\Google\Chrome` for "Google Chrome" and `HKLM\...\Contoso\Widget` for
"Contoso Widget". At most 250 products per vendor are inspected.

`RegistryGuard` rejects protected keys (Microsoft subtrees, uninstall roots, autorun keys, SYSTEM,
top-level keys) before they become candidates. The scan therefore does not show unremovable
"informational" registry rows.

#### App Paths and Classes\Applications

Registrations that point at a specific executable are scanned separately because they live under
Microsoft-owned branches:

```text
HKLM|HKCU \ SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<exe>
HKLM|HKCU \ SOFTWARE\Classes\Applications\<exe>
```

Both registry views are inspected. For each direct child:

1. Match the executable stem (`widget.exe` → `widget`) against the product tokens.
2. Resolve the target:
   - App Paths: the key's default value is the executable path.
   - Classes\Applications: `shell\open\command`'s default value is the command line, falling back
     to the key's default value.
3. Parse the command line into an absolute executable path. Quoted paths, arguments and
   `%Environment%` variables are handled.
4. Offer the registration only when the executable is **missing**, or when it is inside the
   recorded install location. A registration that still points at a real executable elsewhere is
   left alone.
5. Skip it when another installed application owns the target path.

The guard uses a special, narrow flag: only a direct child of `App Paths` or `Applications` may be
removed. The containers themselves remain protected. This is the only way the cleaner can touch a
`SOFTWARE\Microsoft` branch.

#### Registry values in pre-existing keys

Only a trace produces value-level evidence. The scanner reads the current value and offers it when
it still exists and the containing key passes `RegistryGuard.ValidateValueForDeletion`. A value in a
protected tree (SYSTEM, autorun, Microsoft branches) is shown protected rather than selectable.

Values that existed before install and were only modified are not offered. There is no way to
distinguish an installer change from a later third-party change without a signature database.

#### Orphaned services

Service definitions are read from:

```text
HKLM\SYSTEM\CurrentControlSet\Services\<name>
```

For each service the scanner reads `DisplayName`, `ImagePath`, and `Parameters\ServiceDll` (the
latter for `svchost.exe` services). The command line is parsed into an absolute image path.

A service is offered only when **both** conditions hold:

1. its name or display name matches the product; and
2. its binary (or service DLL) no longer exists.

A service whose binary still exists is not offered, even if its name matches: the service may be
running, and removing a live definition is not a safe scan action. The scan does not start, stop,
pause or kill services. Deleting an orphaned definition removes the registry key (backed up first);
a process that was already running when its image disappeared is not terminated.

### 1.4 File and folder candidates

Roots inspected for direct children:

```text
%ProgramData%
%LOCALAPPDATA%
%APPDATA%
%PUBLIC%
%LOCALAPPDATA%\Programs
%ProgramFiles%
%ProgramFiles(x86)%
```

For each existing direct child directory:

1. Skip name-exclusion folders (`Microsoft`, `Packages`, `Temp`, `Installer`, `WindowsApps`,
   `Package Cache`, `OpenUninstaller`, and the other roots themselves).
2. Skip paths that are the recorded install location or live under it (already handled).
3. Skip any path owned by another installed application (see 1.7).
4. Score the child. If the score is `Medium` or better, it is a candidate.
5. If the child is only a publisher/vendor match (`Low`), do **not** offer the publisher folder
   itself. Instead inspect its direct children and score `Vendor Product` combined. A publisher
   folder is commonly shared by several products, so removing it as a unit would be unsafe.

The recorded install location is also a candidate at `High`, unless another installed application
shares it.

Shortcuts are scanned under the user/all-users Start Menu (depth 3) and both desktops:

- a `.lnk` whose name contains one product token is `Strong`;
- a shortcut whose name contains two or more product tokens is `Strong`;
- shortcuts are matched by name only; `.lnk` targets are not resolved.

### 1.5 Scheduled tasks

Task Scheduler stores each task as an XML file under:

```text
%WINDIR%\System32\Tasks\<task path>
```

The scan walks at most three levels, reads at most 5,000 files, and reads at most 1 MB per task. It
extracts `<Command>` and `<Arguments>` from the XML.

A task is offered when it has a command and either:

- the command executable is inside the recorded install location; or
- the task name matches the product and the command executable is missing.

Tasks under `Microsoft\Windows` are never inferred from a name match; they are only considered when
their command is inside the application's install location. A task with no executable command (for
example a COM handler) is skipped because there is no executable evidence to review.

Task name matching is conservative: the task path and file name are scored with the same token
rules as folders. A matching name plus a missing executable is `Strong`; an executable inside the
install location is `High`. Deletion is performed through `schtasks.exe /Delete` and the original XML
is copied into the backup before deletion. Restore recreates the task from that XML with
`schtasks.exe /Create /XML`.

### 1.6 Scoring

| Confidence | Score | Condition |
| --- | ---: | --- |
| Exact | 100 | Created by the traced installer and still present; traced value |
| High | 90 | Recorded install location; compact name equality (`ContosoWidget` = `Contoso Widget`); App Paths target inside the install location; scheduled task with executable inside the install location |
| Strong | 70 | Two or more product tokens; one token plus the publisher; matching shortcut; App Paths/service/task with a missing target; orphaned service |
| Medium | 40 | One distinctive product token |
| Low | 20 | Publisher-only match; used to descend into a vendor folder, never offered as a folder candidate |

Only `High` and `Exact` items are preselected. `Strong` and below require an explicit click. Scores
are evidence, not a probability.

### 1.7 Ownership by another application

Heuristic and trace scans accept an optional inventory of all installed programs. Before a path is
offered, it is checked against every *other* program:

- is the path inside that program's `InstallLocation`;
- does the path contain that program's `InstallLocation` (a parent folder that also hosts it);
- is the candidate the other program's `DisplayIcon` executable or a folder containing it.

Heuristic matches are skipped entirely; trace matches are shown as protected. This prevents a
vendor folder shared by two products from being offered for deletion when one product is being
uninstalled. App Paths, service and scheduled-task targets are checked with the same rule.

### 1.8 Consolidation

1. De-duplicate by `kind + display path`, keeping the highest confidence and trace evidence.
2. If a directory candidate is selected, drop file/directory candidates beneath it.
3. Emit registry keys, values and services first, then scheduled tasks, then files sorted by path
   length so parents precede children.

Sizes are calculated recursively for directories with a 50,000-file cap. An inaccessible file
contributes zero rather than failing the scan. Startup, desktop and task scans are capped at 5,000
entries; vendor folders at 4,000 direct children and 500 nested children; App Paths at 500 children
per root; services at 5,000 definitions.

### 1.9 Deletion pipeline

```text
for each selected item:
    guard                           → refuse with a reason if unsafe
    backup (reg export / zip XML)   → if backup fails, do not delete
    delete
    verify absence
    log JSONL
```

Backup behaviour by kind:

| Kind | Backup | Delete | Verify |
| --- | --- | --- | --- |
| Registry key | `reg.exe export` of the key subtree | `DeleteSubKeyTree` | key absent |
| Registry value | `reg.exe export` of the containing key; value names recorded in the manifest | `DeleteValue` | value absent, sibling values untouched |
| Service | `reg.exe export` of `Services\<name>` | `DeleteSubKeyTree` | key absent |
| Scheduled task | task XML copied into `files.zip` with its Task Scheduler name | `schtasks /Delete` | XML absent |
| File / directory | copied into `files.zip` with SHA-256 | file delete / recursive directory delete | path absent |

Values from the same key are grouped into one export so a backup is never partial. If a value
disappears between scan and backup, the whole group is skipped rather than deleting an unreviewed
sibling. A failed backup is a skip, not a warning; the review UI never has a "delete without backup"
switch. Restore imports registry exports and extracts files; scheduled tasks are recreated from their
XML.

## 2. Junk scan and clean

Junk cleaning is intentionally different from leftover cleaning: caches are disposable and are not
backed up. The user must still select categories and confirm before deletion. Files modified in the
last ten minutes are skipped so an active application's temporary data is not pulled out from under
it. Reparse points are never followed or deleted.

### 2.1 Categories

| Id | Roots | Clean? | Notes |
| --- | --- | --- | --- |
| `user-temp` | `%TEMP%`, `%LOCALAPPDATA%\Temp` | yes | direct children, >10 min old |
| `windows-temp` | `%WINDIR%\Temp` | yes | needs elevation for some items |
| `browser-cache` | every existing Chrome/Edge/Brave/Vivaldi/Chromium/Opera/Firefox profile | yes | warns when a browser process is running; locked files are reported |
| `thumbnails` | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` | yes | `thumbcache_*.db`, `iconcache_*.db` only |
| `crash-reports` | `%LOCALAPPDATA%\CrashDumps`, user and machine WER queues | yes | WER files may be locked while reporting |
| `system-error-reports` | `%WINDIR%\Minidump`, `%WINDIR%\LiveKernelReports` | yes | kernel dumps; elevation |
| `shader-caches` | D3DSCache, NVIDIA DX/GL/Compute, AMD Dx/Dxc/GL, Intel ShaderCache | yes | rebuilt on demand |
| `internet-cache` | `%LOCALAPPDATA%\Microsoft\Windows\INetCache` | yes | WinINet/WinHTTP cache |
| `delivery-optimization` | `%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache` | yes | Windows Update peer cache; elevation; may be locked |
| `developer-caches` | npm, pip, NuGet v3/plugins and Yarn download caches | yes | rebuilt on demand; does not touch project sources or the compiled package folders used by NuGet restore |
| `windows-logs` | `%WINDIR%\Logs\CBS`, `DISM`, `WindowsUpdate` | **no** | report-only; needed for servicing diagnostics |
| `prefetch` | `%WINDIR%\Prefetch` | **no** | report-only; removing it temporarily slows startup |
| `windows-update` | `%WINDIR%\SoftwareDistribution\Download` | **no** | report-only; deleting can break pending updates |
| `old-windows` | `%SystemDrive%\Windows.old` | **no** | report-only; use Windows Storage settings |
| `recycle-bin` | Shell API | yes | permanent; confirmed separately |

Chromium profiles are discovered by name (`Default`, `Guest Profile`, `System Profile`,
`Profile *`) under every installed Chromium-family browser. Cache subdirectories include `Cache`,
`Code Cache`, `GPUCache`, `Service Worker\CacheStorage`, `DawnGraphiteCache`,
`DawnWebGPUCache`, `Media Cache`, `GrShaderCache`, and `ShaderCache`. Firefox profiles are read
from both roaming and local profile directories (`cache2`, `startupCache`, `shader-cache`). Opera
and Opera GX profile folders are handled separately because they do not use a `User Data` folder.

Category discovery is bounded at 100 profiles per browser family, and each finished category is
reported to the UI as soon as it has been measured so a slow cache does not leave the page blank.

### 2.2 Allow-list enforcement

`JunkService` only enumerates children of the roots above. Every item is passed through
`PathGuard.ValidateJunkItem(path, root)`, which requires:

- both paths fully qualified;
- no `..`, UNC, or device path;
- the item is strictly inside the category root (the root itself is never deleted);
- the item is not inside OpenUninstaller's own data or application directory;
- the item is not a reparse point.

A rejected item is counted as skipped and never deleted. Enumeration ignores inaccessible entries
and limits each root to 50,000 entries. Directory sizes stop at 100,000 files.

### 2.3 Cleaning

- `Clean` re-enumerates the selected category at deletion time; it does not trust a stale scan list.
- Directories are deleted recursively, files individually.
- An item modified within ten minutes is skipped. For a directory this means its own last-write
  time; a recently touched directory is treated as active even if some children are older.
- In-use or access-denied items are recorded as failures; the cleaner does not kill processes,
  take ownership, or retry with elevated rights silently.
- A dry run reports the bytes and item count without touching anything.
- Report-only categories never delete; they return a message explaining why.
- Recycle bin is emptied through the Shell API, not by deleting `$Recycle.Bin` directly.
- A category root is never deleted, even when every child is removed.

## 3. Known limits

These are deliberate scope boundaries, not hidden behaviour:

- Other user profiles are not scanned. The application is per-user unless elevated.
- Microsoft Store/MSIX package data is not scanned.
- Trace snapshots cover `HKLM\SOFTWARE` and `HKCU\SOFTWARE` plus the documented file roots; writes
  outside those roots are not traced. Modified registry values are not evidence.
- Schema 1 traces keep registry keys protected because their key/value evidence is ambiguous.
- Heuristic matching is name/location based. A renamed product folder cannot be recognised.
- Services are only offered when their image is missing. The cleaner does not stop or query the
  Service Control Manager; a service definition is removed as a registry key after backup.
- Scheduled tasks are only inferred when a command executable exists in the XML. Tasks with COM
  handlers and tasks outside the XML store are not covered.
- File-association (`Classes`) keys other than `Classes\Applications` are not scanned; COM
  registration and shell extensions need a separate dependency model.
- Environment `PATH` entries, firewall rules and drivers are not removed.
- Junk cleaning does not back up caches; restore is not available for junk.
- Sizes may be lower than Explorer's if files are locked or a size cap is reached.
- `windows-temp`, `delivery-optimization`, `windows-logs` and `prefetch` can require elevation;
  unelevated scans report what they can read and skip the rest.
- Ignored items are keyed by application id and display name; a renamed application starts with a
  fresh ignore list.
- Scan history keeps one snapshot per application display name. A different application with the
  same name shares that history.
- Portable traces tokenize known Windows roots. Paths outside those roots stay absolute and are
  still validated by the path guard on the importing machine.
- Restore preview is a snapshot of intent; the restore operation rechecks the manifest, hashes and
  guards before changing anything.

## 4. Verification

The scanner and tool behaviour is covered by `OpenUninstaller.Tests` (99 tests):

- trace round-trip and exact items, including protected paths;
- schema 2 value-level evidence, value deletion with sibling preservation, and schema 1 protection;
- nested vendor\product detection and publisher-only folder refusal;
- App Paths and Classes\Applications detection, missing-target handling and scoped deletion;
- orphaned service detection and service-key guard scope;
- scheduled-task detection, name/path guard and deletion orchestration with a fake `schtasks`;
- reversible autorun disable/enable for registry values, services and scheduled tasks, including
  a task disabled outside the app and a service whose original start mode is unknown;
- ownership conflicts with another installed application (heuristic skip and trace protection);
- name normalisation and confidence scoring;
- every junk category root including browser profiles, crash/WER, shader caches, delivery
  optimization, developer caches, report-only Windows.old, servicing logs and prefetch;
- allow-list rejection of the category root, outside paths, reparse points, and OpenUninstaller's
  own directories;
- backup/restore round-trip for leftover deletion;
- ignored-leftover persistence, per-application matching and corrupt-state handling;
- scan-to-scan diff classification (new, resolved, unchanged);
- portable trace bundle round-trip, path-token expansion and tamper rejection;
- restore preview actions for registry keys and folders.

Run `dotnet test OpenUninstaller.sln -c Release` after changing any scanner, guard, or scoring rule.
