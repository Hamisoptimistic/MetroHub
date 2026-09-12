# MetroHub Universal Design System & Standards Specification

This document establishes the single source of truth for all universal design measurements, visual tokens, geometry, scrollbar dimensions, typography, color palettes, and motion timings across MetroHub.

---

## 1. Universal Scrollbar & ScrollViewer Standards

MetroHub strictly uses the **Windows 11 Fluent / WPF-UI Universal Scrollbar System** across all views (Main Tile Canvas, All Apps Drawer, and Search Results) to guarantee visual consistency and ergonomic usability:

| Component / Property | Universal Value | Purpose / Behavior |
| :--- | :--- | :--- |
| **Scrollbar Total Width** | **12 px** | Comfortable ergonomic width; 8px pill thumb with 2px padding |
| **Track Background** | `Transparent` (`#00000000`) | Seamless integration with Mica and Acrylic backdrops; zero grey gutter/boxes under all states (hover, dragging, rest) |
| **Track Border** | `0 px` (None) | Eliminates legacy Win32 gutter borders |
| **Thumb Geometry** | Pill / Capsule (`CornerRadius="4"`) | Modern Fluent aesthetic |
| **Thumb Color (Rest)** | `#40FFFFFF` (~25% opacity white) | Clean and visible over dynamic backdrops |
| **Thumb Color (Hover)** | `#80FFFFFF` (~50% opacity bright white) | Interactive feedback on mouse hover (NO GREY) |
| **Thumb Color (Dragging)** | `#B3FFFFFF` (~70% opacity active white) | High-contrast visual lock during drag operations (NO GREY) |
| **Thumb Min Length** | **24 px** | Minimum grab target even in massive lists |
| **Scroll Bar Visibility** | `VerticalScrollBarVisibility="Auto"` | Only appears when content overflows the viewport |
| **Horizontal Visibility** | `HorizontalScrollBarVisibility="Disabled"` | Content wraps/stacks cleanly; no horizontal scrolling |

---

## 2. Window & Shell Geometry

MetroHub adopts an authentic, borderless Metro-Fluent full-workarea design:

| Element | Measurement | Specification |
| :--- | :--- | :--- |
| **Window Corner Preference** | `DWMWCP_DONOTROUND` (`0 px`) | Square, sharp corners docked cleanly to the active display work area |
| **Window Border Color** | `DWMWA_COLOR_NONE` (`0xFFFFFFFE`) | Suppresses Windows 11 default 1px accent border |
| **Window Border Thickness** | `0 px` | Borderless chrome (`WindowStyle="None"`, `BorderThickness="0"`) |
| **Window Background** | `Transparent` | Passes composition directly to Windows 11 DWM Mica/Acrylic pipeline |
| **DWM System Backdrop** | Mica (`DWMSBT_MAINWINDOW`) or Acrylic (`DWMSBT_TRANSIENTWINDOW`) | Real-time backdrop sampling with dark mode enforced (`DWMWA_USE_IMMERSIVE_DARK_MODE`) |
| **Screen Snap Mode** | Monitor Work Area (`rcWork`) | Sized dynamically to active monitor bounds minus taskbar |

---

## 3. Corner Radii Hierarchy (`CornerRadius`)

Corner radius values are structured to balance Metro UI's clean geometric modernism with Fluent Design softness:

| UI Element | Corner Radius | Description |
| :--- | :--- | :--- |
| **Outer Window** | `0` | Sharp screen docking |
| **Sidebar Rail** | `0` | Clean vertical edge |
| **All Apps Drawer Dock** | `0` | Clean left-anchored slide-out container |
| **Tiles (Small, Med, Wide, Large)** | `2 px` | Modernized Metro sharp tiles with subtle 2px rounding |
| **Tile Selection / Focus Ring** | `2 px` | Matches tile bounds |
| **Group Header Badges** (Letter boxes) | `2 px` | Compact alphabet markers (`#14FFFFFF` with `#1CFFFFFF` border) |
| **Search Input Box** | `4 px` | Friendly, tactile input field |
| **App Item Row Hover State** | `4 px` | Clean list item highlight |
| **Context Menu Container** | `8 px` | Floating Fluent popover border |
| **Context Menu Items** | `4 px` | Hover highlight within menus |
| **Dialog Modals** | `8 px` | Elevated modal dialog surfaces |

