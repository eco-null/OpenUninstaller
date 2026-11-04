# Publishing OpenUninstaller on GitHub

This repository is ready for a public GitHub project. Nothing here pushes automatically.

## First push

From the project root:

```powershell
git init
git add .
git commit -m "Initial OpenUninstaller import"
git branch -M main
git remote add origin https://github.com/<owner>/OpenUninstaller.git
git push -u origin main
```

The project is fully independent, clean and open-source under the MIT license.

## Repository settings

- **Description:** `Open-source Windows deep uninstaller: traces, reversible cleanup, backups, autoruns and junk tools.`
- **Topics:** `windows`, `uninstaller`, `dotnet`, `wpf`, `open-source`, `cleanup`, `leftovers`,
  `privacy`, `system-administration`.
- **Features:** enable Issues and Discussions; disable the Wiki unless it is maintained.
- **Security:** enable private vulnerability reporting, Dependabot alerts, secret scanning and push
  protection. The policy is in [SECURITY.md](../SECURITY.md).
- **Branches:** protect `main`; require the `build` CI job and one review. Do not allow force pushes.
- **Releases:** pushing a `v*` tag starts `Release`, which builds, tests, runs UI smoke, and
  publishes the portable ZIP, installer and both SHA-256 files. Notes come from
  `docs/releases/<version>.md`, with GitHub-generated notes as a fallback.
- **Licence:** MIT is already in [LICENSE](../LICENSE). Do not add a different licence to release
  artifacts.

## Release checklist

1. Update `Version` in `Directory.Build.props` (the publish manifest reads it).
2. Add a `CHANGELOG.md` section and `docs/releases/<version>.md` with the same version.
   Update `docs/BACKLOG.md` / `TODO.md` when their tracked status changes.
3. Run locally:
   ```powershell
   dotnet build OpenUninstaller.sln -c Release -warnaserror
   dotnet test OpenUninstaller.sln -c Release
   dotnet run --project tests/OpenUninstaller.UiSmoke/OpenUninstaller.UiSmoke.csproj -c Release -- src/OpenUninstaller.App/bin/Release/net8.0-windows/OpenUninstaller.exe
   ./scripts/package-release.ps1 -Configuration Release -Runtime win-x64
   ./scripts/build-installer.ps1 -Configuration Release -Runtime win-x64
   ```
4. Commit, push `main`, then tag the same commit and push the tag:
   ```powershell
   git push origin main
   git tag v1.0.9
   git push origin v1.0.9
   ```
5. The release workflow runs tests, UI smoke, publishes `OpenUninstaller-<version>-win-x64.zip`,
   compiles `OpenUninstaller-<version>-setup.exe`, writes `.sha256` checksums, and creates the GitHub release.
   Tag and build versions must agree. Rerunning the workflow refreshes assets and checked-in notes.
6. Download the uploaded zip or installer on a clean Windows machine, run the UI and `cli\oucli.exe doctor`,
   and verify the SHA-256 from the release notes. Confirm the optional service binary exists under
   `service/` and is not installed or running by default.

## Code signing

Signing requires a certificate owned by the maintainer. A helper script `scripts/sign-release.ps1`
is provided to sign `OpenUninstaller.exe`, `cli\oucli.exe`, `service\OpenUninstaller.Service.exe`, and the
setup executable `OpenUninstaller-<version>-setup.exe` using `signtool.exe`. Document the certificate fingerprint
in the release notes.
See [SECURITY.md](../SECURITY.md) for the unsigned-build warning.

## Artifacts and privacy

Do not upload traces, backups, `manifest.json` files from a real machine, user paths, or logs that
contain personal data. The CI artifact contains only build output and documentation.
