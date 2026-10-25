# Plan 07 — Testing and release

Status: **implemented in v0.5**

## Test strategy

Tests are isolated from the real machine state:

- registry fixtures live under `HKCU\Software\OpenUninstaller\Tests\<guid>` and are deleted in
  `Dispose`;
- file fixtures live in `Path.Combine(Path.GetTempPath(), "ou-tests", <guid>)`;
- process fixtures are tiny generated `.cmd` scripts for inventory tests; the uninstall runner is
  tested with the real `cmd.exe` and explicit arguments;
- `AppPaths` is injectable so the scanner/cleaner can point at fake roots;
- no test deletes anything outside its fixture.

### Coverage matrix

| Area | Tests |
| --- | --- |
| Inventory | parse values, skip system components/updates, stable ids, 64/32 views, missing DisplayName |
| Command resolver | quoted paths, msi `/I` → `/x`, quiet preference, rundll32, invalid input |
| Uninstall runner | exit code capture, timeout, not-found, still-registered detection |
| Registry snapshot | added/removed keys, added/modified values, depth cap, cancellation |
| File snapshot | added files, ignored reparse points, caps |
| Trace store | save/load round-trip, name match, corrupt file handling |
| Trace bundles | export/import round-trip, path-token expansion, tamper rejection |
| Ignored leftovers | persistence, per-application matching, corrupt state |
| Scan history | new/resolved/unchanged classification |
| Leftovers | trace-exact matches, schema 2 values, schema 1 protection, App Paths, services, scheduled tasks, heuristic scoring, protected items, de-duplication |
| Backup | registry export file created, value-name manifest, hashes, archive content, retention pruning |
| Restore | file restore, overwrite guard, registry import (HKCU fixture), task XML validation, preview actions |
| Guards | every rule rejects, normalization `..`, UNC, reparse |
| Autoruns | list, disable→hidden+enabled=false, enable→restored, conflict |
| Junk | allow-list, recent-file skip, dry run, locked file reporting, Delivery Optimization, developer caches, report-only logs/prefetch |
| Hunter resolver | path match, token match, no match |
| Privacy cleaner | allow-list, dry run, registry categories (HKCU fixture), file categories, app-state category, unknown id handling |
| Shredder | plan/guard rejection, dry run, overwrite+delete, folder removal, reparse refusal, pass clamp |
| Telemetry | off by default, endpoint validation (HTTPS, loopback HTTP), payload excludes paths/names, disabled send is a no-op |
| Maintenance | backup prune, log/scan-history expiry, disabled run, junk step reporting |
| CLI | argument parsing, confirmation exit code, JSON shape, unknown target, privacy/shred/telemetry/maintenance/service usage |

### Commands

```powershell
dotnet test OpenUninstaller.sln -c Release
dotnet run --project src/OpenUninstaller.Cli -- doctor
dotnet run --project src/OpenUninstaller.Cli -- list --json
dotnet run --project src/OpenUninstaller.Cli -- scan-leftovers "Example App" --diff
dotnet run --project src/OpenUninstaller.Cli -- backups preview <id> --json
dotnet run --project src/OpenUninstaller.Cli -- privacy scan --json
dotnet run --project src/OpenUninstaller.Cli -- shred <temp-file> --dry-run --json
dotnet run --project src/OpenUninstaller.Cli -- telemetry status --json
dotnet run --project src/OpenUninstaller.Cli -- maintenance status --json
dotnet run --project src/OpenUninstaller.Cli -- service status --json
```

## Static quality gates

- `TreatWarningsAsErrors` for Core/Cli (WPF XAML generated code excluded where necessary).
- Nullable enabled, no `#pragma warning disable` without a comment.
- No `catch { }`; expected failures become result values.
- `dotnet format --verify-no-changes` is a useful local check; CI enforces the build with
  warnings-as-errors, which covers compiler and nullable diagnostics.
- Dependency policy: production projects must have zero `PackageReference` entries.

## CI (`.github/workflows/ci.yml`)

- `windows-latest`, .NET 8 SDK.
- restore → build Release with warnings-as-errors → test → CLI smoke → self-contained publish →
  upload artifact.
- Tests use isolated HKCU/file fixtures and never require elevation, so the full suite runs on a
  standard GitHub runner.

## Release

