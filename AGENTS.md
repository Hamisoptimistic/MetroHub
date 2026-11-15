# MetroHub — Agent Notes

Windows desktop live-tile launcher (WPF, `net10.0-windows10.0.19041.0`, SVMs via CommunityToolkit.Mvvm, WPF-UI). Single app project + xUnit test project. Detailed standards live in `docs/*.md`.

## Hard rules (non-negotiable)

- **Never start the app.** Do not run `MetroHub.exe`, `dotnet run`, or `dotnet watch run`. The owner tests manually.
- **Never edit code without explicit permission.** A question or clarification request is not permission to edit.
- **Never publish while the app is running.** Stop the process first to avoid file lock errors (MSB3026).
- **Strict Subsystem Containment (No Domain Bleed):** Encapsulate feature logic inside its dedicated directory (e.g. `src/MetroHub/Presentation/Shaders/BackdropShaderDriver.cs`). Do not touch unrelated shell or canvas files.
- **[IMP] Test Standards (Zero Change-Detectors):** Strictly follow `docs/TESTING_STANDARDS.md`. Test observable behavior at the cheapest layer with hardcoded expected values. Never assert internal mocks, private reflection, or call counts.
- Reply concisely in simplified technical English. Use Unicode box-drawing text flowcharts only when strictly necessary (no Mermaid or SVG).

## Build & Publish Workflow

- **Build Verification:** Always verify changes with:
  ```powershell
  dotnet build src\MetroHub\MetroHub.csproj -c Release --nologo -v q
  ```
- **Validation Cycle:** When the build succeeds, STOP and report. Never start the app. The user validates manually.
- **Run Tests:**
  - All tests: `dotnet test`
  - Single class: `dotnet test tests/MetroHub.Tests/MetroHub.Tests.csproj --filter "FullyQualifiedName~RadioCatalogTests"`
- **Publish (Only on Explicit Request):**
  ```powershell
  Stop-Process -Name MetroHub -Force -ErrorAction SilentlyContinue
  dotnet publish src\MetroHub\MetroHub.csproj -c Release -o "C:\Users\HamB\Desktop\MetroHubApp" --self-contained false
  ```

## Widgets

- Adding a widget requires 3 files in `Widgets/Catalog/<Name>/` (ViewModel, View XAML, Settings), then:
  1. Register definition in `Widgets/Registry/WidgetRegistry.cs` (TemplateSelector resolves View automatically).
  2. Register `[JsonSerializable(typeof(<Name>WidgetSettings))]` in `Widgets/Serialization/WidgetJsonContext.cs`.
- Context menus: implement `IWidgetContextMenuProvider` on the ViewModel. Tile clicks live in `TileControl.xaml.cs`.
- Lifecycle: follow `docs/WIDGET_LIFECYCLE_STANDARDS.md`. Override `Dispose(bool)`, stop timers, detach events, and freeze brushes.

## UI & Design System

- Read `docs/DESIGN_SYSTEM.md` before modifying XAML.
- Never show permanent scrollbars. Always use `controls:AutoHideScrollBehavior.IsEnabled="True"`.
- Never hardcode accent colors. Bind to `{DynamicResource SystemAccentColorPrimaryBrush}` and Foreground equivalent.
- Cut taglines, helper text, and secondary labels. If a view is confusing, fix the layout.

## Architecture & Concurrency

- **UI Thread:** Run heavy work off-UI (`Task.Run`). Dispatch property/collection updates via `Application.Current.Dispatcher.InvokeAsync(...)`.
- **Heartbeat & Lifecycle:** Do not create custom timers. Use `OnSecondTick(...)` and pause background work in `Pause()` / `Resume()`.
- **Safety & Cleanup:** Wrap external/hardware calls in `Safe.Try(...)`. Always free unmanaged handles and event handlers in `Dispose(bool)`.

## Testing Quirks & Gotchas

- **[IMP] Testing Standards:** Adhere to `docs/TESTING_STANDARDS.md`. Test public behavior only, avoid mock call assertions, and do not test framework features.
- XAML/view tests require an STA thread via `WpfTestHost.RunSta(...)`.
- Tests redirect state to `%TEMP%\MetroHub_TestSandbox_Default`. Use `WidgetTestSandbox` for widgets.
- Native BASS DLLs are in `lib/native/win-x64/`. Do not move them.
- Shell window inherits `BorderlessFluentWindow`. Dialogs must inherit `Wpf.Ui.Controls.FluentWindow` and run inside `MainWindow.EnterDialogScope()`.
- Protected directories: never edit `bin/`, `obj/`, `publish/`, `dist/`, `logs/`, `diagnostics/`, or `.agents/`.
