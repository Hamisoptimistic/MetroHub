# MetroHub Settings Architecture and Feature Catalog

This document defines the architecture, component structure, navigation flow, categorization system, and exact feature catalog for the MetroHub Settings system.
It incorporates the **Option C (Clean Hybrid Component Architecture)**, maximizing codebase reuse and organizing all settings around the user's mental model.

---

## 1. Core Architecture Principles

The settings engine follows a decoupled, three-tier architecture:

```
┌────────────────────────────────────────────────────────────────────────┐
│                          View Layer (XAML)                             │
│   SettingsWindow (960x640 Frosted Acrylic Shell)                       │
│   ├── State 1: SettingsHomeView (9-Card Category Grid)                 │
│   └── State 2: SettingsDetailLayout (Two-Pane Rail + Scroll Viewport)  │
└───────────────────────────────────▲────────────────────────────────────┘
                                    │ DataBinding
┌───────────────────────────────────┴────────────────────────────────────┐
│                           ViewModel Layer                              │
│   SettingsShellViewModel (Navigation / CurrentView Orchestrator)       │
│   ├── SettingsHomeViewModel                                            │
│   ├── SettingsDetailLayoutViewModel (Left Rail + Content Host)         │
│   └── 9 Page ViewModels (General, Appearance, Workspaces, etc.)        │
└───────────────────────────────────▲────────────────────────────────────┘
                                    │ Invocations
┌───────────────────────────────────┴────────────────────────────────────┐
│                           Service Layer                                │
│   SettingsService (Single Source of Truth)                             │
│   ├── In-Memory AppSettings Model                                      │
│   ├── Dynamic Token Synchronizer (Theme, Accent, & Motion)             │
│   ├── SettingChanged Event Dispatcher                                  │
│   └── Debounced Disk Persistence (StorageService, 400ms)               │
└────────────────────────────────────────────────────────────────────────┘
```

### Key Architectural Decisions:
1. **Option C: Clean Hybrid Component Architecture (No Reinventing the Wheel)**:
   * **Reuses existing global control styles from `ControlStyles.xaml`**:
     * `ui:ToggleSwitch` automatically inherits `FluentAccentToggleSwitchStyle` (Windows accent track + solid black/silver knob).
     * `ComboBox` automatically inherits `FluentGlassComboBoxStyle` (2px frosted dark-glass frame + vertical accent selection pill).
     * `TextBox` automatically inherits `MetroInputStyle`.
     * Multi-segment buttons (e.g. °C/°F, 12h/24h) reuse `MetroSegmentedTabStyle` (`ListBox` pill selector).
     * All scroll views inherit `WidgetFloatingScrollViewerStyle` (4px floating, auto-hiding scrollbar).
     * Buttons reuse `AccentButtonStyle`, `DefaultButtonStyle`, and `SubtleButtonStyle`.
   * **Consolidated Card Styles in `ControlStyles.xaml`**:
     * Rather than scattering new loose files, reusable styles for `ui:CardControl`, `ui:CardExpander`, and `ui:CardAction` (`MetroSettingsCardStyle`, `MetroSettingsExpanderStyle`, `MetroSettingsActionCardStyle`) reside directly in `src/MetroHub/Presentation/Themes/ControlStyles.xaml`.
     * Default implicit styles `<Style TargetType="{x:Type ui:CardControl}" BasedOn="{StaticResource MetroSettingsCardStyle}" />` ensure any card automatically inherits Fluent dark glass aesthetics without explicit `Style="..."` tags.
     * Standard rows are written declaratively in 3 lines without duplicate layout boilerplate.
     * Custom complex controls (interactive hotkey recorder, color palette picker, file pickers) drop right into pages as native WPF elements without fighting rigid data schemas.