1. Version in `Directory.Build.props`.
2. Update `CHANGELOG.md` (Keep a Changelog) and `docs/BACKLOG.md` progress log.
3. Run the gates locally:
   ```powershell
   dotnet build OpenUninstaller.sln -c Release -warnaserror
   dotnet test OpenUninstaller.sln -c Release
   pwsh scripts/package-release.ps1 -Configuration Release -Runtime win-x64
   ```
4. `package-release.ps1` produces `artifacts/release/OpenUninstaller-<version>-win-x64.zip` and
   `.sha256`; it does not include traces, backups or logs.
5. Commit, tag `vX.Y.Z`, and push the tag. `.github/workflows/release.yml` repeats the build and
   tests, packages the zip, writes the checksum and creates the GitHub release.
6. Inspect the published assemblies (identity, architecture, P/Invoke boundaries and resources)
   with a managed PE inspector before announcing the release.

## Manual smoke test

1. Launch UI; verify inventory count > 0 and icons render.
2. Search and sort; export CSV.
3. Trace a harmless installer (or a synthetic `.cmd` installer created in a temp folder).
4. Uninstall an application installed only for the current user; confirm leftovers appear.
5. Accept a subset; verify backup manifest, restore preview and restore.
6. Ignore a leftover, rescan and verify it stays hidden; reset the ignore list.
7. Open a scan with `--diff`/previous history and verify new/resolved/unchanged counts.
8. Disable and re-enable a Run entry; verify with Task Manager.
9. Scan junk; dry run; clean one category.
10. Launch hunter mode, hover over Notepad, verify match and that no process is terminated.
11. Elevate and verify badge; run CLI `doctor`.
12. Queue two applications, verify each opens its own review window, then clear the queue.
13. Privacy page: scan, dry run, then clean one history category with the typed `CLEAN` gate.
14. Shred a synthetic temp folder with dry run, then with the typed `SHRED` gate; verify files
    are gone and a protected path is refused.
15. Settings: telemetry stays off until enabled with a valid endpoint; a loopback endpoint proves
    the request path; the payload preview contains no paths or program names.
16. Install the maintenance service on an elevated test machine, run a maintenance pass, then
    remove it; verify no service is left behind.

## Known verification gaps for v0.5

- Real MSI uninstall is exercised only manually; CI tests use stub processes.
- Real service/task deletion is covered by a fake `schtasks` runner and isolated fixtures, not by
  mutating the build machine's Task Scheduler or service database.
- Very large machines (>150k files in traced roots) truncate the trace by design.
- Code signing is not part of the build.
- No automated UI tests (no WinAppDriver dependency); UI is verified by a manual smoke test and a
  startup smoke test that checks the process stays alive.
- Autorun service/task toggles are covered by fixtures and a fake `schtasks` runner; the release
  verification did not mutate the build machine's service database or Task Scheduler.
- Scheduled-task listing needs administrator rights on a normal Windows installation. An
  unelevated scan reports a warning instead of silently showing an empty list.
- The v0.4 scan verification was read-only: `doctor`, `list`, `junk scan`, `autoruns list`,
  `scan-leftovers --diff`, trace bundle export/import of a synthetic trace, and restore preview
  logic in tests. No installed application was uninstalled and no autorun was toggled.
- The batch queue is verified by launching the UI with a synthetic queue only in source-level
  review; it opens the existing reviewed uninstall window for each entry rather than deleting in
  the background.
- Shredding is best effort and is not verified against forensic recovery; SSD wear-levelling and
  filesystem journals can retain copies, which the UI and CLI state explicitly.
- Privacy history cleanup is tested against HKCU and temp-file fixtures. Real Explorer history and
  thumbnail caches were not deleted during release verification.
- Telemetry tests use a local loopback HTTP listener. No real endpoint is configured and no data
  is sent anywhere during CI or release verification.
- The maintenance service is built, published and tested through `MaintenanceService` and
  `BackgroundServiceManager` query formatting. Installing a real service and running as LocalSystem
  remain a manual elevated smoke test.
- MSIX/Store inventory, drivers, COM registration outside `Classes\Applications`, file associations
  beyond that key, PATH/firewall cleanup, and services whose binary still exists remain out of
  scope and are documented in [BACKLOG.md](../BACKLOG.md).
