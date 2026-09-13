# Mandatory Widget Lifecycle & Resource Hygiene Standards

> **CRITICAL DIRECTIVE**: Whenever building or modifying any widget for MetroHub, you MUST read and enforce this checklist. Failure to adhere to these rules leads to memory leaks, orphaned background timers, battery drain, and process bloat.

---

## 1. Mandatory `IDisposable` & Teardown Implementation
Every widget ViewModel **MUST** extend `WidgetViewModelBase` and override `Dispose(bool disposing)`.
- **Halt All Timers:** Stop and nullify any `DispatcherTimer`, `System.Threading.Timer`, or polling loops.
- **Unhook External / OS Event Listeners:** Always unsubscribe from Windows OS APIs (e.g. `GlobalSystemMediaTransportControlsSessionManager`), system sound notifications, device change listeners, or network monitors.
- **Deactivate CommunityToolkit Messenger:** Calling `base.Dispose(disposing)` sets `IsActive = false`, which unregisters all weak-reference messages.
- **Release Large In-Memory Assets:** Null out references to `ImageSource`, `BitmapSource`, byte arrays, or large data collections.

```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        StopTimer();
        _timer = null;
        // Unsubscribe from any OS or external events
    }
    base.Dispose(disposing);
}
```

---

## 2. WPF View-to-ViewModel Event Hygiene
If a `UserControl` code-behind subscribes to ViewModel property changes (e.g. `vm.PropertyChanged += OnViewModelPropertyChanged`):
- **MUST** hook `Loaded` and `Unloaded` on the `UserControl`.
- On `Unloaded`: **MUST** unsubscribe: `_vm.PropertyChanged -= OnViewModelPropertyChanged`.
- On `Loaded`: If `DataContext` is set, re-subscribe.
- On `DataContextChanged`: Unsubscribe from `e.OldValue` before subscribing to `e.NewValue`.
- **Reason:** Failure to unhook prevents the UserControl (and its entire visual sub-tree with shapes, storyboards, and drop shadows) from being garbage collected when tiles are resized, moved, or detached.

---

## 3. Zero Background CPU & Battery Conservation When Hidden
MetroHub rests quietly in the Windows system tray when closed.
- **Visibility Check:** In any asynchronous callbacks, OS event delegates, or media listeners, check `_isHubVisible` before dispatching work.
- **No Idle UI Dispatching:** **NEVER** dispatch `Dispatcher.InvokeAsync` updates when MetroHub is hidden (`!_isHubVisible`).
- **Dormant Timers for Background State:** If a widget tracks ongoing elapsed time while hidden (like Pomodoro), DO NOT tick a high-frequency UI timer. Instead, pause the UI timer and either compute elapsed time on `Resume()` via `DateTime.UtcNow`, or arm a single dormant one-shot `System.Threading.Timer` with zero CPU overhead.

---

## 4. WPF Freezable Resource Hygiene
- **Always Call `.Freeze()`:** Any created `SolidColorBrush`, `GradientBrush`, `DrawingImage`, or `BitmapSource` MUST have `.Freeze()` called before assignment.
- **Reason:** Frozen resources become immutable and free-threaded, reducing memory tracking overhead and allowing WPF to optimize hardware composition.

---

## 5. Tile Removal & Unpinning Teardown
- Whenever a tile is removed from `Tiles` (unpin, group deletion, layout reset, undo/redo):
- The removal logic **MUST** invoke `tile.Teardown()`, which cascades `Dispose()` into the widget's ViewModel and clears references.

---

## 6. Widget UI & Visual Consistency Standards (The Universal 20px Symmetrical Grid)

> **MANDATORY FOR ALL CURRENT & FUTURE WIDGETS**: Every widget created or updated in MetroHub (Media Player, Focus Timer, Photo Stream, Weather, Volume, Notes, etc.) **MUST** automatically conform to this symmetrical grid system.

- **Universal 20px Symmetrical Margin / Padding:**
  - ALL widgets must maintain an exact **20px clearance** from the left and right tile borders.
  - Left margin from tile border: **20px**.
  - Right margin from tile border: **20px**.

