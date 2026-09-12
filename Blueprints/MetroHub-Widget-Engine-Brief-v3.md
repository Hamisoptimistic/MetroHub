# METROHUB NATIVE WIDGET ENGINE — MASTER BRIEF v3

Revised against an actual audit of `Hamisoptimistic/MetroHub` (not assumptions). Everything tagged **[EXISTING]** is confirmed already in the repo. Everything tagged **[NEW]** is real construction the original brief implied was already there or glossed over.

---

## PHASE 0: STRUCTURAL PREREQUISITES (do this before Phase 2/3 — not optional, not parallel)

The original brief's Phase 2/3 assumes three pieces of infrastructure exist. They don't. Build these first; everything downstream depends on them.

### 0.1 Split `TileControl` into shell + content [NEW]
Today `TileControl.xaml` is one monolithic `UserControl`. The background fill is set **imperatively in code-behind** (`RootBorder.Background = new SolidColorBrush(...)`, driven by `TileModel.AccentColor`/`TileStyle`), not via template. There is no `ContentPresenter`, no `DataTemplateSelector`, no per-type template resolution.
- Refactor `TileControl` so `RootBorder` hosts a `ContentPresenter` bound to the tile's content, selected via `DataType`-keyed `DataTemplate`s (app tile template vs. widget template per kind).
- In the background-coloring code path, early-return when `TileModel.TileType == TileType.Widget` — widgets never get the accent-fill treatment.
- **Definition of done:** an app tile renders exactly as it does today; a stub "Widget" tile type renders a blank dark card with no accent color, through the new template path, with zero visual regression on existing tiles.

### 0.2 Build one shared `WidgetCard` shell style [NEW]
Without this, every widget author freelances their own border radius, background, padding — visual drift within a few widgets. Build a single reusable style/base template (dark card, hairline border, consistent corner radius, consistent internal padding) that every widget `DataTemplate` wraps its content in. Individual widgets only ever supply *interior* content, never re-define the card chrome.
- **Definition of done:** Clock and one other stub widget both visually share identical card chrome despite different interior content.

### 0.3 Generalize the resize system [NEW]
Today's resize menu is three hardcoded items — `OnResizeSmallClick`/`OnResizeMediumClick`/`OnResizeWideClick` — each a fixed literal size. `SpanX`/`SpanY` on `TileModel` are already free-form ints (no model change needed), but there's no mechanism for a tile/widget to declare *which* sizes it supports.
- Add a size-option list (or `MinSpan`/`MaxSpan` + an explicit allowed-size enum) to the widget definition, and generate the resize context menu from it instead of the three hardcoded handlers.
- App tiles keep their existing three-option behavior unchanged — this is additive, not a replacement.
- **Definition of done:** a stub widget can declare `{1x1, 2x2, 4x2, 4x4}` as its allowed sizes and the context menu reflects exactly that list; an app tile's menu is untouched.

### 0.4 Add a generic settings payload field [NEW, small]
`TileModel` has no field for arbitrary per-instance widget config (weather location, clock format, notes text). Add one nullable string field, e.g. `SettingsJson`, serialized like every other `TileModel` property.
- No change needed to `StorageService`'s `JsonSerializerOptions` — it already tolerates missing/extra fields (no strict/required member handling is set), so forward-compatibility across widget versions is already satisfied by the existing persistence layer. Just keep every widget-specific field nullable with a sensible default when you deserialize `SettingsJson` inside each widget.
- **Definition of done:** a stub widget round-trips a settings value through app close/reopen.

