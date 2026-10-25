# Contributor Guide & PR Rules

Welcome! We are excited that you want to contribute to **OpenUninstaller**.

OpenUninstaller is a community-driven, independent, and safety-focused open-source project. Because this software performs privileged filesystem and registry operations on Windows systems, all contributions must adhere to strict security, safety, and quality standards.

Please review these rules before opening an issue or submitting a Pull Request.

---

## 1. Non-Negotiable Core Rules

Every Pull Request must strictly follow these foundational rules. PRs that violate any of these will not be merged.

### 🛡️ Rule 1: 100% Offline & Zero Telemetry
- OpenUninstaller is and will always remain **100% offline**.
- **No network calls, HTTP clients, web sockets, telemetry SDKs, analytics trackers, or auto-updater beacons** are permitted in any assembly.
- Any attempt to introduce network communications will result in immediate rejection.

### 🔒 Rule 2: Non-Negotiable Safety Guards (`PathGuard` & `RegistryGuard`)
- Every destructive filesystem or registry operation **must** pass through `PathGuard` or `RegistryGuard`.
- Hard-coded system protection invariants must **never** be bypassed, relaxed, or weakened:
  - System folders (`C:\Windows`, `System32`, `WinSxS`, `assembly`, Program Files roots, drive roots, user profile roots) cannot be touched.
  - Sensitive registry hives and system keys (`SAM`, `SECURITY`, `SYSTEM\CurrentControlSet\Control`, `Explorer\Shell Folders`, etc.) cannot be deleted or modified.
- High-risk operations (such as file shredding or free space wiping) require explicit user confirmation.

### 💾 Rule 3: Mandatory Pre-Deletion Backups
- Any file, directory, or registry value scheduled for deletion must be captured into a verified backup archive with SHA-256 manifests and `.reg` scripts prior to removal.
- If backup creation fails or is interrupted, the deletion pipeline **must abort immediately**.

### 📦 Rule 4: Zero Third-Party Runtime Dependencies
- `OpenUninstaller.Core`, `OpenUninstaller.Cli`, and `OpenUninstaller.App` must have **zero external third-party dependencies**.
- Rely exclusively on the standard .NET Base Class Library (BCL) and native Windows APIs.
- Unit test projects may use established testing frameworks (`xunit`, `xunit.runner.visualstudio`).

### ⚖️ Rule 5: Clean-Room Independence
- OpenUninstaller is an independent, clean-room software project.
- Do not mention, reference, or import code, schemas, or trademarks from proprietary commercial competitors anywhere in the source code, tests, or documentation.

---

## 2. Code Quality & Formatting Standards

We maintain a clean, fresh, and professional codebase:

- **Target Framework:** C# 13 and .NET 8 / 9.
- **Nullable Context:** Fully enabled. All code must compile with zero nullable warnings.
- **Warnings as Errors:** Builds must pass with 0 warnings in Release configuration (`dotnet build -c Release`).
- **Code Style:**
  - File-scoped namespaces (`namespace OpenUninstaller.Core;`).
  - Four spaces for indentation; no tabs.
  - Explicit typing where clarity is improved; `var` only when the type is obvious from the right-hand side.
  - Clean pattern matching and modern switch expressions.
- **Comment Discipline:**
  - **No chatty, conversational, or obvious inline comments.**
  - Code should be clean and self-explanatory.
  - Use structured, consistent section dividers to organize large files:
    ```csharp
    // ------------------------------------------------------------------ scanning heuristics
    ```
  - Do not include AI-generated markers or attribution comments.

---

## 3. Testing Requirements

No code will be merged without automated test verification:

1. **Isolation Mandate:** Tests must never alter real system state. Always use `TestEnvironment` which provides:
   - Sandboxed temp directory trees for fake `AppData`, `LocalAppData`, `ProgramData`, and `Program Files`.
   - Isolated registry subkeys under `HKCU\Software\OUTests\<guid>`.
2. **Coverage:** Every new scanner heuristic, cleaner category, or guard rule must have corresponding positive and negative unit tests.
3. **Execution:** All unit tests must pass cleanly in Release mode:
   ```powershell
   dotnet test OpenUninstaller.sln -c Release
   ```

---

## 4. Pull Request Submission Checklist

When opening a Pull Request, please copy and complete the following checklist in your PR description:

```markdown
### Summary of Changes
- [Brief description of what was added, fixed, or refactored]

### Safety & Guard Review
- [ ] Confirmed that all destructive operations validate through PathGuard / RegistryGuard.
- [ ] Confirmed that pre-deletion backup is created and verified before any removal.
- [ ] Verified that no system-critical paths or registry hives can be affected.

### Offline & Dependency Review
- [ ] Confirmed 0 network calls, 0 telemetry, 0 tracking.
- [ ] Confirmed 0 third-party runtime NuGet dependencies added.

### Code & Test Verification
- [ ] Clean build with 0 warnings in Release mode (`dotnet build -c Release`).
- [ ] All automated unit tests pass (`dotnet test -c Release`).
- [ ] Tests run against isolated `TestEnvironment` fixtures without touching host system state.
- [ ] Clean code style adhered to: no conversational comments, clean section dividers only.
```

---

## 5. Development Workflow

```powershell
# 1. Fork and clone the repository
git clone https://github.com/<your-username>/openuninstaller.git
cd openuninstaller

# 2. Create a feature branch
git checkout -b feat/your-feature-name

# 3. Restore and build
dotnet restore OpenUninstaller.sln
dotnet build OpenUninstaller.sln -c Release

# 4. Run tests
dotnet test OpenUninstaller.sln -c Release

# 5. Commit using Conventional Commits
git commit -m "feat(scanner): add heuristic detection for orphaned cache trees"

# 6. Push and submit a Pull Request
```

Thank you for helping keep OpenUninstaller clean, safe, and truly open!
