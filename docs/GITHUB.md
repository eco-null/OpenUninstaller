# Publishing OpenUninstaller on GitHub

The project lives at <https://github.com/eco-null/OpenUninstaller> under the MIT license. Only the
Release workflow pushes to the repository, and only when a maintainer starts it.

## Repository settings

- **Description:** `Open-source Windows deep uninstaller: traces, reversible cleanup, backups, autoruns and junk tools.`
- **Topics:** `windows`, `uninstaller`, `dotnet`, `wpf`, `open-source`, `cleanup`, `leftovers`,
  `privacy`, `system-administration`.
- **Features:** enable Issues and Discussions; disable the Wiki unless it is maintained.
- **Security:** enable private vulnerability reporting, Dependabot alerts, secret scanning and push
  protection. The policy is in [SECURITY.md](../SECURITY.md).
- **Branches:** protect `main`; require the `build` CI job and one review. Do not allow force pushes.
  The Release workflow pushes its version commit to `main` with the workflow token, so if `main`
  requires reviews, allow GitHub Actions to bypass that rule (or the push step fails).
- **Releases:** see below.
- **Licence:** MIT is already in [LICENSE](../LICENSE). Do not add a different licence to release
  artifacts.

## Versioning

`Directory.Build.props` is the only place the version is written. Every assembly takes its
`Version`, `AssemblyVersion` and `FileVersion` from it, so the version in the app's navigation bar,
`oucli version`, the publish manifest, the archive name and the installer all follow it.
`scripts/set-version.ps1 -Bump patch|minor|major` (or `-Version X.Y.Z`) updates all three properties
and turns the `## [Unreleased]` section of `CHANGELOG.md` into the new version's section.

## Publishing a release

1. While working, add entries under `## [Unreleased]` in `CHANGELOG.md`. Optionally prepare
   `docs/releases/<next version>.md` for richer GitHub release notes. Update `docs/BACKLOG.md` /
   `TODO.md` when their tracked status changes.
2. On GitHub open **Actions › Release › Run workflow**, keep the branch on `main`, choose `patch`,
   `minor` or `major`, and run it. From a terminal: `gh workflow run release.yml -f bump=patch`.
3. The workflow:
   - bumps the version with `scripts/set-version.ps1` and refuses a version whose tag exists;
   - builds with warnings as errors, runs the tests and UI smoke, and checks that the built app and
     `oucli version` report the new version;
   - publishes `OpenUninstaller-<version>-win-x64.zip` and compiles
     `OpenUninstaller-<version>-setup.exe`, each with a `.sha256` file;
   - commits `Directory.Build.props` and `CHANGELOG.md` as `chore(release): vX.Y.Z` (authored by
     the person who started the run), tags `vX.Y.Z` and pushes both atomically; if `main` moved
     during the run nothing is pushed and the run fails;
   - creates the GitHub release with notes from `docs/releases/<version>.md`, else the version's
     `CHANGELOG.md` section, else GitHub-generated notes.
4. Pull `main` locally afterwards to get the version commit.
5. Download the uploaded zip or installer on a clean Windows machine, run the UI and
   `cli\oucli.exe doctor`, and verify the SHA-256. Run the new setup over the previous release and
   confirm it offers an update and keeps the install folder. Confirm the optional service binary
   exists under `service/` and is not installed or running by default.

A hand-pushed `v*` tag still runs the same workflow without the bump, provided the tag matches
`Directory.Build.props`. Rerunning a release refreshes its assets and checked-in notes.

To check a release locally before starting it:

```powershell
dotnet build OpenUninstaller.sln -c Release -warnaserror
dotnet test OpenUninstaller.sln -c Release
dotnet run --project tests/OpenUninstaller.UiSmoke/OpenUninstaller.UiSmoke.csproj -c Release -- src/OpenUninstaller.App/bin/Release/net8.0-windows/OpenUninstaller.exe
./scripts/package-release.ps1 -Configuration Release -Runtime win-x64
./scripts/build-installer.ps1 -Configuration Release -Runtime win-x64
```

## Code signing

Signing requires a certificate owned by the maintainer. `scripts/sign-artifacts.ps1 -Thumbprint <sha1>`
signs `OpenUninstaller.exe`, `cli\oucli.exe` and `service\OpenUninstaller.Service.exe` in the publish
folder with `signtool.exe`; pass `-Path artifacts\release\OpenUninstaller-<version>-setup.exe` to sign the
setup as well. The Release workflow does not sign. Document the certificate fingerprint in the
release notes.
See [SECURITY.md](../SECURITY.md) for the unsigned-build warning.

## Artifacts and privacy

Do not upload traces, backups, `manifest.json` files from a real machine, user paths, or logs that
contain personal data. The CI artifact contains only build output and documentation.
