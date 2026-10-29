# Project: MetroHub Workspace Safety & Persistence

## Architecture
MetroHub is a WPF-based Windows desktop launcher using .NET 10 (`net10.0-windows10.0.19041.0`).
Workspace lifecycle and data persistence are governed by two core components:
1. **`StorageService` (`src/MetroHub/Core/Services/StorageService.cs`)**:
   - Manages atomic file operations, serialization/deserialization of `workspaces.json`, `layout.json`, and `groups.json`.
   - Uses NTFS `ReplaceFileW` and WriteThrough flushing via `SaveAtomic()`.
   - Discovers and reconstructs workspace models defensively from `AppPaths.WorkspacesDir` when manifest is absent or malformed.
2. **`WorkspaceManager` (`src/MetroHub/Presentation/Controllers/WorkspaceManager.cs`)**:
   - Singleton controller managing the active workspace, workspace collection, and tile subscriptions.
   - Cleans orphaned or zombie workspace directories defensively without destroying valid workspace directories.
   - Synchronously and atomically persists workspace metadata changes (name, icon, order) on edit or property change.
3. **Companion & Shell Views (`src/MetroHub/Presentation/Views/MainWindow/`)**:
   - Houses shell windows and companion service endpoints for workspace discovery and tile pinning.
4. **Test Suite (`tests/MetroHub.Tests/Core/`)**:
   - Runs on xUnit with isolated test sandboxes (`AppPaths.CustomAppDataDir`) under sequential execution collection `[Collection("StorageTests")]`.

```
┌──────────────────────────────────────────────────────────────────┐
│                   Storage & Recovery Architecture                │
└──────────────────────────────────────────────────────────────────┘
                                 │
     App Startup: StorageService.LoadWorkspaces()
                                 │
                 workspaces.json missing / corrupt?
                 ├─── NO ──► Deserialize & return
                 │
                 └─── YES
                       │
                       ▼
         PreserveCorruptFile(workspaces.json)
                       │
                       ▼
         DiscoverWorkspacesFromDisk()
         (Scan AppPaths.WorkspacesDir)
                       │
          Subdirectories found with layout/groups?
          ├─── YES ──► Build manifest entries
          │            ActiveId = "default" or first
          │            SaveWorkspacesSync(manifest)
          │            Return recovered manifest
          │
          └─── NO ───► MigrateLegacyToWorkspaces()
                       SaveWorkspacesSync(manifest)
                       Return starter manifest
                                 │
                                 ▼
         WorkspaceManager.CleanUnmanifestedWorkspaces()
         • Empty/corrupt zombie directory (<= 2 bytes) ──► Delete
         • Valid layout (> 2 bytes) or recovery ─────────► PRESERVE!
```

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | R1 Syntax Typo Fix | Ensure line 356 of `MainWindow.xaml.cs` contains valid `return list;` code | M1 | ORIGINAL_REQUEST §R1 |
| 2 | R2 Defensive Disk Recovery | `StorageService.LoadWorkspaces()` scans `AppPaths.WorkspacesDir` for existing workspace folders upon missing/corrupt manifest | M1 | ORIGINAL_REQUEST §R2 |
| 3 | R2 Diagnostic Preservation | `StorageService.LoadWorkspaces()` preserves corrupt manifest to timestamped backup | M1 | ORIGINAL_REQUEST §R2 |
| 4 | R3 Non-Destructive Cleanup | `WorkspaceManager.CleanUnmanifestedWorkspaces()` never trashes directories with valid layout data or during recovery | M2 | ORIGINAL_REQUEST §R3 |
| 5 | R3 Zombie Directory Pruning | `WorkspaceManager.CleanUnmanifestedWorkspaces()` deletes only empty or corrupt zombie folders | M2 | ORIGINAL_REQUEST §R3 |
| 6 | R4 Atomic Name/Icon Persistence | `WorkspaceManager.UpdateWorkspace()` commits name/icon updates synchronously and atomically to disk | M2 | ORIGINAL_REQUEST §R4 |
| 7 | R4 Model Property Change Sync | `AttachWorkspaceTracking` hooks `ws.PropertyChanged` for Name, IconSymbol, Order to persist synchronously | M2 | ORIGINAL_REQUEST §R4 |
| 8 | R5 Missing Manifest Test | Unit test verifying recovery of multiple existing disk folders when `workspaces.json` is missing | M3 | ORIGINAL_REQUEST §R5 |
| 9 | R5 Corrupted Manifest Test | Unit test verifying recovery of existing disk folders when `workspaces.json` is malformed/truncated | M3 | ORIGINAL_REQUEST §R5 |
| 10 | R5 Safe Cleanup Guardrail Test | Unit test verifying `CleanUnmanifestedWorkspaces()` never moves valid folders to `workspaces_trash` | M3 | ORIGINAL_REQUEST §R5 |
| 11 | R5 Zombie Cleanup Test | Unit test verifying `CleanUnmanifestedWorkspaces()` correctly deletes 0-byte zombie folders | M3 | ORIGINAL_REQUEST §R5 |
| 12 | R5 Synchronous Persistence Test | Unit test verifying immediate on-disk presence of name and icon edits without manual flush | M3 | ORIGINAL_REQUEST §R5 |
| 13 | R5 Property Mutation Test | Unit test verifying direct `WorkspaceModel` property changes immediately persist to disk | M3 | ORIGINAL_REQUEST §R5 |
| 14 | R6 Release Build Verification | Verify `dotnet build src\MetroHub\MetroHub.csproj -c Release --nologo -v q` produces 0 errors and 0 warnings | M4 | ORIGINAL_REQUEST §R6 |
| 15 | R6 Full Test Suite Execution | Verify all tests pass via `dotnet test` under sandboxed environment without executing the app | M4 | ORIGINAL_REQUEST §R6 |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Storage Core & Syntax Fix | Line 356 syntax check in `MainWindow.xaml.cs`, `StorageService.LoadWorkspaces()` defensive discovery via `DiscoverWorkspacesFromDisk()` and `PreserveCorruptFile()` | none | DONE |
| M2 | Workspace Manager Safety & Sync | Hardening `WorkspaceManager.CleanUnmanifestedWorkspaces()`, `AttachWorkspaceTracking` property sync for `WorkspaceModel`, synchronous manifest commits on edits | M1 | DONE |
| M3 | Dedicated Unit Test Suite | Comprehensive tests in `tests/MetroHub.Tests/Core/WorkspaceRecoveryAndPersistenceTests.cs` covering R2, R3, R4 | M2 | DONE |
| M4 | Final Build, Verification & Audit | Release compilation, full test execution, reviewer, challenger, and forensic audit verification | M3 | DONE |

