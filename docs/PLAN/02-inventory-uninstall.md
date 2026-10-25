# Plan 02 — Program inventory and uninstall engine

Status: **implemented in v0.1**

## Inventory sources

| Hive | View | Path | Covers |
| --- | --- | --- | --- |
| HKLM | Registry64 | `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` | machine-wide 64-bit |
| HKLM | Registry32 | `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` | machine-wide 32-bit |
| HKCU | Registry64 | `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` | per-user |
| HKCU | Registry32 | `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` | per-user 32-bit |

Each subkey is a candidate. `DisplayName` is required. Skipped:

- `SystemComponent = 1` (runtime components that Programs and Features hides);
- entries with `ParentKeyName` (patches/updates of a parent product);
- entries whose `ReleaseType` is `Update`, `Hotfix`, `Security Update` or `Update Rollup`;
- entries with `DisplayName` starting with `KB` and matching an update pattern.

Everything else is shown. A separate "system components" toggle (UI) can reveal skipped entries,
marked read-only in v0.1.

## Record

`InstalledProgram`:

```
Id                 stable hash of hive|view|key path
Name               DisplayName
Version            DisplayVersion
Publisher          Publisher
InstallLocation    InstallLocation (expanded)
UninstallCommand   UninstallString (expanded)
QuietUninstallCommand QuietUninstallString (expanded)
DisplayIcon        path[,index]
EstimatedSizeBytes EstimatedSize (KB → bytes)
InstallDate        parsed from yyyymmdd, or null
RegistryPath       hive/view-qualified key path
Architecture       x64 | x86 | Unknown
Hive               HKLM | HKCU
IsWindowsInstaller WindowsInstaller = 1
IsSystemComponent  SystemComponent = 1
UrlInfoAbout       help link
```

`DisplayIcon` may point to an `.ico`, `.exe`, `.dll` or contain `,index`. Resolution is lazy:
try the icon file, then the executable, then the uninstaller executable, then a generic glyph. The
library does not load icons.

## Uninstall command engine

`UninstallCommandResolver` parses strings such as:

```
"C:\Program Files\App\uninst.exe" /S
MsiExec.exe /I{90160000-008C-0000-1000-0000000FF1CE}
msiexec /x {GUID} /qn
C:\Windows\System32\rundll32.exe "C:\Program Files\App\setup.dll",Remove
```

Rules:

1. Expand environment variables.
2. Tokenize on quotes and whitespace without a shell; never concatenate untrusted strings into
   `cmd.exe`.
3. If the executable is `msiexec` and the argument is `/I{ProductCode}`, rewrite to
   `/x {ProductCode}`. In silent mode add `/qn /norestart`.
4. In silent mode prefer `QuietUninstallString`; otherwise use the uninstall command unchanged for
   interactive mode, and for MSI add quiet flags.
5. If `WindowsInstaller = 1` and no product code can be found, try the uninstall registry key name
   when it looks like a GUID.
6. Show the final command line in the confirmation UI and log it.

The engine executes the process with `UseShellExecute = true` (so UAC and file associations work)
and optionally waits. Exit codes are recorded (`0`, `1605`, `1641`, `3010` are treated as success or
reboot-required). Output capture is unavailable with shell execution; the UI relies on exit codes
and post-uninstall inventory checks. `msiexec` runs are also awaited.

## Post-uninstall verification

After the process exits, the scanner re-reads the registry entry:

- gone → success;
- present with the same id → `StillRegistered = true` → offer forced cleanup;
- replaced with a different entry → informational warning.

## Forced uninstall

Used when the uninstaller is missing or the entry is broken. Steps:

1. Confirm; warn that files are not removed by the vendor.
2. Back up and delete the uninstall registry key (guard-checked; this is the only case where an
   uninstall root child is removed, and it requires an explicit `allowUninstallEntry` flag).
3. Run the leftover scan in heuristic mode with the entry's name, publisher and install location.
4. Review and delete as usual.

## CLI contract

`uninstall "<name-or-id>" [--silent] [--yes] [--no-scan] [--force] [--timeout seconds] [--json]`

- Without `--yes` the command prints the resolved command line, the selected registry entry and the
  planned leftovers, then exits with code 2 ("confirmation required"). This makes scripts safe.
- `--force` skips running the vendor uninstaller and performs forced uninstall.
- `--no-scan` is allowed only with `--force`; otherwise leftovers are always reviewed.
- An ambiguous name lists candidates and exits with code 3.