- **Left-Edge Alignment Axis (X = 20px):**
  - Top-tier primary text (Track Title, Phase Title, Widget Header) MUST start at **20px** from the left border (`Margin="20,0,..."`).
  - Bottom docked transport controls (`StackPanel`) MUST declare `Margin="-9.5,0,0,0"` inside `Padding="20,0,20,0"`.
  - **Reason / Math:** The standard 32px-wide transparent transport button (`MediaTransportButtonStyle`) centers its 13px icon (`(32 - 13) / 2 = 9.5px`). Setting `Margin="-9.5,0,0,0"` cancels this internal button padding so the very first glyph (e.g. `<` chevron or `↺` reset) starts at **precisely X = 20px**, forming an unbroken vertical alignment line with the title text above.

- **Right-Edge Alignment Axis (X = Width - 20px):**
  - Top-tier hero visuals (Album Art Cover, Circular Progress Ring Gauge, Hero Cards) MUST terminate at **20px** from the right border (`Margin="0,0,20,0"`).
  - Bottom docked secondary readouts (Live Playback Time `3:05 / 5:21`, Status Badges, Cycle Tracker Dots) MUST terminate at **20px** from the right border.
  - When using indicator dots with internal spacing (`Margin="2.5,0"`), offset the StackPanel by `Margin="0,0,-2.5,0"` so the outer edge of the final dot aligns flush with the 20px right margin.

- **Transport Controls & Play/Pause Symmetry (`MediaTransportButtonStyle`):**
  - **NEVER** use boxed buttons with hard outline borders (`BorderThickness="1"`), solid grey backgrounds, or boxy frames.
  - **Button Dimensions:** `Width="32"`, `Height="28"`, `CornerRadius="2"`, `Background="Transparent"`, `Margin="4,0"`.
  - **Play Icon:** `FontSize="13"`, `Text="&#xE768;"`, `Margin="1.5,0,0,0"` (optical centering adjustment).
  - **Pause Icon:** `FontSize="15.5"`, `Text="&#xE769;"`, `Margin="0"` (scaled to 15.5px so its optical height and visual weight match adjacent 13px chevrons and skip buttons).

- **Timing & Secondary Readout Containers:**
  - Readouts (such as track playback time `TimeDisplayString`) must be hosted in a container with `Height="28"` and `VerticalAlignment="Center"` to match the exact 28px height footprint and vertical center axis of the transport buttons.
  - Typography: `FontSize="13"`, `FontFamily="Segoe UI Variable Text, Segoe UI, sans-serif"`, `Foreground="#D0FFFFFF"`.

- **Universal Edge-to-Edge Separator & Progress Divider Standard:**
  - Separators between Row 0 (content) and Row 1 (docked bar) **MUST AUTOMATICALLY** use an edge-to-edge 2px hairline hosted in a centered 14px container (`Margin="0,-7,0,0"` on `Grid.Row="1"`):
    ```xml
    <!-- Universal Fluent Edge-to-Edge Hairline Divider -->
    <Grid Grid.Row="1"
          VerticalAlignment="Top"
          Height="14"
          Margin="0,-7,0,0"
          Background="Transparent"
          SnapsToDevicePixels="True">
        <Border Height="2"
                VerticalAlignment="Center"
                Background="#14FFFFFF"
                CornerRadius="1"
                SnapsToDevicePixels="True" />
    </Grid>
    ```
  - **Subpixel Centering Rationale:** The 14px container with `Margin="0,-7,0,0"` centers the 2px line at $Y = 0$ (the boundary line between Row 0 and Row 1). It also provides a generous 14px hit-test target for interactive seekbars/progress scrubbers without altering the 2px visual footprint.
  - **Progress / Seekbar Dynamic Dividers:**
    - Background Track: `Height="2"`, `CornerRadius="1"`, `Background="#14FFFFFF"`, `VerticalAlignment="Center"`.
    - Elapsed Fill: `Border` `Height="2"`, `CornerRadius="1"`, `Background="{Binding AccentBrush}"`, dynamically clipped via `RectangleGeometry`.
    - Scrubbing Pill Thumb: `Width="4"`, `Height="12"`, `CornerRadius="2"`, `Background="#FFFFFF"`, `BorderThickness="1"`, fades in to `Opacity="1"` on hover/drag.
  - **Dedicated Grid Row Separators (Top Toolbars, e.g. Notepad):**
    - `Border Grid.Row="1"` with `Height="2"`, `CornerRadius="1"`, `Background="#14FFFFFF"`, `HorizontalAlignment="Stretch"`, `Margin="0"`.
  - **Vertical Toolbar Dividers:** `Border Width="1"`, `Height="16"`, `Background="#1FFFFFFF"`, `Margin="6,0"`.
  - **Context Menu Separators:** Handled globally by `App.xaml` template (`Height="1"`, `Margin="8,3,8,3"`, `Background="#1FFFFFFF"`).
  - **Mandatory Anti-Patterns (NEVER DO THIS):**
    1. **NEVER** use WPF's default `<Separator />` inside widget surfaces.
    2. **NEVER** set `BorderThickness="0,1,0,0"` on `<Border Grid.Row="1">`.
    3. **NEVER** use opaque grey hex codes (`#333333`, `#444444`, etc.); always use `#14FFFFFF` (~8% white) to blend with Mica/Acrylic.
    4. **NEVER** indent horizontal separators; they must run 100% seamlessly edge-to-edge across the entire tile width (`Margin="0"`).
    5. **NEVER** show separators in 1-row compact mode (`SpanY <= 1`) — collapse them along with the docked tier.

