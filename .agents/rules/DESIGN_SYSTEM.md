# MetroHub Universal Design System & Standards Specification

This document establishes the single source of truth for all universal design measurements, visual tokens, geometry, scrollbar dimensions, typography, color palettes, and motion timings across MetroHub.

---

## 1. Universal Scrollbar & ScrollViewer Standards

MetroHub strictly uses the **Windows 11 Fluent Auto-Hiding Scrollbar System** across all views (Main Tile Canvas, All Apps Drawer, Search Results, and Catalog Widgets) to guarantee visual consistency and modern aesthetic excellence:

### Mandatory Auto-Hide Rule
> **CRITICAL RULE**: Scrollbars **MUST be invisible by default (`Opacity="0.0"`)**. They must **ONLY become visible (`Opacity="1.0"`) when actively scrolled or hovered**, and must automatically fade back to completely invisible after 1.2s of inactivity via `controls:AutoHideScrollBehavior.IsEnabled="True"`. Never leave scrollbars permanently visible on screen!

| Component / Property | Universal Value | Purpose / Behavior |
| :--- | :--- | :--- |
| **Default State** | **`Opacity="0.0"` (Invisible)** | Zero clutter; completely transparent when at rest |
| **Scroll / Active State** | **`Opacity="1.0"` (Revealed)** | Smoothly reveals upon scroll wheel, touch, or dragging |
| **Hover State** | **`Opacity="1.0"` (Revealed)** | Visible while mouse hovers directly over the scrollbar |
| **Auto-Hide Timeout** | **1200 ms** (1.2s) | Automatically triggers 300ms fade-out back to `Opacity="0.0"` |
| **Widget / Card Width** | **3 – 4 px** | Ultra-slim floating pill thumb for compact widgets/panels |
| **Canvas / Drawer Width** | **12 px** | Full drawer/canvas ergonomic width (8px thumb + 2px inset) |
| **Track Background** | `Transparent` (`#00000000`) | Seamless integration with Mica/Acrylic; zero grey gutter/boxes under all states |
| **Track Border** | `0 px` (None) | Eliminates legacy Win32 gutter borders |
| **Thumb Geometry** | Pill / Capsule | `CornerRadius="2"` (widgets) or `CornerRadius="4"` (canvas) |
| **Thumb Color (Rest)** | `#40FFFFFF` (~25% white) | Clean and visible over dynamic backdrops |
| **Thumb Color (Hover)** | `#90FFFFFF` (~55% white) | Bright interactive feedback on mouse hover (NO GREY) |
| **Thumb Color (Dragging)** | `#D0FFFFFF` (~80% white) | High-contrast visual lock during drag operations (NO GREY) |
| **Thumb Min Length** | **24 px** | Minimum grab target even in massive lists |
| **Scroll Bar Visibility** | `VerticalScrollBarVisibility="Auto"` | Only appears when content overflows the viewport |
| **Horizontal Visibility** | `HorizontalScrollBarVisibility="Disabled"` | Content wraps/stacks cleanly; no horizontal scrolling |
| **Implementation** | `controls:AutoHideScrollBehavior.IsEnabled="True"` | Applied directly to `ScrollViewer`, `ListView`, or `ListBox` |

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

## 9. Universal Separator & Hairline Divider System

> **COMPULSORY ARCHITECTURAL CONTRACT**: Every current and future widget in MetroHub **MUST AUTOMATICALLY ADHERE** to these separator specifications. Never freelance separator styling, colors, thicknesses, or container placements.

### 9.1 Universal Separator Specification Matrix

