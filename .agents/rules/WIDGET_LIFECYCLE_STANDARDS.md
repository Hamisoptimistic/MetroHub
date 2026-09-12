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

## 6. Widget UI & Visual Consistency Standards (Transport Controls & Aesthetics)
- **Transport Buttons (`<`, `>`, Play, Pause, Folder, etc.):**
  - **NEVER** use boxed buttons with hard outline borders (`BorderThickness="1"`), solid grey backgrounds, or boxy frames.
  - **MUST** match the sleek Media Player standard (`MediaTransportButtonStyle` pattern):
    - `Background="Transparent"`
    - `BorderThickness="0"`
    - `Foreground="#E5E9F0"`
    - `Width="32"`, `Height="28"`, `CornerRadius="2"`
    - On hover (`IsMouseOver="True"`): `Background="#25FFFFFF"`, `Foreground="#FFFFFF"`
    - On pressed: `Background="#45FFFFFF"`
- **Clean Content-First Overlays:**
  - Do NOT clutter live visual surfaces with arbitrary wallpaper/filename text blocks. Keep live surfaces pure, photographic, and focused on the artwork.
  - Show minimal, elegant metadata with non-intrusive typography.
- **Universal 14px Alignment Standard:**
  - ALL bottom widget controls and transport bars MUST use 14px edge margin/padding (`Padding="14,0,14,0"` or `Margin="14,0,0,10"`).
  - Left-aligned transport buttons (Media Player, Pomodoro, and Photo Stream) MUST start at exactly 14px from the left tile edge.
  - This guarantees an identical horizontal baseline and left-margin alignment across all widgets on the dashboard.

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
