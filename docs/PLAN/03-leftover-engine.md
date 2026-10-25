# Plan 03 — Leftover engine

Status: **implemented in v0.2; ignored state and scan history added in v0.4** · Reference: [ALGORITHMS.md](../ALGORITHMS.md)

## Two evidence sources

| Mode | Evidence | Selection default |
| --- | --- | --- |
| Trace-based | Exactly what the traced installer created and what still exists | Exact items preselected, review required |
| Heuristic | Names and locations that resemble the application | Only High (90) items preselected |

A trace is used automatically when one exists for the application. Heuristics still run because
installers do not record every write. Neither mode deletes anything.

## Registry snapshot

`RegistrySnapshotter` walks `HKLM\SOFTWARE` and `HKCU\SOFTWARE` in the matching view, depth-first:

- key path;
- value names and value-data hashes (to detect modification);
- depth limit 12, 100,000 keys, deterministic ordering.

Snapshots are not persisted wholesale. The trace stores only the diff:

```text
added keys       afterKeys - beforeKeys
added values     afterValues(path) - beforeValues(path)
```

Schema 2 keeps the two added-value cases separate:

```text
RegistryKeys    keys created by the installer
RegistryValues  values added to keys that existed before the installer
```

A created key is deleted as a subtree. A value added to a pre-existing key is an individual
`RegistryValue` item; the containing key is never offered. Values are backed up by exporting the
containing key, and restore imports the export. Schema 1 traces are kept but their key items are
protected because the original evidence cannot be reconstructed.

Modified values (same name, different data) are deliberately not evidence.

## File snapshot

Roots indexed for tracing:

```text
%ProgramFiles%, %ProgramFiles(x86)%, %ProgramData%,
%LOCALAPPDATA%, %APPDATA%,
%USERPROFILE%\Desktop, %PUBLIC%\Desktop,
%ProgramData%\Microsoft\Windows\Start Menu\Programs,
%APPDATA%\Microsoft\Windows\Start Menu\Programs
```

Index = path, size, last write. Depth 8, 150,000 entries per root; reparse points are skipped.
The diff keeps added files and added non-empty directories, excluding temp locations under `%TEMP%`
and OpenUninstaller's own directories. Writes outside the indexed roots are a documented limit.

## Heuristic scanner

Inputs: `Name`, `Publisher`, `InstallLocation`, plus the current inventory of other installed
programs for ownership protection.

1. **Normalise** the name: remove version text, lowercase, split, drop short and generic words, and
   strip publisher tokens when a distinctive product token remains.
2. **Registry keys**
   - `HKLM\SOFTWARE\<Vendor>` and `HKCU\SOFTWARE\<Vendor>`, both views;
   - only vendors matching a product/publisher token are opened;
   - product keys are scored on the combined `Vendor Product` name;
   - `RegistryGuard` rejects protected Microsoft/SYSTEM/uninstall/autorun branches.
3. **App Paths / Classes\Applications**
   - direct children of both containers, both hives and views;
   - match the executable stem, resolve the default path or `shell\open\command`;
   - offer only when the executable is missing or under the recorded install location;
   - a scoped guard flag allows the direct child, never the container.
4. **Orphaned services**
   - direct children of `HKLM\SYSTEM\CurrentControlSet\Services`;
   - match name/display name and resolve `ImagePath` or `Parameters\ServiceDll`;
   - offer only when the binary is missing; never stop, start or query a live service.
5. **File candidates**
   - the recorded install location;
   - direct children of `%ProgramData%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%PUBLIC%`,
     `%LOCALAPPDATA%\Programs`, `%ProgramFiles%`, `%ProgramFiles(x86)%`;
   - a publisher-only folder is opened one level further, never offered as a whole;
   - Start Menu shortcuts (depth 3) and desktop shortcuts matched by name.