## Interface Contracts
### `StorageService` ↔ `WorkspaceManager`
- `StorageService.LoadWorkspaces()`:
  - Return type: `WorkspacesManifest` (guaranteed non-null, with at least one `WorkspaceModel`).
  - Behavior: Deserializes `workspaces.json` or `.bak`. If missing/corrupt, calls `PreserveCorruptFile()`, invokes `DiscoverWorkspacesFromDisk()`, and falls back to `MigrateLegacyToWorkspaces()`. Commits recovered manifest to disk immediately.
- `StorageService.SaveWorkspacesSync(WorkspacesManifest manifest)`:
  - Synchronously writes manifest to `workspaces.json` with WriteThrough and backup rotation.
- `WorkspaceManager.CleanUnmanifestedWorkspaces(IEnumerable<string>? validIds, bool isRecoveryScenario = false)`:
  - Signature: `internal static void CleanUnmanifestedWorkspaces(...)`.
  - Behavior: If `validIds` is null or empty, aborts. Inspects subdirectories of `AppPaths.WorkspacesDir`. Only deletes directories where `layout.json` is missing or $\le 2$ bytes. Never moves valid layouts to trash if `isRecoveryScenario` is true or during initial recovery.

## Code Layout
- `src/MetroHub/Presentation/Views/MainWindow/MainWindow.xaml.cs`: Contains `InitializeCompanionService()` (Line 356).
- `src/MetroHub/Core/Services/StorageService.cs`: Contains `LoadWorkspaces()`, `DiscoverWorkspacesFromDisk()`, `SaveWorkspacesSync()`.
- `src/MetroHub/Presentation/Controllers/WorkspaceManager.cs`: Contains `CleanUnmanifestedWorkspaces()`, `AttachWorkspaceTracking()`, `UpdateWorkspace()`.
- `tests/MetroHub.Tests/Core/WorkspaceRecoveryAndPersistenceTests.cs`: New dedicated unit test fixture under `[Collection("StorageTests")]`.