| Separator Type | Element & Geometry | Color / Brush | Position & Centering | Margin / Span |
| :--- | :--- | :--- | :--- | :--- |
| **Docked Tier Divider (Static)** | `Border` (`Height="2"`, `CornerRadius="1"`) | `#14FFFFFF` (~8% White) | Inside 14px container (`Grid.Row="1"`, `Margin="0,-7,0,0"`, `VerticalAlignment="Top"`) | Full Edge-to-Edge (`Margin="0"`) |
| **Docked Tier Divider (Progress/Seekbar)** | Track: `Height="2"`, `#14FFFFFF`<br/>Fill: `Height="2"`, `{Binding AccentBrush}`<br/>Thumb: `4x12px`, `CornerRadius="2"` | Background: `#14FFFFFF`<br/>Fill: Accent<br/>Thumb: `#FFFFFF` | Inside 14px container (`Grid.Row="1"`, `Margin="0,-7,0,0"`, `VerticalAlignment="Top"`) | Full Edge-to-Edge (`Margin="0"`) |
| **Dedicated Grid Row Separator** | `Border` (`Height="2"`, `CornerRadius="1"`) | `#14FFFFFF` (~8% White) | Standalone `<RowDefinition Height="Auto" />` between content tiers | Full Edge-to-Edge (`Margin="0"`) |
| **Vertical Toolbar / Button Divider** | `Border` (`Width="1"`, `Height="16"`) | `#1FFFFFFF` (~12% White) | `VerticalAlignment="Center"`, centered between button groups | `Margin="6,0"` |
| **Context Menu Separator** | Global `<Separator />` template | `#1FFFFFFF` (~12% White) | `Height="1"`, `SnapsToDevicePixels="True"` | `Margin="8,3,8,3"` |

---

### 9.2 Docked Bottom Tier Layout & Boundary Specification
Widgets with multi-tier sections (such as Focus Timer, Media Player, and Volume Widget) must use the standardized docked bottom container pattern:
- **Grid Row Definitions**:
  ```xml
  <Grid.RowDefinitions>
      <!-- Row 0: Top Primary Content / Hero Tier -->
      <RowDefinition Height="*" />
      <!-- Row 1: Bottom Docked Controls / Transport Tier -->
      <RowDefinition Height="Auto" />
  </Grid.RowDefinitions>
  ```
- **Docked Container Styling**:
  ```xml
  <Border Grid.Row="1"
          Background="#14000000"
          BorderThickness="0"
          CornerRadius="0,0,2,2"
          SnapsToDevicePixels="True"
          Padding="20,0,20,0">
  ```
- **Key Rules**:
  - `Background="#14000000"` (~8% black tint) gives subtle depth over Mica/Acrylic.
  - `BorderThickness="0"` is **mandatory**: Never set `BorderThickness="0,1,0,0"` on the docked container.
  - `CornerRadius="0,0,2,2"` matches the tile's bottom rounded corners.
  - `Padding="20,0,20,0"` establishes the universal 20px horizontal alignment gutter.

---

### 9.3 Horizontal Edge-to-Edge Static Hairline Divider (The Golden Standard)
For widgets requiring a clean visual boundary separation between top content and bottom controls:
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
- **Geometry**: `Height="2"`, `CornerRadius="1"` (creates a sleek subpixel hairline with soft rounded ends).
- **Color**: `#14FFFFFF` (~8% opacity translucent white). Never use solid grey or opaque brushes.
- **Subpixel Centering Math**:
  - The outer `Grid` has `Height="14"` and `VerticalAlignment="Top"`.
  - Setting `Margin="0,-7,0,0"` offsets the container upward by half its height ($14 / 2 = 7\text{px}$).
  - The inner `Border` with `VerticalAlignment="Center"` and `Height="2"` is centered at $Y = 7\text{px}$ within the container.
  - With the -7px offset, the 2px hairline's vertical center lands at **precisely $Y = 0\text{px}$** (the exact mathematical seam between Row 0 and Row 1). 1px rests in Row 0, and 1px rests in Row 1.
- **Edge-to-Edge Span**: Must span the entire width of the card (`Margin="0"` left and right, `HorizontalAlignment="Stretch"`). Never indent horizontal dividers.

---

