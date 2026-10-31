# OpenUninstaller

<!-- impeccable:product-schema 1 -->

## Platform

Windows desktop, native WPF (.NET 8).

## Product Purpose

Manage installed Windows applications, review uninstall remnants, and perform local maintenance with explicit review and confirmation.

## Capabilities and Constraints

Eight existing destinations: Programs, Autoruns, Junk files, Privacy & shredder, Store apps, Backups, Windows tools, Settings. Existing C# view models, commands, localization, light/dark theme switching, virtualization, backup and approval behavior remain the functional source of truth. Runtime UI uses WPF and has no third-party UI dependencies. No remote assets or network calls are needed by the application.

## Brand Commitments

An elegant, simple and practical native interface: zinc neutrals, inverse primary actions, hairline borders, restrained semantic color, and equivalent light and dark themes. The O and detached-corner symbol is the application identity. Rendering and assets remain local.

## Evidence on Hand

- `README.md`: product and runtime constraints.
- `src/OpenUninstaller.App/MainWindow.xaml`: shell.
- `src/OpenUninstaller.App/Themes/`: shared control styles and theme brushes.
- `src/OpenUninstaller.App/Views/`: existing screens and dialogs.
- `tests/OpenUninstaller.UiSmoke/Program.cs`: read-only navigation verification.
- `docs/assets/`: current light/dark screenshots of all eight screens and Hunter mode, using sample data in the README gallery.

## Product Principles

- Installed application data is the primary working content.
- Keep destructive actions deliberate and visibly distinguishable.
- Use a consistent, legible control system across all screens and dialogs.
- Keep interface copy focused on actions, results, errors and necessary warnings. Avoid redundant explanations and decorative status labels.
- Preserve native keyboard navigation, resizing, and Windows window controls.
