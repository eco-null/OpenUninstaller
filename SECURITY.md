# Security policy

## Supported versions

Only the latest release receives security fixes. The current release line is v0.6.x.

## Reporting a vulnerability

Do **not** open a public issue for a vulnerability or a guard bypass. Report it privately to the
maintainers through the repository's private security advisory feature, or by contacting the
maintainer listed in the repository profile. Include:

- affected version and Windows build;
- a minimal reproduction (a synthetic folder or registry path is enough);
- the guard or safety invariant that failed;
- impact and whether the issue needs elevation.

You should receive an acknowledgement within a few days. Please allow time for a fix before public
disclosure. There is no bug bounty.

## In scope

- `PathGuard` or `RegistryGuard` accepting a target that should be protected;
- deleting without a successful backup;
- restoring a backup outside its recorded locations;
- path traversal, UNC/device path, reparse-point or junction escapes;
- command-line injection into a vendor uninstaller or a registry export;
- elevation or privilege-boundary bugs;
- a scan reading files it should not, or logging secrets;
- a privacy/shred target escaping its fixed allow-list or a `PathGuard` decision;
- any unexpected or unconsented network connection attempt (the app is strictly offline with zero telemetry);
- the maintenance service performing an action outside the documented retention/junk allow-list;
- denial of service caused by a malicious application name, registry value or trace file.

## Out of scope

- A vendor uninstaller doing something malicious. OpenUninstaller runs the vendor's own command
  with the user's consent and cannot sandbox it.
- A heuristic match that is wrong but still requires the user to select and confirm it. Report it
  as a scanner-quality issue, not a vulnerability, unless it bypasses a guard.
- Issues that require an already-compromised administrator account.
- Missing code signing or installer hardening (tracked in the backlog).
- Windows itself deleting files because a user approved a deletion.

## Design guarantees

The security model is documented in [docs/SAFETY.md](docs/SAFETY.md) and the scanner rules in
[docs/ALGORITHMS.md](docs/ALGORITHMS.md). The application is 100% offline, makes zero network calls,
contains zero telemetry or external phoning home, stores no credentials, and runs no background
service unless the user installs the optional maintenance service.

## Handling reports

Maintainers will reproduce on a supported Windows build, add a regression test that exercises the
guard directly, and publish a fixed release with a short advisory. Guard bypasses are treated as
release blockers.
