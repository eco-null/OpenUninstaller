## Summary

<!-- What user-visible problem does this solve? Keep it short. -->

## Safety

- [ ] No destructive action runs without an explicit confirmation or `--yes`.
- [ ] New deletion paths go through `PathGuard`, `RegistryGuard`, or `ScheduledTaskGuard`.
- [ ] Every new leftover kind is backed up before removal, or is explicitly report-only.
- [ ] A failed backup refuses the deletion instead of continuing.
- [ ] User paths, traces, backups, and credentials are not logged.

## Verification

- [ ] `dotnet build OpenUninstaller.sln -c Release -warnaserror`
- [ ] `dotnet test OpenUninstaller.sln -c Release`
- [ ] CLI smoke uses read-only commands unless the test uses an isolated fixture.
- [ ] Docs updated: `TODO.md`, `docs/BACKLOG.md`, and `docs/ALGORITHMS.md` when scanner rules change.

## Notes

<!-- Migration notes, limitations, screenshots, or follow-up work. -->
