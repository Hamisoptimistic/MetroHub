# WPF Performance & Hardware Acceleration Standards

> **MANDATORY RULE FOR ALL AI ASSISTANTS**: Every time you author, edit, or refactor XAML or C# UI code for MetroHub (or any WPF application), you MUST adhere strictly to these performance rules. Violating any of these rules causes severe framerate drops (<30 FPS), visual bleeding, and micro-stuttering on users' machines, especially integrated GPUs (AMD Radeon Vega, Intel Iris Xe).

---

## 1. The Translucent Drawer & Scissor Curtain Architecture (CRITICAL)

### The Problem
When building Windows 11 Fluent slide-out drawers (like the "All Apps" drawer) with a pure **Mica backdrop** (`Background="Transparent"`):
1. Any tiles, widgets, or footer text underneath the drawer will bleed through and visually collide with drawer text (e.g. app titles colliding with bottom hotkey status bars).
2. Attempting to dim the canvas looks messy and destroys contrast.
3. Animating `RectAnimation` on `RectangleGeometry.RectProperty` forces WPF's Media Integration Layer (`milcore.dll`) to re-tessellate vector geometry on the CPU every 10ms, dropping 100Hz monitors to 30 FPS.
4. Clipping only `ContentScrollViewer` leaves the bottom status bar (`FooterGrid`) unclipped, causing ugly text overlap at the bottom of the drawer.
5. Running the drawer animation at default 60Hz while the clipping animation runs at 100Hz causes continuous phase desync, micro-stutter, and visible tearing.

### The Mandatory Solution (Locked 100 FPS Scissor Curtain)
1. **Target the Common Parent Grid**: Apply the scissor clip to `MainContentAreaGrid.Clip` (not just `ContentScrollViewer`). This cleanly slices off BOTH the tile canvas and the footer status bar at the drawer's right edge.
2. **Matrix Transform on Reusable Geometry**:
   Never animate `RectangleGeometry.RectProperty`. Always use a reusable geometry with a `TranslateTransform`:
   ```csharp
   _canvasScissorTranslate = new TranslateTransform(0, 0);
   _canvasScissorGeom = new RectangleGeometry
   {
       Rect = new Rect(0, 0, clipWidth, clipHeight),
       Transform = _canvasScissorTranslate
   };
   MainContentAreaGrid.Clip = _canvasScissorGeom;
   ```
   Animating `TranslateTransform.XProperty` sends a 4-float matrix directly to the GPU vertex pipeline with **zero CPU tessellation**.
3. **Clock Synchronization (No Jitter)**:
   Both the drawer slide animation and the scissor translate animation MUST share the exact same parameters:
   - Identical duration (`220ms` open, `180ms` close)
   - Identical easing (`CubicEase.EaseOut` open, `CubicEase.EaseIn` close)
   - Identical timeline refresh rate:
     ```csharp
     int refreshRate = NativeMethods.GetScreenRefreshRate();
     Timeline.SetDesiredFrameRate(anim, refreshRate > 0 ? refreshRate : 100);
     ```
4. **Cleanup on Close**:
   When the closing animation finishes (`anim.Completed`):
   ```csharp
   MainContentAreaGrid.Clip = null;
   _canvasScissorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
   _canvasScissorTranslate.X = 0;
   ```
   Ensures zero clipping overhead during normal idle usage.

---

## 2. Hardware Texture Caching During Motion (`BitmapCache`)

### The Problem
Sliding containers with extensive visual trees (such as the All Apps drawer with 200+ installed apps, ~1,250 visual nodes) flood the UI thread. Every frame of the slide forces WPF to re-traverse, re-measure, and re-rasterize all 1,250 controls.

### The Mandatory Solution
Bake the sliding container into a GPU hardware texture during motion:
```csharp
// Inside Open() / Close():
CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true };

anim.Completed += (s, e) =>
{
    // Restore live rendering for crisp ClearType font rendering & scrolling
    CacheMode = null;
};
```
* **Why it works**: The GPU renders the 1,250 nodes into **1 single texture once**. During the 200ms slide, the GPU simply translates this single quad across the screen at locked 100 FPS with **0% CPU usage**.

---

## 3. The DropShadow & Blur Effect Rule (CRITICAL)

