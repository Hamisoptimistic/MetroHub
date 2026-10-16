# MetroHub — Agent Notes

Windows desktop live-tile launcher (WPF, `net10.0-windows10.0.19041.0`, SVMs via CommunityToolkit.Mvvm, WPF-UI). One app project + one xunit test project. The owner's detailed standards live in `docs/*.md`; this file carries only what those don't make obvious.

## Owner rules (non-negotiable)

- **Never start the app.** Do not run `MetroHub.exe`, `dotnet run`, or `dotnet watch run`. The owner launches and validates manually.
- **Never edit code without explicit permission.** A question, a "check this", or a clarification request is not permission to change anything.
- **Never publish while the app is running.** `MetroHub.dll` stays locked and MSBuild fails with MSB3026/MSB3027/MSB3021. Kill it first:
  `Stop-Process -Name MetroHub -Force -ErrorAction SilentlyContinue`
- Reply concisely in simplified technical English. Use Unicode box-drawing text flowcharts for flows and design explanations (no Mermaid or SVG).
- `.agents/rules/` docs are partly stale — trust `src/` over them. Known drift: `BUG_FIXING_PROTOCOL.md` §7.2b describes `MediaPlaybackClock`/`MediaSyncTraceLogger`, and `WIDGET_ONBOARDING_CHECKLIST.md` lists `Core/Serialization/MetroHubJsonContext.cs` plus hardcoded DataTemplates; none of these exist anymore (see Widgets below).

## Code Style & Planning

- **Research First:** Search GitHub, WPF-UI, Stack Overflow, and Microsoft official documentation before finalizing plans.
- **Reuse & Optimize:** Inspect current implementations first. Prioritize reuse of existing controls and services.
- **Language Standards:** Use C# 14 and latest language features.
- **No Dead Code:** Clean up dead code immediately; do not keep unused legacy code.

## Commands

- Build app: `dotnet build src\MetroHub\MetroHub.csproj -c Release --nologo -v q`
- All tests: `dotnet test` (Windows only)
- One test class: `dotnet test tests/MetroHub.Tests/MetroHub.Tests.csproj --filter "FullyQualifiedName~RadioCatalogTests"`
- Publish to the owner's run location:
  ```powershell
  Stop-Process -Name MetroHub -Force -ErrorAction SilentlyContinue
  dotnet publish src\MetroHub\MetroHub.csproj -c Release -o "C:\Users\HamB\Desktop\MetroHubApp" --self-contained false
  ```
- CI (`.github/workflows/ci.yml`): windows-latest, .NET 10, `dotnet restore` → `dotnet build` → `dotnet test`.

## Layout

- `src/MetroHub/Core` — services/infra (storage, logging, BASS audio, search, Win32 interop). `Core/Common/AppPaths.cs` is the single source for all state paths; production data lives in `%LocalAppData%\MetroHub`.
- `src/MetroHub/Presentation` — shell (`Views/MainWindow`), canvas `Controls/Canvas/TileControl.*`, dialogs, themes, controllers.
- `src/MetroHub/Widgets` — `Abstractions/WidgetViewModelBase.cs`, `Catalog/<Name>/` (ViewModel + View + Settings per widget), `Registry/WidgetRegistry.cs` (single source of widget metadata).
- `tests/MetroHub.Tests` — xunit tests; references the app project.

## Widgets

- New widget = 3 files in `Widgets/Catalog/<Name>/` (ViewModel : `WidgetViewModelBase`, `*WidgetView.xaml`, `*WidgetSettings.cs`), then:
  1. Add a `WidgetDefinition` (Id, sizes, `ViewType`, factory) in `WidgetRegistry.cs` — `WidgetTemplateSelector` builds views from `ViewType`, so no DataTemplate entry is needed anywhere.
  2. Add `[JsonSerializable(typeof(<Name>WidgetSettings))]` to `Widgets/Serialization/WidgetJsonContext.cs`. Settings persist through `WidgetSerializer`, which is source-generated only.