---

## 4. Border Thickness & Stroke Standards

| UI Element | Border Thickness | Border Brush / Color |
| :--- | :--- | :--- |
| **Outer Window** | `0 px` | `Transparent` (DWM suppressed) |
| **Tile (Default)** | `0 px` | Borderless (pure tile background) |
| **Tile (Selected / Hover Outline)** | `1.5 px` | Accent brush or `#4DFFFFFF` |
| **Search Box Input** | `1 px` | `#2AFFFFFF` (Rest) / `AccentBrush` (Focused) |
| **Group Header Badges** | `1 px` | `#1CFFFFFF` |
| **Context Menu Border** | `1 px` | `ContextMenuBorderBrush` (`#24FFFFFF`) |
| **Sidebar Divider / Right Shadow** | `0 px` border | Soft linear gradient drop shadow (`#33000000` to `#00000000`) |

---

## 5. Tile Canvas Grid Dimensions & Metrics

MetroHub uses a mathematical 4-column base unit system (`GridPlacementService`):

| Metric | Measurement | Description |
| :--- | :--- | :--- |
| **Base Unit (Cell)** | `60 px` | Unit cell size for a 1x1 small tile |
| **Grid Cell Gap** | `4 px` | Spacing between adjacent tiles |
| **Small Tile (1x1)** | `60 px × 60 px` | Compact square tile |
| **Medium Tile (2x2)** | `124 px × 124 px` | Standard square tile (`60*2 + 4`) |
| **Slim Wide Tile (4x1)** | `252 px × 60 px` | Ultra-compact single-row bar (`60*4 + 4*3`) |
| **Slim Banner Tile (8x1)** | `508 px × 60 px` | Full-width single-row hero bar (`60*8 + 4*7`) |
| **Wide Tile (4x2)** | `252 px × 124 px` | Rectangular tile (`60*4 + 4*3`) |
| **Large Tile (4x4)** | `252 px × 252 px` | Large square tile (`60*4 + 4*3`) |
| **Group Column Spacing** | `24 px` | Horizontal gutter between tile groups |
| **Header Height** | `64 px` | Minimalist top bar containing title & search |
| **Footer Height** | `40 px` | Status bar with keyboard shortcut guide |

---

## 6. Typography & Text Standards

MetroHub uses Windows 11's **Segoe UI Variable** type ramp with ClearType rendering:

| Usage | Font Family | Size | Weight | Color / Opacity |
| :--- | :--- | :--- | :--- | :--- |
| **Primary System Font** | `Segoe UI Variable Text, Segoe UI` | `14 px` | Normal | `#FFFFFFFF` |
| **Group / Category Title** | `Segoe UI Variable Display, Segoe UI` | `18 px` | SemiBold | `#FFFFFFFF` |
| **Tile Label** | `Segoe UI Variable Text, Segoe UI` | `12 px` | SemiBold | `#FFFFFFFF` (with drop shadow) |
| **Alphabet Badge Header** | `Segoe UI Variable Display` | `14 px` | Bold | System Accent Color |
| **All Apps Item Title** | `Segoe UI Variable Text` | `13 px` | Normal | `#E6FFFFFF` (90% white) |
| **Search Placeholder** | `Segoe UI Variable Text` | `13 px` | Normal | `#70FFFFFF` (44% white) |
| **Status Bar Hints** | `Segoe UI Variable Text` | `11 px` | Normal | `#60FFFFFF` (37% white) |

---

## 7. Color Tokens & Translucent Tint Values

| Token | Hex / Alpha | Usage |
| :--- | :--- | :--- |
| **Acrylic Obsidian Tint** | `#990D0D11` | Root Grid tint overlay when Acrylic backdrop is active |
| **Drawer Acrylic Tint** | `#CC111116` | All Apps slide-out drawer backdrop |
| **Sidebar Rail Tint** | `#0DFFFFFF` (5%) | Subtly differentiated left icon rail |
| **Subtle Hover Fill** | `#1AFFFFFF` (10%) | List item, button, and menu hover highlight |
| **Subtle Pressed Fill** | `#26FFFFFF` (15%) | Button and tile click engagement |
| **Accent Primary** | System Accent Brush | Active indicators, focus rings, group badges |
| **Dimmed Overlay** | `#40000000` (25%) | Canvas dimming during All Apps Drawer expansion |