- **Context Menu Purity:**
  - Widget right-click context menus must stay clean and minimal (Resize, Add to Group, Unpin).
  - Do NOT clutter menus with redundant "Effects" submenus or decorative toggles. Keep controls direct, sleek, and functional.

---

## 7. High-Performance Image Decoding & Memory Guidelines
- **NEVER copy files into in-memory `MemoryStream` buffers:**
  - Copying 4K/8K images into a `MemoryStream` causes massive allocations on the Large Object Heap (LOH) which triggers RAM spikes (60MB+).
  - Stream directly from `FileStream` into `BitmapImage` with `CacheOption = BitmapCacheOption.OnLoad` so the native WIC decoder decodes directly without duplicating raw file bytes in managed heap.
- **Always Bound Decode Dimensions at the Decoder Level:**
  - Tiles are at most 248px to 504px wide.
  - Inspect image dimensions with `BitmapCreateOptions.DelayCreation` and set `DecodePixelWidth` or `DecodePixelHeight` to a max bounding size (e.g. 640px). This reduces an uncompressed 4K frame from 33 MB down to < 1 MB.
- **Avoid WPF `BlurEffect` on Large Surfaces:**
  - WPF software/GPU `BlurEffect` allocates unmanaged render targets and shader passes that bloat memory and cause lag.
- **Living Ken Burns Motion:**
  - Photo live tiles MUST feature slow, cinematic Ken Burns pan and zoom motion via hardware-accelerated `RenderTransform` (`ScaleTransform` and `TranslateTransform`), bringing the tile alive rather than leaving it as a dead static image.

---

## 8. Build & Deployment Execution Protocol
- When compiling and publishing an update:
  - Publish the Release build cleanly to `C:\Users\HamB\Desktop\MetroHubApp`.
  - **DO NOT auto-launch the GUI application from the agent terminal / subshell.**
  - **Reason:** Spawning the GUI from developer console subshells causes IDE process tree inheritance, bloated .NET JIT/diagnostic working sets (~100MB instead of ~20MB), and UIPI conflicts that break global hotkeys (`RegisterHotKey(Ctrl + ~)`).
  - Inform the user that the publish is complete so they can launch it directly from their desktop folder in a clean Windows Explorer user session.

---

## 9. Universal Modal Dialog & Picker Protocol (Window Deactivation Prevention)
- MetroHub's `OnWindowDeactivated` automatically calls `HideScreen()` whenever the window loses focus, UNLESS `IsDialogOpen == true`.
- **MANDATORY PATTERN for ALL Dialogs & Pickers (`OpenFolderDialog`, `OpenFileDialog`, `SaveFileDialog`, etc.):**
  ```csharp
  var mainWindow = MainWindow.Current;
  if (mainWindow != null) mainWindow.IsDialogOpen = true;
  try
  {
      var dialog = new OpenFolderDialog { ... };
      bool? result = mainWindow != null ? dialog.ShowDialog(mainWindow) : dialog.ShowDialog();
      if (result == true) { ... }
  }
  finally
  {
      if (mainWindow != null)
      {
          mainWindow.IsDialogOpen = false;
          mainWindow.Activate();
      }
  }
  ```