2. **Zero Pollution of `MetroDialog`:**
   * [`MetroDialog`](file:///d:/MetroHub/src/MetroHub/Presentation/Dialogs/MetroDialog.xaml) remains untouched for small modal dialogs (620 × 360).
   * Settings uses a dedicated, spacious window (`960 × 640`, responsive down to `720 × 460`) located in `Presentation/Views/Settings/`.
3. **`SettingsService` as Single Source of Truth:**
   * ViewModels never save to disk directly.
   * ViewModels never update UI tokens directly.
   * ViewModels call `SettingsService.Update(...)`. The service updates in-memory state, fires `SettingChanged`, updates tokens, and debounces disk saves (400ms).
4. **No Monolithic "God Object" ViewModel:**
   * `SettingsShellViewModel` is a thin coordinator that only manages navigation (`CurrentView`).
   * Each category page has its own focused ViewModel (under 50 lines of code).
5. **WPF `ContentControl` Navigation:**
   * Navigation state lives in `SettingsShellViewModel.CurrentView`.
   * Swapping between Home Grid and Two-Pane Detail View uses XAML `DataTemplate` mapping. Zero navigation code-behind.
6. **WPF-UI 3.1.1 Component Standard & Gotchas:**
   * Standard rows use `ui:CardControl` (Title, Description, Icon, and right-aligned action control).
   * Grouped / deep sub-settings use `ui:CardExpander` (master card with dropdown child options).
   * External triggers and folder links use `ui:CardAction` (clickable card with trailing chevron).
   * **Important WPF-UI 3.1.1 Gotcha**: `ui:CardAction` derives directly from `ButtonBase` and does **not** expose a `CornerRadius` dependency property (unlike `CardControl` and `CardExpander`). Border curvature is managed by its parent container template.
7. **Strict Modular File Isolation (Golden Law):**
   * Do NOT put setting category panels inline inside `SettingsWindow.xaml`.
   * Each category page lives in its own dedicated `UserControl` file in `src/MetroHub/Presentation/Views/Settings/Pages/`.
8. **Native `ui:ListView` for Vertical Rails (Zero Hand-Rolled ListBox Templates):**
   * **Mandatory Control**: Always use WPF-UI's native `<ui:ListView>` with `SelectedItem` for side rails and category selectors.
   * **Never Re-invent with `ListBoxItem`**: Do NOT author 100-line custom `ControlTemplate` styles with manual `ActivePill` borders and mouse-over triggers.
   * **Built-in Fluent Capabilities**: WPF-UI's `ui:ListViewItem` already includes the native Windows 11 Selection Pill (`ListViewItemPillFillBrush`), pointer-over hover feedback (`ListViewItemBackgroundPointerOver`), focus visuals, and corner radii out of the box.
   * **Sizing Rule**: Allocate at least `240px` for the left rail so categories like "Integrations & Companion" have comfortable padding without text clipping or ellipsis.

---

## 2. Mandatory UI & Spacing Manifesto (WinUI / Fluent Design System)

> [!IMPORTANT]
> **CRITICAL DESIGN LAWS (NON-NEGOTIABLE FOR ALL SETTINGS SURFACES):**
> 
> 1. **Full-Bleed Viewport Law**:
>    * In any two-pane master-detail layout, the secondary pane's background plate (`DialogPanelBackground`) MUST bleed continuously from the very top window bezel (Y=0) to the bottom bezel.
>    * Titlebar controls (drag regions, caption close `[X]` button) OVERLAY the canvas; they must NEVER chop the layout in half or push down the background panel.
> 
> 2. **The Strict Vertical Grid Alignment Law**:
>    * Every navigation element in the left rail (Back button, category items, footer items) MUST share the exact same X-axis coordinates for icons and labels.
>    * The Back button icon (`ArrowLeft24`) and category item icons (`SymbolIcon`) must align along the exact same vertical guide line down to the single pixel. Zero zigzagging.
> 
> 3. **The Shared Horizontal Baseline Law**:
>    * The Left Rail (Column 0) and Right Viewport (Column 1) MUST share the exact same row definition baseline (`Row 0` Top Navigation Band, `Row 1` Content Area).
>    * The top of the first category item in the `ui:ListView` and the top of the Viewport Category Title MUST align on the exact same horizontal coordinate ($Y = 56\text{px}$). Never use disconnected, arbitrary margins across separate sub-grids.
> 
> 4. **Rail & Selection Pill Breathing Room (12px / 16px Inset Law)**:
>    * Rail containers must maintain at least `12px` of outer margin from the outer window border.
>    * Selection pills (`ListViewItemPillFillBrush`) must NEVER hug the window border or suffocate the category icon.
>    * Item container padding must provide at least `12px` of clearance between the selection pill and the icon.
> 
> 5. **Standard WinUI Spacing Tokens (Page & Cards Layout)**:
>    * **Header-to-Cards Spacing**: `20px - 24px`.
>    * **Outer Viewport Side Padding**: `36px` uniform left and right margins.
>    * **Inter-Card Group Spacing**: `16px` between distinct card groups / expanders.
>    * **Intra-Card Element Spacing**: `8px - 12px` between adjacent cards.
>    * **Inside Card Padding**: `16px` horizontal, `12px` vertical.
>    * **Navigation Row Heights**: Navigation items and Back buttons must be standardized at `38px` height with standard corner radii.

---

## 3. The 9-Pillar Information Architecture

Settings are grouped strictly by the **user's mental model**, eliminating confusion between developer subsystems:

```
                  METROHUB SETTINGS 9 CORE PILLARS
┌──────────────────────────────────────────────────────────────────────────────────┐
│ 1. General         Startup, Tray, Auto-Hide, Dismiss behaviors                   │
│ 2. Appearance      Themes, Mica/Acrylic, Wallpapers, Curves, Motion Tokens       │
│ 3. Workspaces      Canvas orientation, Cyclic scroll, Groups & "Open All" button │
│ 4. Sidebar & Apps  Auto-hide rail, Pinned quick shortcuts, All-Apps Grid/List    │
│ 5. Search Engine   Voidtools Everything integration, FZF fuzzy search, Indexing  │
│ 6. Widgets         Clock (12/24h), Weather (°C/°F, location), Calendar, Heartbeat│
│ 7. Integrations    Browser Companion server (port/health), Media controller sync │
│ 8. Shortcuts       Global summon hotkey (Ctrl+~), Workspace jumps, Search hotkey │
│ 9. About           Velopack updates, App version, Data folders, Crash logs       │
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Visual Layout and Navigation Flow

The Settings experience uses a two-state flow:

### State 1: Home View (3 × 3 Category Grid Matrix)
* Displayed when the window opens.
* Contains a clean title bar (`Settings`) and a uniform 9-card grid (3 columns × 3 rows) of high-density Fluent category tiles.
* Clicking any category navigates to State 2 for that pillar.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Settings                                                               [X] │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   ┌─────────────────────┐ ┌─────────────────────┐ ┌─────────────────────┐   │
│   │  General           │ │  Appearance        │ │  Workspaces        │   │
│   │ Startup, window, tray│ │ Themes, materials   │ │ Layout, groups, grid│   │
│   └─────────────────────┘ └─────────────────────┘ └─────────────────────┘   │
│   ┌─────────────────────┐ ┌─────────────────────┐ ┌─────────────────────┐   │
│   │  Sidebar & Apps    │ │  Search & Indexing │ │  Widgets           │   │
│   │ Rail, links, drawer │ │ Everything, FZF     │ │ Clock, weather, sync│   │
│   └─────────────────────┘ └─────────────────────┘ └─────────────────────┘   │
│   ┌─────────────────────┐ ┌─────────────────────┐ ┌─────────────────────┐   │
│   │  Integrations      │ │  Shortcuts         │ │  About & Maintain  │   │
│   │ Browser, media, port│ │ Global hotkey, keys │ │ Updates, logs, info │   │
│   └─────────────────────┘ └─────────────────────┘ └─────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

#### Home Grid Icon & Description Roster:
| Pillar | Title | Subtitle | Icon (`SymbolRegular`) |
| :--- | :--- | :--- | :--- |
| `General` | General | Startup, window behavior, tray | `Power24` |
| `Appearance` | Appearance | Themes, materials, wallpapers | `PaintBucket24` |
| `Workspaces` | Workspaces & Canvas | Layout orientation, groups, grid | `Grid24` |
| `Sidebar` | Sidebar & Apps | Navigation rail, quick links, drawer | `AppsListDetail24` |
| `Search` | Search & Indexing | Everything engine, FZF, sources | `Search24` |
| `Widgets` | Widgets | Clock, weather, calendar, refresh | `AppGeneric24` |
| `Integrations` | Integrations & Companion | Browser extension, media, port | `AppsAddIn24` |
| `Shortcuts` | Shortcuts & Hotkeys | Global hotkey, workspace jumps | `Keyboard24` |
| `About` | About & Maintenance | Updates, version, logs, backup | `Info24` |

### State 2: Detail View (Two-Pane Split)
* **Left Column (240px):** Frosted Acrylic rail (`WH_MOUSE_LL` protected) with native `<ui:ListView>`.
  * Top `[← Settings]` back button returns to State 1.
  * Vertical category list powered by `ui:ListView` (native `ListViewItemPillFillBrush` selection pill, pointer-over hover feedback).
  * 240px width ensures full visibility for longer category titles (e.g., "Integrations & Companion") without clipping or ellipses.
* **Right Column (720px):** Obsidian dark background panel (`DialogPanelBackground`).
  * Sticky category title and description header.
  * `ScrollViewer` (`WidgetFloatingScrollViewerStyle`) displaying the category's `ui:CardControl` and `ui:CardExpander` list.

```
┌─────────────────────────┬───────────────────────────────────────────────────┐
│ [← Settings]            │ Appearance & Theming                          [X] │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  General               │ Theme Mode                                        │
│  Appearance       [ | ]│ Choose Dark, Light, or Windows System Sync       │
│  Workspaces            │ [ Dark                                          ▼]│
│  Sidebar & Apps        ├───────────────────────────────────────────────────┤
│  Search & Indexing     │ Backdrop Material                                 │
│  Widgets               │ [ Mica (Windows 11)                             ▼]│
│  Integrations          ├───────────────────────────────────────────────────┤
│  Shortcuts & Hotkeys   │  Live Wallpapers                                ▾│
│                         ├───────────────────────────────────────────────────┤
│  About                 │   Pause playback when MetroHub is hidden [ Toggle]│
│                         │   Parallax scroll offset effect          [ Toggle]│
│ (Frosted Acrylic Rail)  │ (Obsidian Dark Content Viewport)                  │
└─────────────────────────┴───────────────────────────────────────────────────┘
```

---

## 5. Codebase Reuse & Repertoire Controls

### Reused Global Styles (from `ControlStyles.xaml`):
* `FluentAccentToggleSwitchStyle` -> Default implicit style for all `<ui:ToggleSwitch />`.
* `FluentGlassComboBoxStyle` -> Default implicit style for all `<ComboBox />`.
* `MetroSegmentedTabStyle` -> Multi-choice segmented pills for `<ListBox />`.
* `MetroInputStyle` -> Default implicit style for all `<TextBox />`.
* `DefaultButtonStyle` / `AccentButtonStyle` / `SubtleButtonStyle` -> Action and trigger buttons.
* `WidgetFloatingScrollViewerStyle` -> Smooth 4px floating scrollbars.

### Reusable Settings Card Styles (added to `ControlStyles.xaml`):
* `MetroSettingsCardStyle` -> Standard `ui:CardControl` container:
  * Margin: `0,0,0,8`
  * Padding: `16,14`
  * Background: `DialogPanelBackground` (#280D0D11)
  * BorderBrush: `SurfaceBorderSubtleBrush` (#15FFFFFF)
  * BorderThickness: `1`
  * CornerRadius: `TileCornerRadius` (2px)
  * Hover pre-glow: Subtle hairline border glow matching `SettingsHomeView` reveal DNA.
* `MetroSettingsExpanderStyle` -> Container for `ui:CardExpander` grouping child sub-settings.
* `MetroSettingsActionCardStyle` -> Container for clickable `ui:CardAction` navigation tiles.

---

## 6. Complete 9-Pillar Feature Catalog

### Pillar 1: General (System & Window Behavior)
*Target ViewModel: `GeneralSettingsViewModel.cs`*
*Target Page: `GeneralPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `LaunchAtStartup` | `ui:ToggleSwitch` | `false` | Start MetroHub automatically on Windows sign-in (Registry Run key). |
| `StartMinimized` | `ui:ToggleSwitch` | `false` | Launch directly to system tray in the background on startup. |
| `CloseOnLaunch` | `ui:ToggleSwitch` | `true` | Hide MetroHub automatically when launching an application or file. |
| `DismissOnDeactivate` | `ui:ToggleSwitch` | `true` | Hide MetroHub automatically when clicking outside its window. |

---

### Pillar 2: Appearance & Theming (Visuals & Polish)
*Target ViewModel: `AppearanceSettingsViewModel.cs`*
*Target Page: `AppearancePage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `ThemeMode` | `ComboBox` | `System` | App color theme: `System`, `Dark`, or `Light`. |
| `AccentSource` | `ComboBox` | `SystemSync` | Accent source: `SystemSync` (Windows accent) or `CustomPalette`. |
| `CustomAccentColor`| Color Swatches | `#4CC2FF` | Custom accent color when `AccentSource` is `CustomPalette`. |
| `BackdropType` | `ComboBox` | `Mica` | Material: `Mica`, `Acrylic`, `DesktopWallpaper`, `BingDaily`, `SpotlightDaily`, `Wallpaper` (Custom). |
| `CustomWallpaperPath`| File Picker Button | `null` | Select custom background image (`.png`, `.jpg`) or live video (`.mp4`, `.webm`). |
| `WallpaperDimOpacity`| 3-Step `MetroSegmentedTabStyle` | `50%` | Scrim darkness level: `Light (35%)`, `Balanced (50%)`, `Heavy (65%)`. |
| **Live Wallpapers Group** | `ui:CardExpander` | Group | Sub-settings for video wallpaper performance and effects: |
| ↳ `PauseLiveWallpapers` | `ui:ToggleSwitch` | `true` | Pause video playback when MetroHub is hidden to save GPU resources. |
| ↳ `WallpaperParallax` | `ui:ToggleSwitch` | `true` | Parallax scroll offset effect on custom wallpapers. |
| `TileCornerRadius` | 4-Step `MetroSegmentedTabStyle` | `2px` | Corner curvature: `0px` (Metro Sharp), `2px` (Subtle), `4px` (Medium), `8px` (Rounded). |
| `AnimationsEnabled`| `ui:ToggleSwitch` | `true` | Master toggle for Fluent motion transitions. When disabled, durations collapse to 0ms. |

---

### Pillar 3: Workspaces & Canvas (Layout & Grid)
*Target ViewModel: `WorkspacesSettingsViewModel.cs`*
*Target Page: `WorkspacesPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Orientation` | 2-Step `MetroSegmentedTabStyle` | `Vertical` | Canvas scroll orientation: `Vertical` or `Horizontal`. |
| `CyclicWorkspaceScroll` | `ui:ToggleSwitch` | `true` | Loop continuously from the last workspace back to the first. |
| **Tile Groups Group** | `ui:CardExpander` | Group | Group header and batch action controls: |
| ↳ `ShowGroupHeaders` | `ui:ToggleSwitch` | `true` | Render group title headers above tile clusters. |
| ↳ `EnableGroupOpenAllButton`| `ui:ToggleSwitch` | `true` | Display "Open All" action button on group headers to launch all apps in group. |
| `GridMagneticSnap` | `ui:ToggleSwitch` | `true` | Snap tiles cleanly to grid coordinates during drag-and-drop. |

---

### Pillar 4: Sidebar & Apps (Navigation Rails & Drawer)
*Target ViewModel: `SidebarSettingsViewModel.cs`*
*Target Page: `SidebarPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `SidebarAutoHide` | `ui:ToggleSwitch` | `true` | Auto-hide sidebar rail when mouse leaves the left screen edge. |
| `SidebarPinned` | `ui:ToggleSwitch` | `false` | Lock sidebar rail permanently expanded on screen. |
| `RecentViewMode` | 2-Step `MetroSegmentedTabStyle` | `Grid` | All-Apps drawer default layout mode: `Grid` (cards) or `List` (compact rows). |
| **Quick Shortcuts Group** | `ui:CardExpander` | Group | Configure default folders and system icons shown in the sidebar rail: |
| ↳ `SidebarShortcuts` | Checked List / Toggles | Default | Toggle visibility of Explorer, Downloads, Documents, Pictures, Music, Videos. |

---

### Pillar 5: Search & Indexing (Everything & Fast Query)
*Target ViewModel: `SearchSettingsViewModel.cs`*
*Target Page: `SearchPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Everything Search Engine** | `ui:CardExpander` | Group | Deep integration with Voidtools Everything service: |
| ↳ `EverythingEnabled` | `ui:ToggleSwitch` | `true` | Use Voidtools Everything for instantaneous NTFS drive file search. |
| ↳ `ServiceStatus` | `ui:InfoBar` / Badge | Live | Displays service status (Ready / Connecting / Not Installed). |
| `FzfFuzzySensitivity` | `ComboBox` | `Balanced` | Fuzzy matching tolerance in All-Apps drawer: `Strict`, `Balanced`, or `Lenient`. |
| `ClearSearchHistory` | `ui:Button` | Action Trigger | Erase saved search query history and cache. |

---

### Pillar 6: Widgets (Global Defaults & Performance)
*Target ViewModel: `WidgetsSettingsViewModel.cs`*
*Target Page: `WidgetsPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Clock Widget** | `ui:CardExpander` | Group | Time and format options: |
| ↳ `ClockTimeFormat` | 2-Step `MetroSegmentedTabStyle` | `12-Hour` | Clock display format: `12-Hour (AM/PM)` or `24-Hour`. |
| ↳ `ClockShowSeconds`| `ui:ToggleSwitch` | `false` | Display seconds readout on active clock widgets. |
| **Weather Widget** | `ui:CardExpander` | Group | Weather data and units: |
| ↳ `WeatherUnit` | 2-Step `MetroSegmentedTabStyle` | `Celsius` | Temperature unit: `Celsius (°C)` or `Fahrenheit (°F)`. |
| ↳ `WeatherAutoLocation`| `ui:ToggleSwitch` | `true` | Detect current city automatically via IP geocoding. |
| `CalendarFirstDay` | `ComboBox` | `Monday` | First day of week on calendar widgets: `Monday` or `Sunday`. |
| `WidgetSleepWhenHidden` | `ui:ToggleSwitch` | `true` | Pause background timers and weather API polling when MetroHub is hidden. |

---

### Pillar 7: Integrations & Companion (Browser & System Ecosystem)
*Target ViewModel: `IntegrationsSettingsViewModel.cs`*
*Target Page: `IntegrationsPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Browser Extension Companion**| `ui:CardExpander` | Group | Local companion server for Chrome / Edge / Firefox extension: |
| ↳ `BrowserCompanionEnabled` | `ui:ToggleSwitch` | `true` | Run local HTTP companion server to pin web pages directly to workspaces. |
| ↳ `CompanionPort` | `TextBox` (Numeric) | `5174` | Localhost port number for browser extension communication. |
| ↳ `CompanionStatus` | `ui:InfoBar` | Live | Companion server status badge (`Listening on localhost:5174`). |
| `MediaControllerSync` | `ui:ToggleSwitch` | `true` | Synchronize Windows Media playback state (Now Playing) to widget cards. |

---

### Pillar 8: Shortcuts & Hotkeys (Input & Speed)
*Target ViewModel: `ShortcutsSettingsViewModel.cs`*
*Target Page: `ShortcutsPage.xaml`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `GlobalHotkey` | Interactive Key Recorder | `Ctrl + ~` | Interactive hotkey box. Listens for user key combination with modifiers. |
| `WorkspaceDirectHotkeys`| Informational Card | `Ctrl + 1..9` | Direct shortcut jump to Workspace 1 through 9. |
| `WorkspaceSlideHotkeys` | `ComboBox` | `Ctrl + Alt + Arrows` | Sequential workspace slide navigation (`Ctrl + Alt + Left/Right` or `Up/Down`). |
| `SearchHotkeys` | Informational Card | `Alt + Space` | Direct shortcut to summon search drawer. |

---

### Pillar 9: About & Maintenance (Diagnostics & Life-Cycle)
*Target ViewModel: `AboutSettingsViewModel.cs`*
*Target Page: `AboutPage.xaml`*

| Item | Control Type | Action / Value | Description |
| :--- | :--- | :--- | :--- |
| `AppVersion` | Text Block | `v2.1.0 (x64)` | MetroHub build version, .NET 10 runtime, and OS build. |
| `VelopackCheck` | `ui:Button` | Action Trigger | "Check for Updates" button (wired for Velopack auto-updater). |
| `OpenDataDirectory`| `ui:CardAction` | Action Trigger | Opens `%LocalAppData%\MetroHub` in Windows File Explorer. |
| `OpenLogsFile` | `ui:CardAction` | Action Trigger | Opens `logs\crash.log` in default text editor. |
| `ResetSettings` | `ui:Button` (Danger) | Action Trigger | Restore all settings to factory defaults with confirmation dialog. |

---

## 7. Detailed File Structure

```
d:\MetroHub\src\MetroHub\
├── Core\
│   ├── Models\
│   │   └── AppSettings.cs               <-- Clean properties for all 8 pillars
│   └── Services\
│       └── SettingsService.cs           <-- Single source of truth, tokens, debounced save
│
└── Presentation\
    ├── Themes\
    │   └── ControlStyles.xaml           <-- Contains MetroSettingsCardStyle & ExpanderStyle
    │
    └── Views\
        └── Settings\
            ├── SettingsWindow.xaml      <-- Main Frosted Acrylic shell (960x640)
            ├── SettingsWindow.xaml.cs   <-- Window lifecycle, WH_MOUSE_LL hook, drag
            ├── SettingsShellViewModel.cs<-- Controls CurrentView navigation orchestrator
            │
            ├── ViewModels\
            │   ├── SettingsHomeViewModel.cs
            │   ├── GeneralSettingsViewModel.cs
            │   ├── AppearanceSettingsViewModel.cs
            │   ├── WorkspacesSettingsViewModel.cs
            │   ├── SidebarSettingsViewModel.cs
            │   ├── SearchSettingsViewModel.cs
            │   ├── WidgetsSettingsViewModel.cs
            │   ├── IntegrationsSettingsViewModel.cs
            │   ├── ShortcutsSettingsViewModel.cs
            │   └── AboutSettingsViewModel.cs
            │
            └── Pages\
                ├── SettingsHomeView.xaml         <-- State 1: 8-Category Card Grid
                ├── SettingsDetailLayout.xaml     <-- State 2: Two-Pane Split Container
                ├── GeneralPage.xaml              <-- Reusable card rows
                ├── AppearancePage.xaml           <-- Reusable card rows + swatches
                ├── WorkspacesPage.xaml           <-- Reusable card rows
                ├── SidebarPage.xaml              <-- Reusable card rows
                ├── SearchPage.xaml               <-- Reusable card rows + Expander
                ├── WidgetsPage.xaml              <-- Reusable card rows + Expander
                ├── IntegrationsPage.xaml         <-- Reusable card rows + Expander
                ├── ShortcutsPage.xaml            <-- Reusable card rows + Key recorder
                └── AboutPage.xaml                <-- Action cards & update triggers
```

---

## 8. Implementation Checklist & Phased Roadmap

### Phase 1: SettingsWindow Shell & Home View (COMPLETED)
- [x] Wire Settings button in [`SidebarRailControl.xaml`](file:///d:/MetroHub/src/MetroHub/Presentation/Controls/Shell/SidebarRailControl.xaml).
- [x] Wire window summon in [`MainWindow.Sidebar.cs`](file:///d:/MetroHub/src/MetroHub/Presentation/Views/MainWindow/MainWindow.Sidebar.cs).
- [x] Construct `SettingsWindow.xaml` host with `960x640` bounds, `WH_MOUSE_LL` outside-click dismissal, DWM Acrylic backdrop, and drag region.
- [x] Create `SettingsHomeView.xaml` and `SettingsHomeViewModel.cs`.
- [x] Update `SettingsHomeView` and `SettingsHomeViewModel` to render the clean 9-pillar category layout (3 × 3 UniformGrid).
- [x] Verify hover sheens and reveal animations.

### Phase 2: Two-Pane Detail Layout Shell & Reusable Card Styles (COMPLETED)
- [x] Add `MetroSettingsCardStyle` (`ui:CardControl`), `MetroSettingsExpanderStyle` (`ui:CardExpander`), and `MetroSettingsActionCardStyle` (`ui:CardAction`) to `ControlStyles.xaml`.
- [x] Provide implicit default styles for `ui:CardControl`, `ui:CardExpander`, and `ui:CardAction` so all child pages inherit them automatically without boilerplate.
- [x] Build `SettingsDetailLayoutViewModel.cs` with Left Rail `Categories`, `SelectedCategory`, `CurrentPageViewModel`, and `BackToHomeCommand`.
- [x] Build `SettingsDetailLayout.xaml` (Left 240px Frosted Acrylic Rail powered by native `ui:ListView` with built-in selection support + Right 720px Obsidian Deck with `WidgetFloatingScrollViewerStyle`).
- [x] Refactor rail: deleted 105 lines of hand-rolled `ListBoxItem` template boilerplate in favor of native `ui:ListView` (native selection pill, hover effects, zero text truncation).
- [x] Build live preview `DataTemplate` validating all 3 card types in the viewport.
- [x] Wire `[← Settings]` back button and navigation transitions between Home Grid and Detail View via `SettingsShellViewModel`.
- [x] Register `DataTemplate` for `SettingsDetailLayoutViewModel` in `SettingsWindow.xaml`.
- [x] Verify clean `dotnet build` and `dotnet publish` to `C:\Users\HamB\Desktop\MetroHubApp` with 0 warnings/errors.

### Phase 3: Modular Category Pages (Option C Hybrid)
- [ ] **Step 3.1: General Settings (`GeneralPage.xaml` & `GeneralSettingsViewModel.cs`)**
  - `LaunchAtStartup` (`ui:CardControl` + `ui:ToggleSwitch` + Registry Run key hook)
  - `StartMinimized` (`ui:CardControl` + `ui:ToggleSwitch`)
  - `CloseOnLaunch` (`ui:CardControl` + `ui:ToggleSwitch`)
  - `DismissOnDeactivate` (`ui:CardControl` + `ui:ToggleSwitch`)
- [ ] **Step 3.2: Appearance & Theming (`AppearancePage.xaml` & `AppearanceSettingsViewModel.cs`)**
  - Theme mode selector (`ComboBox`)
  - Material backdrop selector (`ComboBox`: Mica, Acrylic, DesktopWallpaper)
  - Color swatches & scrim opacity slider (`MetroSegmentedTabStyle`)
  - Live video wallpapers expander (`ui:CardExpander`)
- [ ] **Step 3.3: Workspaces & Canvas (`WorkspacesPage.xaml` & `WorkspacesSettingsViewModel.cs`)**
  - Canvas orientation (`Vertical` / `Horizontal`)
  - Cyclic scroll toggle
  - Tile groups expander (`ShowGroupHeaders`, `EnableGroupOpenAllButton`)
- [ ] **Step 3.4: Sidebar & Apps (`SidebarPage.xaml` & `SidebarSettingsViewModel.cs`)**
  - Auto-hide rail toggle
  - Pinned sidebar shortcut list expander
  - All-Apps drawer layout mode (`Grid` / `List`)
- [ ] **Step 3.5: Search & Indexing (`SearchPage.xaml` & `SearchSettingsViewModel.cs`)**
  - Voidtools Everything integration expander
  - FZF fuzzy search algorithm sensitivity
  - Search history retention
- [ ] **Step 3.6: Widgets (`WidgetsPage.xaml` & `WidgetsSettingsViewModel.cs`)**
  - Clock format (12h / 24h)
  - Weather unit (°C / °F) and location expander
  - Calendar agenda sync
- [ ] **Step 3.7: Integrations & Companion (`IntegrationsPage.xaml` & `IntegrationsSettingsViewModel.cs`)**
  - Browser extension companion HTTP server expander (Port, Status, Token)
  - Windows Media Session / Spotify track sync
- [ ] **Step 3.8: Shortcuts & Hotkeys (`ShortcutsPage.xaml` & `ShortcutsSettingsViewModel.cs`)**
  - Global summon hotkey recorder card
  - Workspace jump hotkeys
- [ ] **Step 3.9: About & Maintenance (`AboutPage.xaml` & `AboutSettingsViewModel.cs`)**
  - App version & channel
  - Velopack update check trigger
  - Data folders and crash logs (`ui:CardAction`)

### Phase 4: State Management & Debounced Persistence
- [ ] Expand `AppSettings.cs` to match all 9-pillar catalog properties.
- [ ] Implement `SettingsService.cs` with in-memory caching, token synchronization, and 400ms debounced JSON saving.
- [ ] Connect two-way data bindings across all page ViewModels.
- [ ] Validate with `dotnet build src/MetroHub/MetroHub.csproj` and publish protocol.
