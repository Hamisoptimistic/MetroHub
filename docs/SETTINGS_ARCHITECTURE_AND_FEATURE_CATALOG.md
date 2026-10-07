# MetroHub Settings Architecture and Feature Catalog

This document defines the architecture, component structure, navigation flow, and exact feature list for the MetroHub Settings system.
It incorporates all architectural decisions, layout requirements, and feature trims agreed upon for production.

---

## 1. Core Architecture Principles

The settings engine follows a decoupled, three-tier architecture:

```
┌────────────────────────────────────────────────────────┐
│                   View Layer (XAML)                    │
│   SettingsWindow (920x600 Acrylic)                     │
│   ├── State 1: SettingsHomeView (Category Grid)        │
│   └── State 2: SettingsDetailLayout (Two-Pane Rail)    │
└───────────────────────────▲────────────────────────────┘
                            │ DataBinding
┌───────────────────────────┴────────────────────────────┐
│                    ViewModel Layer                     │
│   SettingsShellViewModel (Navigation / CurrentView)    │
│   ├── SettingsHomeViewModel                            │
│   └── Page ViewModels (General, Personalization, etc.) │
└───────────────────────────▲────────────────────────────┘
                            │ Invocations
┌───────────────────────────┴────────────────────────────┐
│                    Service Layer                       │
│   SettingsService (Single Source of Truth)             │
│   ├── In-Memory AppSettings Model                      │
│   ├── Dynamic Token Synchronizer (Theme & Motion)      │
│   ├── SettingChanged Event Dispatcher                  │
│   └── Debounced Disk Persistence (StorageService)      │
└────────────────────────────────────────────────────────┘
```

