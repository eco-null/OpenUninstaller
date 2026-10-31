# Application result sorting — 31 October 2026

Scope: Programs/uninstall inventory, Autoruns Basic/Advanced, autorun history, Store apps.

- Explicit sort pickers and native header clicks share one sort state per list.
- Programs: name, publisher, numeric size, raw install date, version and architecture.
- Autoruns: name, publisher, enabled state, source and command. History: timestamp, name,
  action, source and details; newest timestamp first by default.
- Store: display name, publisher, kind, architecture and version.
- Both directions are available. Missing program sizes/dates stay last. Name breaks ties.
- Reordering uses the existing collection view, retaining row identity, selection and queue flags.
  Search/filter/refresh retain the chosen sort during the current page's lifetime.
- No persisted settings, inventory discovery or system mutation behavior was added.

Verification commands (Windows):

```powershell
dotnet build OpenUninstaller.sln -c Release --artifacts-path artifacts/sorting-final -warnaserror
& artifacts/sorting-final/bin/OpenUninstaller.UiSmoke/release/OpenUninstaller.UiSmoke.exe --verify-sorting
dotnet test tests/OpenUninstaller.Tests -c Release --artifacts-path artifacts/sorting-final --no-build
```

`--verify-sorting` exercises real WPF collection views and view models using in-memory fixtures:
mixed MB/GB values, unknown values, dates, name tie breaks, header/dropdown agreement, selected
rows, queue flags, filtered and empty lists, collection repopulation, Basic/Advanced membership,
history timestamps with different UTC offsets and independent mode sort choices. It invokes no
scan/uninstall/autorun mutation commands. The UI Automation smoke also exercises each picker,
including history, across the existing theme/window matrix.

Local screenshot and build evidence belongs under `artifacts/sorting-final/` (ignored by Git).

Results: final Release solution build passed with zero warnings/errors; all in-memory sorting
checks and the full UI Automation matrix passed on the final build. The existing core/CLI suite
passed 227/227 in `artifacts/sorting-validation/`; subsequent changes only adjusted the sort
label rendering and Autoruns toolbar spacing. No core/CLI code changed in this task.
Sample-data previews cover the four result views in both themes at 1440×860 and 1040×620 DIPs;
they are under `artifacts/sorting-final/previews/`. The render-to-bitmap previews retain transparent
canvas areas; the native UI Automation run is the authority for the live window.

Pre-push verification rebuilt the combined layout, Autoruns and sorting changes under
`artifacts/push-validation/` with zero warnings/errors. All 227 core/CLI tests and the
in-memory sorting checks passed on that build. The full UI Automation matrix also passed,
including the wide Settings/Privacy two-column assertions.