### The Problem
In WPF, applying a `<DropShadowEffect>` or `<BlurEffect>` without a hardware cache forces WPF's rendering pipeline (`milcore`) to **switch the element and its entire visual subtree from GPU hardware acceleration (Render Tier 2) to CPU software emulation**. If an element with a DropShadow moves, animates, or sits under an active canvas, CPU rasterization will choke the render thread, immediately dropping frame rates from 100+ FPS down to **under 30 FPS**.

### The Mandatory Solution
Replace software `DropShadowEffect` with lightweight painted borders or GPU radial gradients. If a `DropShadowEffect` is strictly required, pair it with `CacheMode="BitmapCache"`:

```xaml
<!-- ✅ CORRECT: Hardware accelerated bitmap texture, 60-120+ FPS -->
<Border CornerRadius="2" Background="#20FFFFFF">
    <Border.CacheMode>
        <BitmapCache SnapsToDevicePixels="True" RenderAtScale="1.0" />
    </Border.CacheMode>
    <Border.Effect>
        <DropShadowEffect BlurRadius="8" ShadowDepth="2" Color="Black" Opacity="0.3" />
    </Border.Effect>
</Border>
```

---

## 4. Text Formatting & Icon Scaling Standards

### The Universal Standard
1. Use **`TextFormattingMode="Ideal"`**, **`TextRenderingMode="Grayscale"`**, and **`TextHintingMode="Animated"`** universally across the Window, Root Grid, TextBlocks, Controls, and `ui:SymbolIcon`s.
   * DirectWrite grayscale sub-pixel anti-aliasing renders ultra-clean vector curves without chromatic color fringing on dark/Mica backgrounds, avoiding chunky GDI pixel snapping.
2. When large element trees or icon lists slide or animate, use **`CacheMode="BitmapCache"`** during motion so the GPU translates a single baked texture quad, maintaining 100 FPS without sacrificing font or icon quality.
3. Defer `TextBox.Focus()` to `anim.Completed` rather than on frame 0 to prevent Windows IME message pump initialization from blocking the start of animations.
4. **Optical Centroid & Metric Alignment for Transport Glyphs**:
   * Right-pointing triangle glyphs (e.g. `Segoe MDL2 Assets` Play `&#xE768;`) naturally concentrate their visual mass along their flat left base. Centering the font bounding box directly within a button causes the glyph to appear shifted left by ~1.5px to 2px, ruining perceived symmetry between adjacent chevrons (`<` and `>`). Add a compensatory `Margin="1.5,0,0,0"` to the Play glyph and `Margin="0"` when displaying symmetric states like Pause (`&#xE769;`).
   * Standardize icon heights across states: if Pause bars (`&#xE769;`) appear physically shorter than adjacent chevrons at default sizes, scale the active pause glyph (e.g. `FontSize="15.5"` vs `13`) via style triggers to equalize visual heights.

---

## 5. Tile & Widget Background Transparency Standards

1. **Total Background Transparency (No Artificial Glow/Scrim Overlays)**:
   * Widget views (e.g. `MediaWidgetView`, `PomodoroWidgetView`, `VolumeWidgetView`) MUST NOT inject full-card background overlays (such as ambient radial glows, fluid drift washes, dither film grain, or depth scrims).
   * These opaque stacked layers destroy the native Windows 11 Fluent/Mica translucency of the tile shell (`TileControl`), turning cards into dark, muddy, opaque boxes that clash with the rest of the canvas.
   * All widgets MUST use a clean transparent `WidgetCard` (`Background="Transparent"` with zero background child layers), allowing the desktop wallpaper and system acrylic glass to shine through with 100% cohesion across every tile.
   * Docked bottom bars must use uniform subtle `#14000000` with borderless 2px `#14FFFFFF` hairline tracks.

---

## 6. Widget Action Strip & Sliding Indicator Standards (`WidgetTiles`)

1. **Re-use Standard Controls**:
   * Action bars and navigation strips inside widgets MUST use `WidgetTiles` and `WidgetTile` instead of custom ItemsControls or button stacks.
2. **Zero-Layout Matrix Sliding**:
   * The sliding active indicator MUST animate via `TranslateTransform.XProperty` on a pre-sized visual. Never animate `Margin`, `Width`, or trigger layout passes (`MeasureOverride`/`ArrangeOverride`) during indicator motion.
