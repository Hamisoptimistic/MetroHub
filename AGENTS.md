# MetroHub — Agent Notes

Windows desktop live-tile launcher (WPF, `net10.0-windows10.0.19041.0`, SVMs via CommunityToolkit.Mvvm, WPF-UI). Single app project + xUnit test project. Detailed standards live in `docs/*.md`.

## Hard rules (non-negotiable)

- **Never start the app.** Do not run `MetroHub.exe`, `dotnet run`, or `dotnet watch run`. The owner tests manually.
- **Never edit code without explicit permission.** A question or clarification request is not permission to edit.
- **Never publish while the app is running.** Stop the process first to avoid file lock errors (MSB3026).
- **Strict Subsystem Containment (No Domain Bleed):** Encapsulate feature logic inside its dedicated directory (e.g. `src/MetroHub/Presentation/Shaders/BackdropShaderDriver.cs`). Do not touch unrelated shell or canvas files.
- Reply concisely in simplified technical English. Use Unicode box-drawing text flowcharts only when strictly necessary (no Mermaid or SVG).

## IDE & MCP Workflow

- **Visual Studio (`vs-debug-mcp`):** When Visual Studio is open, use `vs_run_build` and `vs_get_errors` to verify changes. If Visual Studio is closed, use the terminal build command under Commands. Do NOT run `dotnet publish` during edits.
- **Validation Cycle:** After build succeeds, STOP and report. The user validates manually with `Ctrl + F5`.
- **Publishing:** Run `dotnet publish` ONLY when the user explicitly requests it.
- **Code Intelligence (`roslyn-mcp`):** Primary tool for C# exploration. Always use `roslyn_search_symbols`, `roslyn_find_references`, `roslyn_go_to_definition`, and `roslyn_validate_file` first for C# types, methods, properties, and references. Fall back to `git grep` / PowerShell when Roslyn MCP is offline, unresponsive, or fails to find a symbol, as well as for XAML markup, string literals, and non-C# files.
- **Research:** For unfamiliar WPF or WPF-UI APIs, check official documentation before implementation.

## Commands

- Build app (when VS is closed): `dotnet build src\MetroHub\MetroHub.csproj -c Release --nologo -v q`
- Run all tests: `dotnet test` (Windows only)
- Run one test class: `dotnet test tests/MetroHub.Tests/MetroHub.Tests.csproj --filter "FullyQualifiedName~RadioCatalogTests"`
- Publish to Desktop (only when explicitly requested):
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

- **Thread Marshaling:** BASS audio callbacks, Everything queries, and background listeners run off-UI. Marshal property updates to the UI thread via `Application.Current.Dispatcher.InvokeAsync(...)`.
- **Unmanaged Handles:** Free BASS stream handles with `Bass.BASS_StreamFree(...)` explicitly before reopening or on shutdown.
- **Search Fallback:** The Everything service may not be running. Catch failures and degrade gracefully.

## Testing Quirks & Gotchas

- XAML/view tests require an STA thread via `WpfTestHost.RunSta(...)`.
- Tests redirect state to `%TEMP%\MetroHub_TestSandbox_Default`. Use `WidgetTestSandbox` for widgets.
- Native BASS DLLs are in `lib/native/win-x64/`. Do not move them.
- Shell window inherits `BorderlessFluentWindow`. Dialogs must inherit `Wpf.Ui.Controls.FluentWindow` and run inside `MainWindow.EnterDialogScope()`.
- Protected directories: never edit `bin/`, `obj/`, `publish/`, `dist/`, `logs/`, `diagnostics/`, or `.agents/`.