- **Rules:**
  1. `IsDialogOpen = true` MUST be set BEFORE `ShowDialog()` to prevent premature window dismissal.
  2. `mainWindow` MUST be passed as the owner to `ShowDialog(mainWindow)` so the dialog is modal and anchored to MetroHub.
  3. In `finally`, `IsDialogOpen = false` must be reset AND `mainWindow.Activate()` invoked so keyboard/mouse focus returns cleanly to MetroHub.

---

## 10. Tile Resize Optimization & Dynamic Span Handling
- **Layout Thrashing Prevention:**
  - Widgets with multi-row features (such as lists or secondary headers) MUST listen to `model.PropertyChanged` for `SpanX` and `SpanY`.
  - When `SpanY <= 1`, instantly update `IsCompactMode = true` so the sub-layout collapses BEFORE or DURING the animation, rather than overflowing and forcing the WPF layout engine to re-measure deeply nested items during resize interpolation.
- **Uninterrupted Content Rendering:**
  - DO NOT animate tile content opacity to `0.0` on resize.
  - Animate width and height directly using `QuarticEase` (220ms) with `ClipToBounds="True"` on `RootBorder`.

---

## 11. Single-Instance Mutex & Process Lifecycle Hygiene
- MetroHub enforces a named single-instance mutex (`MetroHub_App_SingleInstance_Mutex`).
- If an orphaned background process is alive (e.g. from a previous build or crash), any newly launched instance will immediately exit after ~0.5 seconds upon detecting the existing mutex owner.
- Always ensure previous processes are cleanly terminated (`Stop-Process -Name MetroHub -Force`) before deploying or starting new builds.

---

## 12. Windows Media Session (GSMTC) Synchronization & Concurrency Hygiene
- **Never Rely Solely on Passive WinRT Events:** Windows GSMTC events (`PlaybackInfoChanged`, `TimelinePropertiesChanged`) are delivered asynchronously across arbitrary threadpool threads and can be delayed, dropped, or arrive out of order during player state transitions.
- **Active Authoritative State Query:** On every playback timer tick (250ms), actively check `session.GetPlaybackInfo()`. If `PlaybackStatus != Playing`, halt the timer immediately, set `IsPlaying = false`, and freeze position to prevent seekbar advancement while paused.
- **Freshness-Based Timeline Gate (LastUpdatedTime):** Use the OS-reported `timeline.LastUpdatedTime` as the **canonical freshness signal**. If an incoming timeline update has a `LastUpdatedTime` OLDER than the most recently accepted one, reject it as stale. This eliminates all classes of transient/out-of-order position glitches from Chromium.
- **Guard Against Chromium 0:00 Transient Resets:** Chromium/YouTube emits transient 0:00 position snapshots during pause, play, and seek transitions. Never overwrite known positions ($\ge 2.5\text{s}$) with $< 1.5\text{s}$ on the same track — reject and schedule a recovery poll.
- **Seek Recovery Window:** After any large position jump ($> 3\text{s}$), enter a 1.5s "seek recovery" window. During this window, every timer tick performs a full OS timeline query (instead of local extrapolation) and aggressive background polls (50ms, 100ms, 200ms, 400ms, 800ms delays) sample the OS to quickly converge on the real position.
- **Atomic Track Identity Resets:** Reset position to 0:00 atomically only when track identity (`Artist|Title|Album`) changes, ensuring new tracks start cleanly at 0:00 while mid-stream tracks remain protected. Also reset `_lastAcceptedOsUpdateTime` and `_seekRecoveryUntil`.
- **Optimistic Transport Locking:** Apply a 1.5s grace window on user play/pause toggles to prevent UI flickering while the OS processes asynchronous commands.
- **Thread Safety:** Lock state mutations under a synchronization lock (`_stateLock`) and marshal all WinRT event updates to the UI Dispatcher.