3. **Hardware Timeline Sync**:
   * Use `Timeline.SetDesiredFrameRate(anim, 120)` with `CubicEase.EaseOut` for butter-smooth 120 FPS slider transitions.
4. **ViewModel Momentary Action Pattern**:
   * For momentary action triggers (e.g. quick actions), bind to `SelectedValue` and immediately reset `CurrentAction = null` in the ViewModel to allow re-triggering without stale selection state.

---

## 7. Zero-CPU Idle Hygiene: Spinners & UI Automation (CRITICAL)

### 1. Dynamic Progress Controls (`IsIndeterminate`)
* **The Problem:** In WPF (especially `Wpf.Ui.Controls.ProgressRing` which uses vector `Arc` geometries, and standard `ProgressBar`), hardcoding `IsIndeterminate="True"` causes background animation storyboards and `MediaContext.AnimatedRenderMessageHandler` to continuously tick in the composition engine—even if the control or its parent container has `Visibility="Collapsed"`. This triggers continuous `PathGeometry.GetPathBoundsAsRB` recalculations and burns 2–5% CPU in pure idle.
* **The Mandatory Rule:** **NEVER** hardcode `IsIndeterminate="True"` in XAML.
  * Always bind dynamically to the loading boolean:
    ```xaml
    <!-- ✅ CORRECT: Storyboard stops completely when not loading -->
    <ui:ProgressRing IsIndeterminate="{Binding IsLoading}"
                     Visibility="{Binding IsLoading, Converter={StaticResource BoolToVis}}" />
    ```
  * In code-behind (e.g. dialogs), always toggle `IsIndeterminate` alongside `Visibility`:
    ```csharp
    spinner.Visibility = Visibility.Visible;
    spinner.IsIndeterminate = true;
    // On complete / dismiss:
    spinner.Visibility = Visibility.Collapsed;
    spinner.IsIndeterminate = false;
    ```

### 2. UI Automation Peer Suppression (`OnCreateAutomationPeer`)
* **The Problem:** When Windows UIAutomation / Accessibility tools or shell monitors are active on the system, WPF's default `WindowAutomationPeer` recursively walks the element tree (`ContextLayoutManager.fireAutomationEvents()` -> `AutomationPeer.UpdateSubtree()`), creating hundreds of `ItemAutomationPeer`s and repeatedly resizing internal collections, burning hundreds of milliseconds in idle layout passes.
* **The Mandatory Rule:** On custom root windows (`BorderlessFluentWindow`) and dense canvas shells, override `OnCreateAutomationPeer()` to return `null` unless explicit external screen-reader integration is required:
  ```csharp
  protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer()
  {
      // Suppress recursive UI Automation peer subtree walking during layout and render passes,
      // preventing ContextLayoutManager.fireAutomationEvents() from burning CPU cycles in idle.
      return null;
  }
  ```

---

## 8. RAM & Managed Allocation Standards (CRITICAL)

1. **No Destructive `EmptyWorkingSet` P/Invokes or Forced GCs**:
   * Never call `psapi.dll!EmptyWorkingSet(hProcess)` or invoke manual `GC.Collect()` on window minimization or idle. Forcibly paging working set pages to disk causes hard page faults and sluggish app restore times.
   * Modern .NET 9 uses self-tuning, background concurrent GC. Avoid manual `GC.Collect()` or `Task.Run` hops on minimize; allow the CLR to manage generation budgets autonomously.

2. **Zero-Leak ViewModel & Element Lifecycle**:
   * Never subscribe anonymous lambdas `Model.PropertyChanged += (s, e) => ...` inside ViewModels without an unsubscription mechanism.
   * Always use named handler methods `OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)` and explicitly detach in `Dispose(bool disposing)`.
   * For static control registries (e.g. `TileControl.ActiveTiles`), hook the canonical WPF `Loaded` (`ActiveTiles.Add(this)`) and `Unloaded` (`ActiveTiles.Remove(this)`) events instead of ad-hoc scans.