### 0.5 Widget ViewModel pattern — DIRECTIVE, not a choice [NEW]
Use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`) for every widget's state and logic. This is mandatory, not optional, for the following reason: without an explicit instruction, an implementer (human or AI) will default to extending the existing 4,000+ line code-behind style already present in `MainWindow.xaml.cs`, since that's the path of least resistance from the surrounding code. That outcome is undesirable given multiple widgets (Performance, Volume, Notes) have continuously changing live state.
- **Scope boundary — do not blur this:** MVVM applies ONLY to widget ViewModels/Views. The existing canvas/tile/drag-drop code-behind is NOT to be touched, refactored, or migrated to MVVM as part of this work. This is a bolt-on module, not a rewrite.
- **Boundary adapter:** each widget reads its state from `TileModel.SettingsJson` on activation and writes back on deactivation/change (debounced where appropriate, e.g. Notes). This is the only point where the code-behind world and the MVVM world touch — keep it thin and explicit, one method in, one method out.
- **Definition of done:** a stub widget's ViewModel has zero references to any WPF UI element type (`Border`, `TextBlock`, etc.) — all UI lives in its View/DataTemplate, bound only.

### 0.6 Supporting infrastructure — build these alongside the widget layer, not after [NEW]
These are not "nice to have later" — build each one as part of the first widget, since retrofitting them after 5 widgets exist means touching all 5.

- **Declarative widget registry.** One `WidgetDefinition` record (Id, display name, icon, allowed sizes, ViewModel type, View type) that every widget kind registers into a single list. Adding a widget is "add one entry," never "correctly touch five files." This is the highest-value item on this list for an unsupervised/AI-driven build — it removes the chance of a widget being wired in inconsistently.
- **`IMessenger` (from `CommunityToolkit.Mvvm`) for any cross-widget or hub-to-widget notification**, instead of raw C# events. `IMessenger` uses weak references, so a closed widget doesn't leave a dangling subscription holding it in memory — this directly satisfies the "Static Event Cleanup" hardening requirement in Phase 2 without relying on someone remembering a manual `-=` unsubscribe.
- **Source-generated `System.Text.Json` serialization** (`[JsonSerializable]` context classes) for widget settings payloads, instead of reflection-based serialization. Fixes serialization behavior at compile time; removes a class of "forgot to annotate this type correctly" runtime surprises.
- **One reusable attached behavior** (via `Microsoft.Xaml.Behaviors.Wpf`) for the "mark `PreviewMouseLeftButtonDown` as handled" input-safeguard rule already required in Phase 2. Write once, attach to every interactive widget control — removes the risk of the rule being copy-pasted inconsistently or forgotten on a later widget.
- **A small test project — not full coverage, two tests per widget kind:** (1) ViewModel state updates correctly given a fake metrics tick, (2) settings payload round-trips correctly through save/reload. This is the actual verification mechanism when the code isn't being reviewed line-by-line — it catches the exact class of subtle state/persistence bug that slips through unreviewed generated code.

**Explicitly out of scope — do not add:** a DI container (`Microsoft.Extensions.DependencyInjection`). It solves a problem this project doesn't have yet (constructor-injecting many services) and stacking it onto an already-mixed code-behind/MVVM codebase introduces a second new architectural pattern at once. One new pattern (MVVM) per build pass.

---

## STRICT PERFORMANCE & ARCHITECTURAL CONSTRAINTS (revised)

1. **Target working set:** ~15–25MB idle is the goal, **not yet validated on this stack** — treat it as a target to measure against with a real self-contained Release publish, not a hard gate that blocks design decisions. If a Release build measures materially higher, that's a data point to react to, not evidence the architecture is wrong.
2. **Zero admin/UAC friction — confirmed feasible for the widgets that matter, with named exceptions:**
   - CPU (`GetSystemTimes`), RAM (`GlobalMemoryStatusEx`), network throughput (`NetworkInterface`), per-process CPU/RAM (`Process.GetProcesses` + `WorkingSet64`), per-process GPU% (raw PDH counters) — all confirmed zero-admin.
   - NVMe SMART/health data is **not** available zero-admin (`IOCTL_STORAGE_QUERY_PROPERTY` on physical drives typically needs elevation). A "storage" widget under this constraint degrades to free-space-only, no wear/health data — decide now whether that's acceptable scope or the widget gets cut.
3. **No kernel drivers, no reparenting, no WebView2** — unchanged, correct, keep as-is.
4. **Zero battery drain when hidden** — unchanged, correct. Pause/resume ties into `MainWindow.ShowScreen()`/`HideScreen()`, which already exist [EXISTING] and are the right hook points.
5. **No visual stutter** — unchanged, correct.
6. **WPF-UI native, with two corrections:**
   - There is **no dynamic Dark/Light theme switching today** [EXISTING reality check] — `App.xaml` hardcodes `<ui:ThemesDictionary Theme="Dark" />`. Don't design widget theming as if it needs to react to a light-mode path that doesn't exist; widgets simply inherit the fixed dark surface.
   - Mica/Acrylic is applied via **raw P/Invoke** (`NativeMethods.ApplyMica` against `DWMSBT_*` constants), not WPF-UI's backdrop manager [EXISTING reality check]. Any widget-adjacent backdrop work goes through that same native path.

---

## PHASE 1: DISCOVERY — RESULTS (already completed, confirmed against source)

- **Canvas:** freeform `Canvas` via `ItemsControl` with `Canvas.Left`/`Canvas.Top` bound to `TileModel.X`/`Y` (pixel space), snap-to-grid math done entirely in C# (`GridPlacementService`, 64px grid step), not in XAML.
- **Tile model:** `TileModel` (`INotifyPropertyChanged`), `TileType` enum already includes `Widget` as a case — the enum value exists, nothing consumes it yet.
- **Persistence:** `StorageService` → `%LocalAppData%\MetroHub\layout.json` etc., safe temp-file-then-rename writes, tolerant JSON deserialization already in place.
- **Lifecycle:** `App.xaml.cs` (single-instance mutex, hotkey wake), `MainWindow.ShowScreen()`/`HideScreen()`/`ToggleVisibility()` — correct, stable hook points for widget pause/resume.
- **Theming:** WPF-UI 3.0.4, fixed Dark theme, Mica via raw P/Invoke (see corrections above).

---

## PHASE 2: ARCHITECTURAL SPECIFICATION (unchanged from original — this part was sound)

*(Central Data Hub + Dynamic XAML Template architecture, `SystemMetricsService`, `AudioEndpointService`, ring-buffer sparklines via frozen `StreamGeometry`, OS edge-case hardening — all technically correct as originally written. No changes needed here; see prior review. The only addition: every "Tile & UI Integration" item in the original Phase 2 now explicitly depends on Phase 0.1–0.3 above being done first.)*

---

## PHASE 3: IMPLEMENTATION ORDER (revised sequencing)

1. **Phase 0** (this document) — `TileControl` split, shared widget shell, resize generalization, settings payload field, MVVM directive, widget registry, `IMessenger`, source-generated JSON, shared input-safeguard behavior. Nothing below can start cleanly without this.
2. **Core native layer** — `SystemMetricsService`, `AudioEndpointService`. Fully independent of Phase 0; can be built and unit-tested in isolation first if you want early momentum.
3. **First widget suite** — Clock, Performance Monitor, Volume, Notes, Power Controls — now trivial to add since Phase 0 did the structural work once.
4. **Lifecycle hookup + measurement** — wire `Resume()`/`Pause()` into `ShowScreen()`/`HideScreen()`, then actually publish a self-contained Release build and measure working set against the ~15–25MB target instead of assuming it.

---

## PHASE 4: MANDATORY WIDGET LIFECYCLE & RESOURCE HYGIENE (COMPULSORY)

Every widget must adhere to `.agents/rules/WIDGET_LIFECYCLE_STANDARDS.md`:
1. **`IDisposable` Implementation:** Must override `Dispose(bool disposing)` in ViewModel to halt all timers (`DispatcherTimer`, `Timer`) and unhook OS event listeners (e.g. WinRT Media session manager).
2. **View-ViewModel Event Unhooking:** All code-behind subscribing to `PropertyChanged` must hook `Loaded`/`Unloaded` and detach on `Unloaded` to prevent visual tree memory retention.
3. **Zero Background CPU:** Check `_isHubVisible` in any external callbacks; never dispatch UI updates when MetroHub is hidden in the tray.
4. **WPF Freezable Hygiene:** Call `.Freeze()` on all created brushes, pens, and geometries.
5. **Tile Removal Teardown:** Tile removal paths must call `tile.Teardown()` to cascade disposal.