### 9.4 Horizontal Edge-to-Edge Dynamic Progress / Seekbar Separator
For widgets that display elapsed progress or allow scrubbing (such as Focus Timer and Media Player):
```xml
<!-- Edge-to-Edge Hairline Progress / Seekbar running along Row 1 top boundary -->
<Grid Grid.Row="1"
      VerticalAlignment="Top"
      Height="14"
      Margin="0,-7,0,0"
      Background="Transparent"
      Cursor="Hand"
      Tag="InteractiveControl"
      SnapsToDevicePixels="True">

    <!-- 1. Background Hairline Track (Universal Fluent #14FFFFFF) -->
    <Border Height="2"
            VerticalAlignment="Center"
            Background="#14FFFFFF"
            CornerRadius="1"
            SnapsToDevicePixels="True" />

    <!-- 2. Elapsed Progress / Seek Fill: Accent Color Bar -->
    <Border Height="2"
            HorizontalAlignment="Stretch"
            VerticalAlignment="Center"
            Background="{Binding AccentOrSeekbarBrush}"
            CornerRadius="1"
            SnapsToDevicePixels="True">
        <Border.Clip>
            <!-- Clipped dynamically in code-behind / SizeChanged -->
            <RectangleGeometry Rect="0,-5,0,14" />
        </Border.Clip>
    </Border>

    <!-- 3. Scrubbing Pill Thumb (Appears on Hover/Drag) -->
    <Border Width="4"
            Height="12"
            CornerRadius="2"
            Background="#FFFFFF"
            BorderBrush="{Binding AccentOrSeekbarBrush}"
            BorderThickness="1"
            HorizontalAlignment="Left"
            VerticalAlignment="Center"
            Opacity="0"
            SnapsToDevicePixels="True"
            IsHitTestVisible="False" />
</Grid>
```
- **Hit-Test Ergonomics**: The 14px container (`Height="14"`, `Background="Transparent"`, `Tag="InteractiveControl"`, `Cursor="Hand"`) provides a generous 14px click/scrub hit-target while maintaining an ultra-sleek 2px visual footprint. Users never have to hunt for a 2px sliver.
- **Track & Fill**: Both the background track and progress fill are `Height="2"` with `CornerRadius="1"`.
- **Pill Thumb**: `Width="4"`, `Height="12"`, `CornerRadius="2"`, `Background="#FFFFFF"`. Smoothly transitions to `Opacity="1"` on mouse hover or drag.

---

### 9.5 Dedicated Grid Row Separator (Top Toolbars & Multi-Tier Content)
When a widget features a top-mounted toolbar (e.g. Notepad / Notes) where the divider sits between Row 0 (toolbar) and Row 2 (editor/canvas):
```xml
<Grid.RowDefinitions>
    <RowDefinition Height="Auto" />  <!-- Row 0: Top Toolbar -->
    <RowDefinition Height="Auto" />  <!-- Row 1: Dedicated Separator Row -->
    <RowDefinition Height="*" />     <!-- Row 2: Main Content Area -->
</Grid.RowDefinitions>

<!-- ROW 1: Dedicated Border-to-Border Hairline Separator -->
<Border Grid.Row="1"
        Height="2"
        HorizontalAlignment="Stretch"
        Background="#14FFFFFF"
        CornerRadius="1"
        Margin="0"
        SnapsToDevicePixels="True" />
```
- Exactly identical geometry (`Height="2"`, `CornerRadius="1"`) and color (`#14FFFFFF`).
- Runs border-to-border (`Margin="0"`).

---

### 9.6 Vertical Button Group & Toolbar Dividers
When separating groups of buttons or toolbar sections vertically:
```xml
<Border Width="1"
        Height="16"
        VerticalAlignment="Center"
        Background="#1FFFFFFF"
        Margin="6,0"
        SnapsToDevicePixels="True" />
```
- **Width & Height**: `Width="1"`, `Height="16"`, `VerticalAlignment="Center"`.
- **Color**: `#1FFFFFFF` (~12% translucent white).
- **Margins**: `Margin="6,0"` (6px breathing room on both sides).

