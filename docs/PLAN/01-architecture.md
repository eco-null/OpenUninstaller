# Plan 01 — Architecture

Status: **implemented in v0.1**

## Shape

```
OpenUninstaller.sln
├─ src/OpenUninstaller.Core      net8.0-windows  (no UI, no third-party packages)
│   ├─ Models/                   records and enums shared by CLI and UI
│   ├─ Abstractions/             registry, filesystem, process, clock, logging, elevation
│   ├─ Safety/                   path guard, allow-lists, token normalization
│   └─ Services/                 inventory, uninstall, snapshots, traces, leftovers,
│                                backups, autoruns, junk, hunter, reporting
├─ src/OpenUninstaller.Cli       net8.0-windows console host, JSON/table output
├─ src/OpenUninstaller.App       net8.0-windows WPF host, MVVM, dark theme
└─ tests/OpenUninstaller.Tests   xUnit; isolated HKCU + temp directories only
```

Dependencies point inward: `App`/`Cli` → `Core`. Core never references WPF, never calls
`Environment.Exit`, and never prompts. This keeps every destructive operation testable.

## Runtime decisions

- **.NET 8 / WPF.** Ships with Windows desktop tooling, no external UI package, 64-bit.
- **No third-party NuGet packages in production code.** Registry, ZIP, JSON, hashing and process
  control ship with the framework. This reduces supply-chain surface for a tool that is granted
  admin rights.
- **Windows-targeted TFM** (`net8.0-windows`) for access to `Microsoft.Win32.Registry`.
- **Manual composition root** in `App.xaml.cs` and `Program.cs`; constructors take interfaces.
  No DI container, no service locator.

## Ports (abstractions)

| Port | Production | Test double / purpose |
| --- | --- | --- |
| `IRegistryStore` | `WindowsRegistryStore` | HKCU sandbox keys; injected fake for failure paths |
| `IFileSystem` | `PhysicalFileSystem` | temp roots; enumeration caps |
| `IProcessRunner` | `ProcessRunner` | stub uninstaller executables |
| `IClock` | `SystemClock` | deterministic backup/trace names |
| `IOperationLog` | `FileOperationLog` | in-memory capture of every destructive step |
| `IElevationService` | `WindowsElevationService` | reports admin state, relaunches elevated |

`RegistryLocation` = `(RegistryHive Hive, RegistryView View, string Path)`. All registry access goes
through it, including 32/64-bit views. Tests only write under
`HKCU\Software\OpenUninstaller\Tests\...` and always clean up.

## Data flow

### Inventory

`ProgramScanner` enumerates the three uninstall roots × applicable views, reads values, filters
updates/system components, derives a stable id (SHA-256 of hive + path, first 16 hex chars), and
returns `InstalledProgram` records sorted by display name. Icons are resolved lazily in the UI from
`DisplayIcon`/install location.

### Uninstall

```
select program
  → confirm (command is shown)
  → log start
  → (optional) snapshot before
  → run UninstallString or QuietUninstallString
  → wait, capture exit code, refresh inventory
  → scan leftovers (trace if available, else heuristic)
  → review (UI) / auto-select high confidence (CLI only with --yes)
  → BackupService.CreateBackup(selected)
  → DeletionService.Delete(selected)   [registry first, then files]
  → summary + log + backup id
```

### Trace

```
trace-install
  → snapshot registry + file roots
  → run installer, wait
  → snapshot again
  → diff (added keys, added values, added files)
  → TraceStore.Save(program name, diff)
```

A trace is a JSON document under `%ProgramData%\OpenUninstaller\Traces`. Uninstall looks up the
trace by program name, install location and publisher: exact name match wins, then install-location
match. If no trace exists the heuristic scanner runs.

## Error model

Public service methods return result records (`UninstallResult`, `ScanResult`, `CleanResult`,
`RestoreResult`) carrying `Succeeded`, `Warnings`, `Errors`, `Cancelled`, `Duration`. Expected
failures (in use, access denied, not found) are values, not exceptions. Exceptions are reserved for
programmer errors and unexpected I/O. Every destructive loop catches per item and continues,
reporting `Failed` counts — one locked file never aborts a cleanup.

## Concurrency and cancellation

- All long operations are `async` with `CancellationToken`; enumeration and deletion check the
  token every item.
- WPF commands run operations on the thread pool, marshal progress via `IProgress<T>`.
- No parallel deletion: serial ordering is more predictable for logging and backups.

## Bounded work

| Operation | Cap | On overflow |
| --- | --- | --- |
| Registry snapshot | 100,000 keys, depth 12 | stops, marks `Truncated` |
| File index per root | 150,000 entries, depth 8 | stops, marks `Truncated` |
| Heuristic scan | 5,000 candidates per category | stops, warning |
| Junk scan | 250,000 files | stops, warning |
| Uninstall wait | user-configured (default infinite; CLI 0 = no wait) | timeout result |

## Logging

`FileOperationLog` writes JSON lines to `%ProgramData%\OpenUninstaller\Logs\op-<timestamp>.jsonl`.
Each line: timestamp, operation, action, target, outcome, detail. Secrets are never logged; commands
are logged verbatim because they come from the registry and are shown to the user.

## Security posture

- Path guard refuses system roots, drive roots, Windows directory, Program Files root itself,
  user profile root itself, and reparse points.
- Registry guard refuses `HKLM\SOFTWARE` roots, `HKLM\SYSTEM`, `HKLM\SAM`, `HKLM\SECURITY`, the
  uninstall roots themselves, and keys shallower than three components.
- Backups are written before deletion; failed backups abort deletion of that item.
- No network code runs unless the user enables telemetry and supplies an endpoint. All HTTP lives
  in `TelemetryService`; every other component is local-only.
- The optional maintenance service is a separate executable (`OpenUninstaller.Service.exe`) and is
  installed only by an explicit, elevated user action.
- CLI destructive commands require `--yes`; UI destructive commands require a modal confirmation.