---

## 8. Motion & Animation Timings

| Animation | Duration | Easing Function | Description |
| :--- | :--- | :--- | :--- |
| **Window Entrance** | `180 ms` | `CubicEase (EaseOut)` | Smooth rise (20px to 0) + opacity fade (0 to 1) |
| **Window Dismiss** | `130 ms` | `CubicEase (EaseIn)` | Smooth drop (0 to 20px) + quick fade (1 to 0) |
| **All Apps Drawer Slide-In** | `220 ms` | `CubicEase (EaseOut)` | Horizontal slide from -320px to 0px |
| **All Apps Drawer Slide-Out** | `160 ms` | `CubicEase (EaseIn)` | Horizontal slide from 0px to -320px |
| **Context Menu Entrance** | `160 ms` | `CircleEase (EaseOut)` | Subtle 12px glide + opacity fade |
| **Tile Resize Transition** | `220 ms` | `QuarticEase (EaseOut)` | Non-blocking dimensional glide with zero content black-out |
| **Inline Control Cross-Fade** | `120 ms / 250 ms` | `CubicEase (EaseOut)` | Quick 120ms enter, smooth 250ms exit for inline value readouts |

---

## 9. Widget Edge-to-Edge Hairline Dividers & Docked Containers

MetroHub widgets with multi-tier sections (such as Focus Timer and Volume Widget) must use the standardized edge-to-edge hairline separator and docked bottom container pattern:

