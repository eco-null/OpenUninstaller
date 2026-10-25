# Support

OpenUninstaller is maintained by volunteers. Please use the public issue tracker for reproducible
bugs and feature requests; use the private process in [SECURITY.md](SECURITY.md) for vulnerabilities.

Before opening an issue:

1. Read [docs/USER-GUIDE.md](docs/USER-GUIDE.md) and [docs/SAFETY.md](docs/SAFETY.md).
2. Run `oucli doctor --json` and include the redacted result.
3. Reproduce with the latest release or `main`.
4. Remove user names, paths, registry exports, traces and backups from logs before posting.

## Common checks

| Symptom | Check |
| --- | --- |
| No programs listed | Run `oucli list --json`; run as administrator for machine-wide entries |
| Leftovers look wrong | Run `oucli scan-leftovers "Name" --json`; a trace gives exact evidence |
| Junk scan is slow | Browser and shader caches can contain many files; the scan is read-only |
| Service/task toggle fails | These need administrator rights; the UI reports the exact error |
| Restore cannot overwrite files | Use `--overwrite` after reading the restore list |
| Build fails | Install the .NET 8 SDK; run `dotnet build OpenUninstaller.sln -c Release` |

## Not supported

- Removing another product's licence, DRM, or trial limitations.
- Killing processes or unlocking files automatically.
- Uploading traces, backups, or application inventories to a server.
- Running the app as a background service.

## Response expectations

Issues are triaged when a maintainer is available. Security reports are handled through the private
channel and should not be filed publicly.
