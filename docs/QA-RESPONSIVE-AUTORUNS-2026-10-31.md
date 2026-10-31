# Responsive cards and autorun modes — 2026-10-31

## Scope

- Settings and Privacy use centered content up to 1680 DIPs wide, with stable groups of related
  cards. They stack at the minimum window and use two columns at default and wider sizes.
  Settings has five consolidated cards; Privacy separates cleanup from permanent erasure.
  Long history/browser lists have their own bounded scrolling region.
- Junk and Windows tools retain the responsive one-to-four-column card layout.
  Short cards retain their natural heights rather than stretching beside long lists.
- Autoruns defaults to Basic, with Advanced and the existing History & rollback view available.
  Switching modes preserves search and clears selection when its entry becomes hidden.

## Startup inventory

Basic includes Run/RunOnce entries, both Startup folders, automatic or disabled application
services, application/user scheduled tasks and declared packaged startup tasks. Microsoft user
applications such as PowerToys and Terminal remain visible, as explicitly requested by the user.
Windows system tasks/components, kernel drivers and manual-only services remain in Advanced.
Unknown application publishers are retained rather than filtered by publisher text alone.

Package discovery reads `windows.startupTask` extensions from registered package manifests and
the matching current-user AppModel task state. Unrelated state keys do not create entries.
Packaged tasks are managed through Windows Startup settings. Classic entries respect
`Explorer\StartupApproved`; enabling an externally disabled entry preserves its launch command.
Scheduled tasks are read through the local Task Scheduler COM API, with readable XML files
as an additional source. Protected service metadata is skipped with a warning.

## Verification

- Release solution build with warnings treated as errors: zero warnings/errors.
- 227 core/CLI tests, including 34 new discovery, classification and state cases, passed.
- Actual view-model probe: Basic/Advanced membership, Microsoft application retention,
  search preservation, hidden selection reset and history switching passed.
- Actual card-panel probe: one/two/three/four columns, natural heights and reading order passed.
- UI smoke checks cover all eight screens in both themes at default/minimum sizes,
  card placement, mode switching, search/selection and stable two-column Settings/Privacy.
- A read-only live scan found 998 Advanced entries and 96 Basic entries, including 28 scheduled
  tasks and 14 packaged startup tasks. Representative screenshot entries were confirmed.
  These are environment-specific observations, not expected counts for other machines.

Local evidence: `artifacts/responsive-autoruns-validation/`, `artifacts/autorun-probe/`
and `artifacts/layout-probe/`. No real startup entry was enabled/disabled during verification;
no cleanup, service installation or application removal was performed.

## Grouping refinement

User feedback on the first layout identified scattered narrow cards at full-screen width.
Settings and Privacy now use fixed semantic groups instead of placing every card in the shortest
column. A focused panel probe checks that changing one group's content does not relocate its
neighbor at one/two-column widths. All 42 Settings and 60 Privacy bindings remain unchanged.
Current default/full-screen previews are under `artifacts/grouped-layout-validation/previews/`.

Pre-push verification of the combined changes under `artifacts/push-validation/` passed:
Release solution build with zero warnings/errors, all 227 core/CLI tests, sorting regression
checks and the full UI Automation matrix, including wide two-column placement.

## Sources

- User-provided Revo screenshots and the explicit decision to retain Microsoft user applications.
- [Revo Autorun Manager](https://www.revouninstaller.com/online-manual/tools/).
- [Windows startup locations](https://support.microsoft.com/en-au/windows/experience/startup-boot/configure-startup-applications-in-windows).
- [Packaged StartupTask declarations](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask).
- [Task Scheduler folders and tasks](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasks).
- [Windows indirect resource strings](https://learn.microsoft.com/en-us/windows/win32/api/shlwapi/nf-shlwapi-shloadindirectstring).

The source inventory is based on these documented Windows mechanisms and local read-only evidence.