3. **Universal Freezable Hygiene**:
   * In WPF, any dynamic `SolidColorBrush`, `LinearGradientBrush`, or `Geometry` created in C# MUST call `.Freeze()` immediately after creation.
   * Frozen Freezables are immutable, thread-safe, and read directly by `milcore.dll` without allocating change-tracking listener hooks or context thread switches.
   * Prefer `StreamGeometry` over heavy `PathGeometry`/`PathFigure` hierarchies for procedural drawing (`StreamGeometry.Open()` -> `geometry.Freeze()`).

4. **Event-Driven Caching Over Polling Allocations**:
   * Avoid calling expensive hardware enumeration APIs (such as `NetworkInterface.GetAllNetworkInterfaces()`, `GetIPProperties()`, and LINQ transformations) on recurring timers.
   * Cache hardware references and invalidate reactively using OS change notifications (e.g. `NetworkChange.NetworkAddressChanged`, `NetworkChange.NetworkAvailabilityChanged`).
   * For sliding histories and time-series samples, use `Queue<T>` with $O(1)$ circular buffer behavior instead of `List<T>.RemoveAt(0)` which causes continuous $O(N)$ array copies.

5. **Buffer Pooling & Zero-Heap Raster Scanning**:
   * Never allocate throwaway byte arrays (e.g. `new byte[height * stride]`) on recurring or interactive paths (such as thumbnail color extractors or media seekbars).
   * Use `ArrayPool<byte>.Shared.Rent(totalBytes)` with a `finally { ArrayPool<byte>.Shared.Return(buffer); }` block.
   * Use `Span<T>` and `stackalloc` for small temporary analysis structures (such as color histogram buckets).

6. **Bounded In-Memory Caches & Devirtualization**:
   * Any static or in-memory dictionary caching bitmaps or assets (such as `CatalogItemModel._memoryIconCache`) must enforce an upper capacity limit (`MaxCachedIcons`) and purge oldest entries to prevent unbounded RAM growth during catalog exploration.
   * Always mark concrete `WidgetViewModelBase` subclasses as `sealed` to allow the .NET JIT compiler to devirtualize method/property dispatches, inline calls, and minimize runtime type overhead.

---

## 9. Summary Checklist Before Merging Any UI Code

- [ ] Scissor curtain clips `MainContentAreaGrid` to prevent canvas & footer text collisions.
- [ ] Scissor curtain animates `TranslateTransform.XProperty` on a reusable `RectangleGeometry` (never `RectAnimation`).
- [ ] Both drawer and scissor animations share `Timeline.SetDesiredFrameRate(anim, refreshRate)`.
- [ ] Sliding panels with dense item lists use `BitmapCache` during motion.
- [ ] `SearchBox.Focus()` is deferred to `anim.Completed`.
- [ ] Every `<DropShadowEffect>` is accompanied by `CacheMode="BitmapCache"` or replaced with GPU borders.
- [ ] Window, styles, and controls specify `TextFormattingMode="Ideal"` and `TextRenderingMode="Grayscale"`.
- [ ] Dynamic media/photo scrims are conditional on thumbnail presence and avoid muddy tinting.
- [ ] Asymmetric glyphs (Play triangle, Pause height) use optical centroid and height compensation.
- [ ] Widget action strips reuse standard `WidgetTiles` and `WidgetTile` instead of custom button templates.
- [ ] `WidgetTiles` indicator slides via `TranslateTransform.XProperty` (zero layout passes during motion).
- [ ] Momentary action tiles use the `CurrentAction = null` reset pattern in ViewModel.
- [ ] Widget ViewModels halt all background timers/polling in `Pause()` when MetroHub is hidden.
- [ ] ViewModels detach all `Model.PropertyChanged` subscriptions in `Dispose(bool)`.
- [ ] Dynamic brushes and procedural vector geometries call `.Freeze()`.
- [ ] Raster and pixel extraction loops use `ArrayPool<byte>.Shared` and `stackalloc`.
- [ ] Hardware queries use event-driven caching (`NetworkChange`) instead of periodic polling.
- [ ] Concrete widget ViewModels are `sealed`.
- [ ] No hardcoded `IsIndeterminate="True"` on `ProgressRing` or `ProgressBar` (bound dynamically to loading state).
- [ ] Root window overrides `OnCreateAutomationPeer()` returning `null` to prevent idle UIA tree walks.
- [ ] Zero manual `GC.Collect()` calls, zero `EmptyWorkingSet()` or forced memory trimming.

