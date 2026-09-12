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