---

### 9.7 Context Menu Separators
Context menus across the entire application automatically inherit the global style from `App.xaml`:
```xml
<Style TargetType="{x:Type Separator}">
    <Setter Property="OverridesDefaultStyle" Value="True" />
    <Setter Property="SnapsToDevicePixels" Value="True" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type Separator}">
                <Border Height="1" Margin="8,3,8,3" Background="#1FFFFFFF" SnapsToDevicePixels="True" />
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```
- Height: `1px`.
- Margin: `8,3,8,3` (8px horizontal indent from menu edges, 3px vertical spacing).
- Background: `#1FFFFFFF` (~12% translucent white).

---

### 9.8 Mandatory Separator Rules & Anti-Patterns (NEVER DO THIS)
1. **NEVER use WPF's default `<Separator />` inside widget surfaces:**
   - WPF's unstyled `<Separator />` draws a legacy 1px blurry grey box with system margins and subpixel rendering artifacts.
2. **NEVER use `BorderThickness="0,1,0,0"` on the docked container:**
   - Setting a top border directly on `<Border Grid.Row="1">` causes clipping at tile boundaries, subpixel bleeding, and inconsistent corner blending.
3. **NEVER use opaque or grey hex values (`#333333`, `#444444`, `#808080`, etc.):**
   - MetroHub tiles sit on dynamic Windows 11 Mica and Acrylic backdrops. Always use translucent white (`#14FFFFFF` for horizontal tile dividers, `#1FFFFFFF` for vertical/menu dividers) so dividers adapt dynamically to any background tint.
4. **NEVER indent or add horizontal margins to horizontal tile dividers:**
   - Separators must always span 100% seamlessly edge-to-edge from the extreme left border to the extreme right border of the tile (`Margin="0"`).
5. **NEVER omit `SnapsToDevicePixels="True"`:**
   - Subpixel hairline rendering requires pixel snapping to remain razor-sharp across 100%, 125%, 150%, and 200% Windows display scaling factors.
6. **NEVER display hairline separators in 1-Row Compact Modes (`SpanY <= 1`):**
   - When a tile is resized to 1-row height (64px), separators and docked containers MUST be collapsed (`Visibility="Collapsed"`). Hairline separators are strictly for multi-row widgets.

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

---

## 15. Universal Symmetrical 20px Widget Grid & Control Alignment Standard

> **COMPULSORY ARCHITECTURAL CONTRACT**: Every current and future widget in MetroHub (Media Player, Focus Timer, Photo Stream, Weather, Volume, Notes, Clock, etc.) **MUST AUTOMATICALLY ADHERE** to this symmetrical grid system. Never freelance margins, button paddings, or icon scales on new widgets.

### 15.1 The 20px Symmetrical Clearance Rule
All widgets must enforce an exact, symmetrical **20px margin from both the left and right tile boundaries**:
- **Left Margin from Border:** Exactly `20px`.
- **Right Margin from Border:** Exactly `20px`.

### 15.2 Left-Edge Optical Alignment Axis (X = 20px)
- **Top Row Header Content:** The primary text block (Track Title, Focus Phase Title, Widget Heading) must declare `Margin="20,0,..."`, starting precisely at `X = 20px`.
- **Bottom Docked Bar Transport Controls:** The button group `StackPanel` inside `<Border Grid.Row="1" ... Padding="20,0,20,0">` **MUST declare `Margin="-9.5,0,0,0"`**:
  ```xml
  <Border Grid.Row="1"
          Background="#14000000"
          BorderThickness="0"
          CornerRadius="0,0,2,2"
          SnapsToDevicePixels="True"
          Padding="20,0,20,0">
      <Grid>
          <!-- Left: Transport Controls (Aligned with Title at X = 20px) -->
          <StackPanel Orientation="Horizontal"
                      HorizontalAlignment="Left"
                      VerticalAlignment="Center"
                      Margin="-9.5,0,0,0">
              <!-- First button (Previous, Reset, etc.) starts at X = 20px -->
  ```
  - **The Mathematical Rationale:** The standard transparent button has `Width="32"` and centers its `13px` icon glyph (`(32 - 13) / 2 = 9.5px`). Setting `Margin="-9.5,0,0,0"` offsets the button's internal padding so that the first visible icon glyph (e.g. `<` chevron or `↺` reset) begins at **precisely X = 20px**, creating an unbroken, pixel-perfect vertical alignment line straight down from the first letter of the title text.