- Context-menu items: implement `IWidgetContextMenuProvider` on the VM; `TileControl.xaml.cs` (Canvas folder) merges them into the shared menu. Tile click / press-down hooks (`LaunchTile`, `AnimatePressDown`) live in that same file.
- Lifecycle rules are enforced, not optional: override `Dispose(bool)`, stop/null timers, unhook OS events, `.Freeze()` brushes, no `GC.Collect()`/`EmptyWorkingSet()`, never hardcode `IsIndeterminate="True"`. Full checklist: `docs/WIDGET_LIFECYCLE_STANDARDS.md`.

## Design System & UI Invariants (Full Spec: docs/DESIGN_SYSTEM.md)

When creating or modifying XAML views, widgets, or dialogs, adhere strictly to these rules:
- **Mandatory Reference:** Read `docs/DESIGN_SYSTEM.md` for exact XML snippets and subpixel layout math before creating new UI.
- **Scrollbars:** Never show permanent scrollbars. Always set `controls:AutoHideScrollBehavior.IsEnabled="True"`.
- **Corner Radii Hierarchy:** Window = `0`, Tiles = `2`, Inputs/Hover Rows = `4`, Menus/Dialogs = `8`.
- **Accent Colors:** Never hardcode hex codes for accents. Always bind to `{DynamicResource SystemAccentColorPrimaryBrush}` and `{DynamicResource SystemAccentColorForegroundBrush}`.
- **Hairline Dividers:** Never use solid grey. Use 2px hairline `Background="#14FFFFFF"` with `CornerRadius="1"` edge-to-edge.
- **Docked Bottom Containers:** Use `Background="#14000000"`, `BorderThickness="0"`, `CornerRadius="0,0,2,2"`, and `Padding="20,0,20,0"`.
- **Typography:** Use `Segoe UI Variable Text` (14px main, 12px tile labels, 18px display headers).
- **Tile Grid Math:** Unit cell = 60px, gap = 4px (Small: 60x60, Med: 124x124, Wide: 252x124, Large: 252x252).

## Architecture & Concurrency

- **Thread Marshaling:** BASS audio callbacks, Everything search queries, and network listeners run off the UI thread. Any property updates bound to WPF elements must marshal through `Application.Current.Dispatcher.InvokeAsync(...)` to prevent cross-thread exceptions.
- **Messenger Lifetimes:** Always unregister `WeakReferenceMessenger` handlers and detach OS event hooks (e.g., media/power events) inside `Dispose(bool)` or when deactivated to avoid leaking view models.
- **Unmanaged Audio Handles:** BASS channels (`HSTREAM`) must be explicitly freed with `Bass.BASS_StreamFree(...)` before reopening or on shutdown; do not rely on GC finalizers.
- **Bitmap & Brush Memory:** Always set `DecodePixelWidth` on `BitmapImage` to avoid full-resolution RAM bloat, and call `.Freeze()` on all dynamic brushes and icons.
- **External Search Fallback:** The Everything service (`Voidtools.Everything.Net`) may not be running on user machines; all search calls must catch failures and degrade gracefully without crashing.

## Testing quirks

- XAML/view tests must run on an STA thread via `WpfTestHost.RunSta(...)` — it also merges the WPF-UI and app theme dictionaries that XAML loading needs.
- Tests never touch real app data: `AppPaths` detects testhost and redirects to `%TEMP%\MetroHub_TestSandbox_Default`; use `WidgetTestSandbox` for isolated widget-state directories.
- Native BASS DLLs are committed in `lib/native/win-x64/` and copied next to the app and test binaries by both csproj files; don't delete or move them or the radio/audio tests fail.
- `.editorconfig` raises memory/GC/perf analyzers (CA2000, CA1859, S3168, …) to warnings — keep the build warning-clean.

## Gotchas

- Window base classes: the shell `MainWindow` uses `BorderlessFluentWindow` (WS_EX_TOOLWINDOW + `WM_NCACTIVATE` handling). Dialogs must use `Wpf.Ui.Controls.FluentWindow` and wrap their lifetime in `MainWindow.EnterDialogScope()` — inheriting the shell class breaks keyboard focus in dialogs.
- `.agents/`, `.gemini/`, `.vscode/` are gitignored local-only. Never edit generated/runtime dirs: `bin/`, `obj/`, `publish/`, `dist/`, `logs/`, `diagnostics/`.
- Perf tooling the owner uses: `tools\metro-top.ps1` (live CPU/RAM TUI) and `tools\analyze_cpu.py` (parses dotnet-trace speedscope JSON).