6. **Scheduled tasks**
   - XML files under `%WINDIR%\System32\Tasks`, depth 3;
   - parse `<Command>`/`<Arguments>`; offer when the executable is under the install location, or
     when the name matches and the executable is missing;
   - `Microsoft\Windows` tasks require install-location evidence, not a name match;
   - deletion uses `schtasks /Delete`; the XML is backed up and restore uses `schtasks /Create`.
7. **Ownership protection** — a path owned by another installed application (inside its install
   location, containing it, or its display icon) is skipped for heuristics and marked protected for
   traces.
8. **Scoring**

| Score | Condition | Preselected |
| --- | --- | --- |
| 100 | Traced and still present (key, value or file) | yes |
| 90 | Install location, compact name equality, App Paths target or scheduled-task executable inside install location | yes |
| 70 | Two name tokens, one token + publisher, matching shortcut, missing App Paths/service/task target | no |
| 40 | One distinctive name token | no |
| 20 | Publisher-only; used to descend into a vendor folder | no |

Candidates are de-duplicated, children of a selected directory are dropped, and protected items are
never selectable. Every candidate carries `Reason` and `Confidence`.

## Deletion pipeline

```text
for each selected item:
    guard.Validate(...)            → skip with reason if unsafe
    backup.ExportOrCopy(...)       → on failure, skip (never delete unbacked)
    log("delete", target)
    delete (registry value, key, task or file tree)
    verify absence
    record result
```

Registry items are exported with `reg.exe export <key> <file> /y`; values from the same key share
one export. Files/directories are copied into the backup archive; task XML files are copied with
their Task Scheduler name. In-use files are reported as failures. Scheduled tasks are removed with
`schtasks /Delete`; services are removed as registry keys after their missing binary was verified.

## Ignored state and scan history

Two small JSON stores live under `%ProgramData%\OpenUninstaller`:

- `ignored-leftovers.json` maps an application id/name to leftover item ids that the user chose to
  hide. The scanner removes those ids after consolidation, reports the hidden count, and the review
  window can clear the list and rescan. Ignoring never deletes anything.
- `ScanHistory\<slug>.json` keeps the most recent scan per application display name. The next scan
  is classified into new, resolved and persistent items by item id. The desktop workflow records
  history automatically; the CLI records it only when `--diff` is passed.

Both stores are written atomically (temp file + move) and treat unreadable content as empty so a
corrupt state file cannot block a scan.

## Caps and performance

- Registry scan only enumerates children of a fixed set of parents; no full-hive scan.
- File heuristics inspect direct children and one nested level for vendor folders.
- Caps: 4,000 children per root, 500 grandchildren per vendor, 250 products per vendor, 500 App
  Paths children, 5,000 shortcuts, 5,000 services, 5,000 task files (depth 3, 1 MB each), 50,000
  files for a size calculation.
- Scans report duration, candidates and truncation.

## Testing strategy

- Synthetic HKCU tree under `HKCU\Software\OUTests\<id>`.
- Synthetic file tree in a temp directory with injected `AppPaths` roots.
- Snapshot diff tests: added key, added value, modified value, removed key.
- Trace round-trip and exact-match leftovers.
- Schema 2 value items, value deletion, schema 1 protection.
- App Paths / Classes\Applications detection and scoped deletion.
- Orphaned service detection and guard scope.
- Scheduled task detection, guard and deletion orchestration.
- Heuristic tests: nested vendor\product, publisher-only folder refusal, ownership conflict,
  protected path rejection, name normalisation.
- Guard tests: attempts to delete `C:\Windows`, `C:\`, the user profile root, `HKLM\SOFTWARE`.

## Deliberately out of scope

- Other user profiles and MSIX/Store application data.
- File associations outside `Classes\Applications`, COM registration, shell extensions and drivers.
- Starting/stopping services or querying the Service Control Manager.
- Scheduled tasks without an executable command or outside the XML task store.
- Environment `PATH` entries and firewall rules.