### 15.3 Right-Edge Optical Alignment Axis (X = Width - 20px)
- **Top Row Hero Visuals:** Primary visual elements (Album Cover Art, Circular Progress Ring Gauge, Hero Cards) **MUST declare `Margin="0,0,20,0"`**:
  ```xml
  <Grid HorizontalAlignment="Right"
        VerticalAlignment="Center"
        Margin="0,0,20,0">
      <!-- Album Art or Circular Gauge terminates flush at X = Width - 20px -->
  </Grid>
  ```
- **Bottom Docked Bar Secondary Elements:** Live readouts (Playback timing `3:05 / 5:21`, status indicators, cycle tracker dots) must terminate at **precisely X = Width - 20px**:
  ```xml
  <!-- Live Playback Time Readout: Matches 28px height footprint and aligns flush right -->
  <Border HorizontalAlignment="Right"
          VerticalAlignment="Center"
          Height="28"
          SnapsToDevicePixels="True">
      <TextBlock Text="{Binding TimeDisplayString}"
                 VerticalAlignment="Center"
                 HorizontalAlignment="Right"
                 FontSize="13"
                 FontWeight="Normal"
                 FontFamily="Segoe UI Variable Text, Segoe UI, sans-serif"
                 Foreground="#D0FFFFFF"
                 SnapsToDevicePixels="True" />
  </Border>
  ```
- **Indicator Dot Margins:** When using multi-dot progress indicators (e.g. Cycle Tracker Dots with `Margin="2.5,0"`), the container `StackPanel` **MUST declare `Margin="0,0,-2.5,0"`** so that the rightmost edge of the final dot aligns flush with the 20px line.

### 15.4 Transport Button Style & Play/Pause Symmetry Standard
All widgets with media or timer playback controls must share the identical button footprint:
- **Button Footprint:** `Width="32"`, `Height="28"`, `CornerRadius="2"`, `Background="Transparent"`, `Margin="4,0"`.
- **Hover Feedback:** `Background="#25FFFFFF"`, `Foreground="#FFFFFF"`.
- **Play Icon:** `FontSize="13"`, `Text="&#xE768;"`, `Margin="1.5,0,0,0"` (optical centering adjustment).
- **Pause Icon:** `FontSize="15.5"`, `Text="&#xE769;"`, `Margin="0"` (scaled to 15.5px so its optical height and visual weight match adjacent 13px chevrons / skip buttons).

### 15.5 Timing & Numerical Readout Containers
- **Height Footprint:** Must be wrapped in a container with `Height="28"` and `VerticalAlignment="Center"` to match the 28px height and vertical center axis of the adjacent transport buttons.
- **Typography:** `FontSize="13"`, `FontFamily="Segoe UI Variable Text, Segoe UI, sans-serif"`, `Foreground="#D0FFFFFF"`.

### 15.6 Right-Click Context Menu Purity
- **Minimalist Standard:** Right-clicking any widget tile must show only universal tile actions (Resize, Add to Group, Unpin).
- **Zero Effect Bloat:** Never inject redundant "Effects", glow mode toggles, or decorative gimmick submenus into tile context menus. Keep controls clean, direct, and distraction-free.



