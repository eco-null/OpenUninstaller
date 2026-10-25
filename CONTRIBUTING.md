# Contributing

Thanks for helping. OpenUninstaller is deliberately small, dependency-light and conservative about
destructive operations.

## Development setup

- Windows 10 1809+ or Windows 11 x64
- .NET 8 SDK
- No third-party runtime packages are allowed in `src/`. The test project may use xUnit.

```powershell
dotnet restore OpenUninstaller.sln
dotnet build OpenUninstaller.sln -c Release
dotnet test OpenUninstaller.sln -c Release
```

Run the desktop app with `dotnet run --project src/OpenUninstaller.App` and the CLI with
`dotnet run --project src/OpenUninstaller.Cli -- help`.

## Architecture

```
Core   registry, filesystem, snapshots, scanning, guards, backup, restore
CLI    argument parsing, JSON output, confirmation gates
App    WPF views, view-models, composition root
Tests  xUnit tests with an isolated temp tree and HKCU test subtree
```

Rules that are not negotiable:

1. A destructive operation must pass `PathGuard` or `RegistryGuard` first.
2. Leftover deletions must be backed up before deletion. If the backup fails, do not delete.
3. No network calls, telemetry, auto-update, or tracking. The application is 100% offline
   with zero telemetry and zero external network calls. The background maintenance service ships as
   an optional, explicitly installed executable. Any network or external phoning code is strictly
   prohibited.
4. Do not add a third-party dependency to `Core`, `Cli` or `App` without a strong reason and a
   licence review.
5. Do not bypass UAC or run hidden elevated helpers.
6. Do not target, modify or depend on another uninstaller's installation or data.
7. A scan only proposes candidates. Never add a "clean everything" path that skips review.

## Tests

Every scanner, guard or deletion change needs a test. Use `TestEnvironment`, which creates:

- a private temp tree for fake `ProgramData`, `LocalAppData`, `AppData`, Program Files, Windows and
  desktop/Start Menu roots;
- a private `HKCU\Software\OUTests\<id>` subtree for registry fixtures.

Never write tests that touch real uninstall keys, real Startup folders, real user data or a live
application. Junk tests must only create files under the test tree.

Run a focused test while iterating:

```powershell
dotnet test tests/OpenUninstaller.Tests --filter FullyQualifiedName~Leftover
```

Then run the whole suite in Release before opening a pull request.

## Style

- Nullable enabled, warnings are errors in production projects.
- File-scoped namespaces, four-space indentation, `var` only when the type is obvious.
- Comments explain *why* (safety, Windows quirks), not *what*.
- Keep UI text plain and explicit; never imply that a heuristic is certain.
- Do not add AI attribution or generated-code markers to source files.

## Pull requests

Include:

- what changed and why;
- safety impact (which guard/path/registry rule is involved);
- tests run and their result;
- screenshots for UI changes;
- known limitations if the change is partial.

Small, focused PRs are easier to review than large refactors. For a new scanner or a deletion
path, open an issue first so the threat model can be discussed.

## Commit messages

Use the imperative mood, for example `Add nested vendor folder detection to leftover scan`. Keep
generated build output and secrets out of commits. No AI attribution trailers are required or
wanted in this repository.
