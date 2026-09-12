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