### 9.1 Docked Container Specification
- **Row Grid Assignment:** `Grid.Row="1"`
- **Background Fill:** `#14000000` (~8% black tint)
- **Border:** `BorderThickness="0"` (Never draw top border directly on the container to avoid WPF subpixel arc/border rendering artifacts)
- **Corner Radius:** `CornerRadius="0,0,2,2"` (Blends with tile's bottom 2px corners)
- **Padding:** `Padding="14,14,14,12"` (Standard 14px horizontal gutter; ample breathing room below the divider)

### 9.2 Edge-to-Edge Hairline Divider Track
The divider runs completely edge-to-edge with subpixel centering on the row boundary:
```xml
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
- **Track Height & Geometry:** `Height="2"`, `CornerRadius="1"`, `Background="#14FFFFFF"` (~8% white).
- **Subpixel Centering:** The 14px container with `Margin="0,-7,0,0"` places the vertical center exactly at Y=0 (the boundary between Row 0 and Row 1).

---

## 10. Minimalist Auto-Hiding Internal List Scrollbar (`MinimalScrollViewerStyle`)

For nested scrollable content inside widgets (e.g. Device Selector list, Session Mixers):

| Property | Value | Rationale |
| :--- | :--- | :--- |
| **Thumb Width** | **3 px** (Half-width) | Minimizes visual intrusion and eliminates collisions with list item cards |
| **Thumb Corner Radius** | `1.5 px` | Pill geometry |
| **Rest Opacity** | `0.0` (Completely Hidden) | Clean, uncluttered default view; zero permanent vertical lines |
| **Active / Hover Opacity** | `0.85` | Fades in smoothly (`200ms`) upon user scroll or mouse hover |
| **Idle Fade-Out Duration** | `350ms` | Gracefully hides after interaction stops |
| **Content Right Margin** | `8 px` (`Margin="0,0,8,0"`) | Leaves 8px clearance between list card highlights and the scroll track |

---

## 11. Inline Control State Feedback (Floating Tooltip Replacement)

**Rule: NEVER use floating tooltip bubble popups for live scrubbing feedback.**

- Floating tooltips are visually jarring, obscure surrounding tile content, and break aesthetic consistency across widgets.
- **The MetroHub Standard (Inline Cross-Fade):**
  - Live feedback is displayed directly inside adjacent interactive elements (e.g. the Mute / Action button next to a slider).
  - While dragging or scrolling:
    1. The resting icon (e.g. speaker glyph) smoothly fades out (`Opacity = 0.0` in `120ms`).
    2. The live numeric readout (e.g. `76%`, `11.5pt SemiBold`) smoothly fades in (`Opacity = 1.0` in `120ms`).
  - Upon release: A decay timer (`650ms` on drag release, `750ms` on wheel scroll) holds the readout, then smoothly cross-fades back to the resting icon (`250ms`).
  - Button width must be bounded (e.g. `MinWidth="34"`) to guarantee zero horizontal layout shifting during numeric transitions.

---

## 12. Tile Resize Performance & Smoothness Guidelines

1. **Zero Content Fade Flash:** Never drop `TileContentPresenter` opacity to `0.0` during tile resizing. Content must remain visible and smoothly resize in place.
2. **Instant Compact Mode Switching:** ViewModels with dynamic layouts (e.g. `IsCompactMode`) must subscribe to `TileModel.PropertyChanged` for `SpanX` and `SpanY`. When resized to 1-row height (`SpanY <= 1`), secondary elements must collapse immediately to prevent WPF layout engine thrashing.
3. **Quartic Dimensional Interpolation:** Use `QuarticEase (EaseOut)` with `220ms` duration for tile dimensional transitions, enforcing `ClipToBounds="True"` on `RootBorder` to prevent temporary child element overflow.

---

## 13. Compact Mode (1-Row) Single Strip Standards (`4x1` & `8x1`)

For widgets that support 1-row heights (such as `WidgetSize.CompactBanner` `4x1` and `WidgetSize.SlimBanner` `8x1`):

1. **Zero Hairline Separators in 1-Row Modes:**
   - When `SpanY <= 1`, hairline dividers and docked secondary containers (`Grid.Row="1"`) **MUST be completely collapsed** (`Visibility="Collapsed"`).
   - Under no circumstances should an edge hairline or secondary tier appear in a 64px tall tile.
2. **RowSpan Centering:**
   - The primary master element strip (`Grid.Row="0"`) must dynamically set `Grid.RowSpan="2"` when in compact mode (`SpanY <= 1`).
   - This ensures the master control strip is vertically centered with exact optical balance across the full 64px card height.
3. **Allowed Size Declarations:**
   - Widgets providing a slim single-strip tier must declare `WidgetSize.CompactBanner` (`4x1`) and/or `WidgetSize.SlimBanner` (`8x1`) in `AllowedSizes`.

---

## 14. Windows Media Session Synchronization Protocol (GSMTC)

To guarantee bulletproof synchronization with Windows System Media Transport Controls (Chromium, YouTube, Spotify, VLC):

1. **Guard Against Chromium Transient 0:00 Resets:**
   - When pausing or resuming mid-stream, Chromium dispatches transient events resetting `timeline.Position` to `0.00` or `0.01`.
   - **Rule:** If the track has duration $> 5\text{s}$ and known position $\ge 2.5\text{s}$, any sudden drop to $< 1.5\text{s}$ on the same track MUST be rejected as transient **regardless of whether playback is active or paused**.
   - Retain the last known valid position and initiate verification polls until Chromium stabilizes.
2. **Track Identity Tracking:**
   - Maintain a composite track identifier (`$"{Artist}|{Title}|{Album}"`).
   - When a track change is detected, reset position to `0:00` and clear all glitch suppression to allow new tracks to start at zero cleanly.
3. **Active OS State Polling on Every Tick (250ms):**
   - Do NOT rely solely on passive OS events or local stopwatch extrapolation.
   - On every 250ms tick of the playback timer, directly query `session.GetPlaybackInfo()`.
   - If `PlaybackStatus != Playing`, immediately halt the timer, set `IsPlaying = false`, and freeze position in place.
4. **Periodic 1-Second OS Timeline Sync:**
   - Every 4th tick (~1000ms), query `session.GetTimelineProperties()` to catch skips, external seeks, and ad transitions without drift.
5. **Optimistic Transport Locking:**
   - When user clicks Play/Pause, apply an optimistic target state with a 1.5s grace window to provide instant UI feedback without bouncing.
6. **Thread Synchronization & Epoch Tokens:**
   - Marshal WinRT callbacks onto the UI Dispatcher under a dedicated synchronization lock (`_stateLock`).
   - Use a monotonic `_updateEpoch` counter so delayed thumbnail loads or background tasks never overwrite newer state.


