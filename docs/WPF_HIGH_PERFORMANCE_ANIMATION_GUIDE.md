# MetroHub High-Performance WPF Animation & Mica Clipping Guide

This document captures the architectural solution for achieving **locked 100 FPS** translucent drawer animations and the **Mica Scissor Curtain** in WPF on integrated GPUs (such as AMD Radeon Vega 8 and Intel Iris Xe).

---

## The Core Challenge: Translucent Windows 11 Mica vs. Background Occlusion

In Windows 11 Fluent Design, slide-out drawers (like the "All Apps" drawer) often feature a transparent or translucent **Mica backdrop** (`Background="Transparent"`):
* The desktop wallpaper / Mica material shines through the drawer surface.
* **The Conflict**: Because the drawer is translucent, any canvas tiles, widgets, or footer text located underneath the drawer will bleed through and visually clash with the drawer's text and search interface.
* **Bad Solutions**:
  - *Dimming the whole canvas*: Destroys contrast and makes the screen look dirty.
  - *Vanishing tiles (card flips / opacity fades)*: Tiles under the drawer disappear while neighboring tiles remain, creating an unnatural, broken appearance.
  - *Software geometry animation (`RectAnimation`)*: Animating `RectangleGeometry.RectProperty` forces WPF's `milcore.dll` to re-tessellate vector geometry on the CPU every 10ms, dropping framerates from 100 FPS to under 30 FPS.

---

## The Breakthrough Architecture: The 100 FPS Scissor Curtain

### 1. Matrix Transform on Reusable Geometry
Instead of modifying `Rect` bounds on each frame:
1. Allocate **one** `RectangleGeometry` with an attached `TranslateTransform`.
2. Apply it to `MainContentAreaGrid.Clip` (the parent of both the tile canvas and the footer status bar).
3. Animate `TranslateTransform.XProperty` using `DoubleAnimation`.
4. In Direct3D, translating a 2D matrix sends a 4-float update directly to the GPU vertex pipeline. The geometry is **never re-tessellated on the CPU**.

```csharp
_canvasScissorTranslate = new TranslateTransform(0, 0);
_canvasScissorGeom = new RectangleGeometry
{
    Rect = new Rect(0, 0, clipWidth, clipHeight),
    Transform = _canvasScissorTranslate
};
MainContentAreaGrid.Clip = _canvasScissorGeom;

var anim = new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(durationMs))
{
    EasingFunction = easing
};
Timeline.SetDesiredFrameRate(anim, refreshRate);
_canvasScissorTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
```

### 2. Common Parent Clipping (Eliminating the Footer Bar Bleed)
* **Mistake**: Clipping only `ContentScrollViewer` leaves the bottom status bar (`FooterGrid`) unclipped. As a result, the bottom status bar text bleeds straight through the drawer, colliding with the drawer's app items.
* **Fix**: Apply the clip to `MainContentAreaGrid`, which encompasses both Row 0 (Canvas) and Row 1 (Footer Bar). Both are sliced off in exact unison.

### 3. Unified 100Hz Animation Clock (Zero Jitter)
* **Mistake**: If the drawer slide animation runs at WPF's default 60Hz while the scissor clip runs at 100Hz, the two animations phase-drift and leapfrog each other every few milliseconds, causing perceptible tearing and jitter.
* **Fix**: Both the drawer's `DrawerTranslate` animation and the scissor's `_canvasScissorTranslate` animation must use the exact same `Timeline.SetDesiredFrameRate(anim, refreshRate)` linked to the monitor's native refresh rate (e.g. 100Hz), along with matching durations (`220ms` / `180ms`) and easing curves (`CubicEase.EaseOut` / `CubicEase.EaseIn`).

### 4. GPU Hardware Texture Caching During Motion (`BitmapCache`)
* **Problem**: The All Apps drawer contains 200+ installed applications (~1,250 visual tree nodes). Moving 1,250 elements on every frame floods the UI thread and GPU fill-rate.
* **Fix**: Set `CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true }` during the slide.
  - The GPU bakes the drawer into **1 single hardware texture** once.
  - During the slide, the GPU simply translates this single texture quad across the screen with **0% CPU overhead**.
  - On `anim.Completed`, `CacheMode = null` is restored so text selection, ClearType font rendering, and scrolling remain native.

### 5. Fast Bilinear Icon Scaling & Deferred Focus
* Change app icon `BitmapScalingMode` from `HighQuality` (expensive bicubic filtering) to `LowQuality` (hardware bilinear filtering).
* Defer `SearchBox.Focus()` until `anim.Completed` to avoid Windows IME message loop initialization on frame 0.

---

## Results
* **Frame Rate**: Locked at **100 FPS** on AMD Radeon Vega 8 integrated APU.
* **Visuals**: Pure, uncompromised Windows 11 Mica backdrop behind the drawer.
* **Cohesion**: Zero tile vanishing acts, zero canvas dimming, zero text collisions.