### Key Architectural Decisions:
1. **Zero Pollution of `MetroDialog`:**
   * [`MetroDialog`](file:///d:/MetroHub/src/MetroHub/Presentation/Dialogs/MetroDialog.xaml) remains untouched for small modal dialogs (620 × 360).
   * Settings uses a dedicated, spacious window (`920 × 600`) located in `Presentation/Views/Settings/`.
2. **`SettingsService` as Single Source of Truth:**
   * ViewModels never save to disk directly.
   * ViewModels never update UI tokens directly.
   * ViewModels call `SettingsService.Update(...)`. The service handles in-memory state, fires the `SettingChanged` event, updates tokens, and debounces disk saves (400ms).
3. **No Monolithic "God Object" ViewModel:**
   * `SettingsShellViewModel` is a thin shell that only manages navigation (`CurrentView`).
   * Each category page has its own small ViewModel (under 40 lines of code).
4. **WPF `ContentControl` Navigation:**
   * Navigation state lives in `SettingsShellViewModel.CurrentView`.
   * Swapping between Home and Detail uses XAML `DataTemplate` mapping. Zero navigation code-behind.
5. **No Search Box:**
   * Kept minimal and focused. The category cards provide direct, one-click access.
6. **WPF-UI Component Standard:**
   * Setting rows use `ui:CardControl` (Title, Description, and right-aligned action control).
   * Category selection tiles use `ui:CardAction` with icons and chevrons.
7. **Strict Modular File Isolation (Golden Law):**
   * Do NOT put setting category panels inline inside `SettingsWindow.xaml`.
   * Each category page must live in its own dedicated `UserControl` file in `src/MetroHub/Presentation/Views/Settings/Pages/`.
   * Never skip phases. Complete visual polish and sign-off on Phase 1 (Home Shell) before opening Phase 2 (Two-Pane Shell), and Phase 2 before Phase 3 (Modular Pages).

---

## 2. Visual Layout and Navigation Flow

The Settings experience uses a two-state flow:

### State 1: Home View (Category Grid)
* Displayed when the window opens.
* Contains a top title bar and a clean grid of 6 large `ui:CardAction` category cards.
* Clicking any card navigates to State 2 for that category.

```
┌─────────────────────────────────────────────────────────────┐
│  Settings                                               [X] │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│   ┌─────────────────┐   ┌─────────────────┐                 │
│   │  General       │   │  Personalize   │                 │
│   │ Startup & Exit  │   │ Themes & Walls  │                 │
│   └─────────────────┘   └─────────────────┘                 │
│   ┌─────────────────┐   ┌─────────────────┐                 │
│   │  Canvas        │   │  Shortcuts     │                 │
│   │ Orientation     │   │ Hotkeys & Keys  │                 │
│   └─────────────────┘   └─────────────────┘                 │
│   ┌─────────────────┐   ┌─────────────────┐                 │
│   │  Widgets       │   │  About         │                 │
│   │ Clock & Weather │   │ Version & Files │                 │
│   └─────────────────┘   └─────────────────┘                 │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### State 2: Detail View (Two-Pane Split)
* **Left Column (220px):** Frosted Acrylic rail.
  * Contains a top `[← Home]` back button to return to State 1.
  * Contains a vertical navigation list of the 6 categories.
  * Active item displays an accent color indicator bar.
* **Right Column (700px):** Obsidian dark background panel (`DialogPanelBackground`).
  * Contains a `ScrollViewer` displaying the category's `ui:CardControl` list.

```
┌─────────────────────────┬───────────────────────────────────┐
│ [← Home]                │ Personalization               [X] │
├─────────────────────────┼───────────────────────────────────┤
│  General               │ Theme Mode                        │
│  Personalize      [ | ]│ Choose Dark, Light, or System     │
│  Canvas                │ [ System                        ▼]│
│  Shortcuts             ├───────────────────────────────────┤
│  Widgets               │ Background Material               │
│  About                 │ [ Mica (Windows 11)             ▼]│
│                         ├───────────────────────────────────┤
│                         │ Live Wallpapers                   │
│                         │ Pause video when MetroHub hides   │
│                         │ [ Toggle: ON                     ]│
│ (Frosted Acrylic Rail)  │ (Obsidian Dark Content Panel)     │
└─────────────────────────┴───────────────────────────────────┘
```

---

## 3. Detailed File Structure

All new files are placed in a dedicated namespace:

```
d:\MetroHub\src\MetroHub\
├── Core\
│   └── Services\
│       └── SettingsService.cs           <-- Single source of truth, tokens, debounced save
│
└── Presentation\
    └── Views\
        └── Settings\
            ├── SettingsWindow.xaml      <-- Main Acrylic shell (920x600)
            ├── SettingsWindow.xaml.cs   <-- Window lifecycle & drag
            ├── SettingsShellViewModel.cs<-- Controls CurrentView navigation
            │
            ├── ViewModels\
            │   ├── SettingsHomeViewModel.cs
            │   ├── GeneralSettingsViewModel.cs
            │   ├── PersonalizationSettingsViewModel.cs
            │   ├── CanvasSettingsViewModel.cs
            │   ├── ShortcutsSettingsViewModel.cs
            │   ├── WidgetsSettingsViewModel.cs
            │   └── AboutSettingsViewModel.cs
            │
            └── Pages\
                ├── SettingsHomeView.xaml         <-- State 1: Category Card Grid
                ├── SettingsDetailLayout.xaml     <-- State 2: Two-Pane Split Container
                ├── GeneralPage.xaml              <-- Card rows
                ├── PersonalizationPage.xaml      <-- Card rows
                ├── CanvasPage.xaml               <-- Card rows
                ├── ShortcutsPage.xaml            <-- Card rows
                ├── WidgetsPage.xaml              <-- Card rows
                └── AboutPage.xaml                <-- Card rows
```

---

## 4. Complete Streamlined Settings Catalog

All low-level grid math (cell size, tile gaps, resistance, group column widths) and complex factory resets have been removed.

### Category 1: General & Behavior
*Target ViewModel: `GeneralSettingsViewModel.cs`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `LaunchAtStartup` | `ui:ToggleSwitch` | `false` | Start MetroHub automatically on Windows sign-in (Registry Run key). |
| `CloseOnLaunch` | `ui:ToggleSwitch` | `true` | Hide MetroHub automatically when launching an application or file. |
| `DismissOnDeactivate` | `ui:ToggleSwitch` | `true` | Hide MetroHub automatically when clicking outside its window. |

---

### Category 2: Personalization (Theming & Visuals)
*Target ViewModel: `PersonalizationSettingsViewModel.cs`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `ThemeMode` | `ComboBox` | `System` | App color theme: `System`, `Dark`, or `Light`. |
| `AccentSource` | `ComboBox` | `SystemSync` | Accent source: `SystemSync` (Windows accent) or `CustomPalette`. |
| `CustomAccentColor`| Color Swatches | `#4CC2FF` | Custom accent color when `AccentSource` is `CustomPalette`. |
| `AccentStyle` | Fixed Display | `Flat` | Standard solid color Windows 11 accent styling. |
| `BackdropType` | `ComboBox` | `Mica` | Material: `Mica`, `Acrylic`, `DesktopWallpaper`, `BingDaily`, `SpotlightDaily`, `Wallpaper` (Custom). |
| `CustomWallpaperPath`| File Picker Button | `null` | Choose custom image (`.png`, `.jpg`) or live video (`.mp4`, `.webm`). |
| `WallpaperDimOpacity`| 3-Step Segmented | `50%` | Scrim darkness level: `Light (35%)`, `Balanced (50%)`, `Heavy (65%)`. |
| `WallpaperParallax` | `ui:ToggleSwitch` | `true` | Parallax scroll offset effect on custom wallpapers. |
| `PauseLiveWallpapers`| `ui:ToggleSwitch` | `true` | Pause `.mp4` video playback when MetroHub is hidden to save GPU resources. |
| `TileCornerRadius` | 4-Step Segmented | `2px` | Corner curvature: `0px` (Metro Sharp), `2px` (Subtle), `4px` (Medium), `8px` (Rounded). |
| `AnimationsEnabled`| `ui:ToggleSwitch` | `true` | Fluent motion transitions. When disabled, durations collapse to 0ms. |

---

### Category 3: Canvas & Workspaces
*Target ViewModel: `CanvasSettingsViewModel.cs`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Orientation` | `ComboBox` / Segmented | `Vertical` | Canvas scroll orientation: `Vertical` or `Horizontal`. |
| `SlideDirection` | Synchronized | Auto | Workspace transition animation direction matching canvas orientation. |

---

### Category 4: Shortcuts & Input
*Target ViewModel: `ShortcutsSettingsViewModel.cs`*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `GlobalHotkey` | Interactive Key Box | `Ctrl + ~` | Interactive hotkey recorder. Requires at least one modifier (`Ctrl`, `Alt`, or `Shift`). |
| `WorkspaceDirectHotkeys`| Informational Card | `Ctrl + 1..9` | Direct shortcut jump to Workspace 1 through 9. |
| `WorkspaceSlideHotkeys` | `ComboBox` | `Ctrl + Alt + Arrows` | Sequential workspace slide navigation (`Ctrl + Alt + Left/Right` or `Up/Down`). |

---

### Category 5: Widgets (Global Defaults)
*Target ViewModel: `WidgetsSettingsViewModel.cs`*
*(Rule: Changing a default updates all active widgets immediately across all workspaces).*

| Setting | Control Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `WeatherUnit` | 2-Step Segmented | `Celsius` | Temperature unit: `Celsius (°C)` or `Fahrenheit (°F)`. |
| `WeatherAutoLocation`| `ui:ToggleSwitch` | `true` | Automatically detect location via IP. When off, uses custom default city. |
| `ClockTimeFormat` | 2-Step Segmented | `12-Hour` | Clock display format: `12-Hour (AM/PM)` or `24-Hour`. |
| `ClockShowSeconds`| `ui:ToggleSwitch` | `false` | Display seconds on active clock widgets. |
| `CalendarFirstDay` | `ComboBox` | `Monday` | First day of week on calendar widgets: `Monday` or `Sunday`. |

---

### Category 6: About & Updates
*Target ViewModel: `AboutSettingsViewModel.cs`*

| Item | Control Type | Action / Value | Description |
| :--- | :--- | :--- | :--- |
| `AppVersion` | Text Block | `v2.1.0` | MetroHub build version and target architecture (x64). |
| `VelopackCheck` | `ui:Button` | Action Trigger | "Check for Updates" button (wired for Velopack integration). |
| `OpenDataDirectory`| `ui:CardAction` | Action Trigger | Opens `%LocalAppData%\MetroHub` in Windows File Explorer. |
| `OpenLogsFile` | `ui:CardAction` | Action Trigger | Opens `logs\crash.log` in default text editor. |

---

## 5. Step-by-Step Runtime Execution Sequence

```
1. User clicks the Gear Button at the bottom of the Sidebar Rail.
2. MetroHub instantiates or shows SettingsWindow.
3. SettingsShellViewModel initializes with CurrentView = SettingsHomeViewModel.
4. SettingsWindow renders SettingsHomeView (6 Category Cards).
5. User clicks a category card (e.g. "Personalization").
6. SettingsShellViewModel changes CurrentView to SettingsDetailLayoutViewModel(Category).
7. SettingsWindow renders the Two-Pane View:
   - Left Rail shows categories with an active indicator line.
   - Right Panel displays PersonalizationPage.
8. User flips a toggle (e.g., Animations).
9. Two-way data binding updates PersonalizationSettingsViewModel.AnimationsEnabled.
10. ViewModel calls SettingsService.Update(s => s.AnimationsEnabled = value).
11. SettingsService executes 3 operations:
    a. Updates in-memory AppSettings model.
    b. Updates MotionTokens.AnimationsEnabled in real time.
    c. Debounces and saves settings.json via StorageService.
12. User clicks [← Home] button:
    - SettingsShellViewModel restores CurrentView = SettingsHomeViewModel.
```

---

## 6. Implementation Checklist & Phased Roadmap

### Phase 1: SettingsWindow Shell & Home View (CURRENT ACTIVE FOCUS)
*Goal: Construct the window host and Home Category Grid (Image 1 style) and polish to complete perfection before moving forward.*
- [x] Add Settings button (`Settings24` icon) directly below Power button in [`SidebarRailControl.xaml`](file:///d:/MetroHub/src/MetroHub/Presentation/Controls/Shell/SidebarRailControl.xaml).
- [x] Wire sidebar Settings button click in [`MainWindow.xaml`](file:///d:/MetroHub/src/MetroHub/Presentation/Views/MainWindow/MainWindow.xaml) and [`MainWindow.Sidebar.cs`](file:///d:/MetroHub/src/MetroHub/Presentation/Views/MainWindow/MainWindow.Sidebar.cs) to open `SettingsWindow`.
- [x] Create directory `src/MetroHub/Presentation/Views/Settings/`.
- [ ] **Window Host Architecture:**
  - [ ] Set exact window dimensions, minimum bounds, and startup position (`CenterOwner`).
  - [ ] Configure DWM Acrylic backdrop (`DWMSBT_TRANSIENTWINDOW`) and obsidian dark base brush.
  - [ ] Standardize header titlebar drag region, title text, and top-right dismiss button (`ModalDismissButtonStyle`).
- [ ] **Home View Elements (Single-Pane Grid):**
  - [ ] Header Banner: Profile/System Hero Card (App Icon, MetroHub title, version, system status).
  - [ ] Category Card Grid: 6 high-density Fluent cards (General, Personalization, Canvas, Shortcuts, Widgets, About).
  - [ ] Typography & Tokens: Match `MetroHub` standard font sizes, muted descriptions, and spacing.
  - [ ] Hover & Click Micro-interactions: Card elevation, subtle border sheen, and pointer feedback.
- [ ] **Visual Sign-Off:** Review running window with user and iterate until polished to perfection.

---

### Phase 2: Two-Pane Detail Layout Shell (PAUSED UNTIL PHASE 1 COMPLETE)
*Goal: Construct the Two-Pane detail container (Images 2 & 3 style) and polish the navigation ergonomics.*
- [ ] **Left Navigation Rail (220px Frosted Acrylic):**
  - [ ] Header Back Button (`← Settings`) with fluid hover feedback.
  - [ ] Vertical Category List (compact icons, labels).
  - [ ] Active Selection Pill (vertical accent line indicator on active item).
  - [ ] Hover sheens and keyboard navigation support.
- [ ] **Right Content Viewport (Obsidian Dark Glass):**
  - [ ] Sticky category title and description header.
  - [ ] Dynamic content host container for modular pages.
  - [ ] Smooth transition mechanics (Fade/Slide between Home Grid and Two-Pane View).
- [ ] **Visual Sign-Off:** Review navigation interactions with user and iterate until polished to perfection.

---

### Phase 3: Modular Category Pages (PAUSED UNTIL PHASE 2 COMPLETE)
*Goal: Build each category page as a separate, clean UserControl in `src/MetroHub/Presentation/Views/Settings/Pages/` and polish each one individually.*
- [ ] **Phase 3.1: GeneralPage.xaml**
  - [ ] Launch behavior toggles (Run on Startup, Start Minimized, Close to Tray).
  - [ ] Performance mode toggles (Hardware Acceleration, High-DPI fidelity).
  - [ ] Language / Locale selector.
- [ ] **Phase 3.2: PersonalizationPage.xaml**
  - [ ] Theme mode (Obsidian Dark, System).
  - [ ] Backdrop materials (Mica, Acrylic, Solid Glass).
  - [ ] Accent color picker / preset swatches.
  - [ ] Motion & Animation master toggle.
- [ ] **Phase 3.3: CanvasPage.xaml**
  - [ ] Default layout presets (Single, Dual, Ultrawide).
  - [ ] Drag sensitivity & magnetic snap threshold.
  - [ ] Edge resistance toggles.
- [ ] **Phase 3.4: ShortcutsPage.xaml**
  - [ ] Global toggle hotkey recorder.
  - [ ] Workspace quick-switch combinations.
  - [ ] Collision detection indicators.
- [ ] **Phase 3.5: WidgetsPage.xaml**
  - [ ] Default widget refresh intervals.
  - [ ] Weather temperature unit (Celsius / Fahrenheit).
  - [ ] Clock 24h format toggle.
- [ ] **Phase 3.6: AboutPage.xaml**
  - [ ] MetroHub branding, author attribution, and GitHub repository links.
  - [ ] Runtime info (.NET 10, WPF, OS version).
  - [ ] License details and release notes button.

---

### Phase 4: State Management, ViewModels & Persistence (PAUSED UNTIL PHASE 3 COMPLETE)
*Goal: Wire reactive MVVM, services, and disk persistence to the visually approved controls.*
- [ ] Expand [`AppSettings.cs`](file:///d:/MetroHub/src/MetroHub/Core/Models/AppSettings.cs) with clean configuration properties.
- [ ] Create [`SettingsService.cs`](file:///d:/MetroHub/src/MetroHub/Core/Services/) for state caching and debounced JSON persistence.
- [ ] Implement `SettingsShellViewModel.cs`, `SettingsHomeViewModel.cs`, and category page ViewModels.
- [ ] Bind all page controls to ViewModels with two-way data bindings.
- [ ] Run `dotnet build` and unit test validation.

