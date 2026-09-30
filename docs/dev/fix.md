# MetroHub Fix Tracker

---

## Critic 1: `{DynamicResource}` Gap Analysis

> Cross-referencing [METROHUB_MODULAR_ARCHITECTURE_ROADMAP.md](file:///d:/MetroHub/docs/METROHUB_MODULAR_ARCHITECTURE_ROADMAP.md) against actual codebase usage.

The roadmap's core principle (#3) states:

> *"All visual attributes (CornerRadius, AccentBrush, HoverGlow, MotionDuration, Opacity) resolve via `{DynamicResource}` tokens, allowing the upcoming Settings Page to change the entire app live without a restart."*

Below is **everything that still needs `{DynamicResource}` conversion** to fulfill that vision.

---

### 1. ❌ Widgets with ZERO `{DynamicResource}` Usage

These 8 widget views have **no token bindings at all** — every font, color, and radius is hardcoded:

| Widget | File | Issues |
|---|---|---|
| **Calendar** | `Widgets/Catalog/Calendar/CalendarWidgetView.xaml` | 12× hardcoded `FontFamily="Segoe UI Variable..."`, 2× hardcoded `CornerRadius` |
| **Clock** | `Widgets/Catalog/Clock/ClockWidgetView.xaml` | No token bindings at all |
| **Dino** | `Widgets/Catalog/Dino/DinoWidgetView.xaml` | Hardcoded `CornerRadius="4"` |
| **Habit** | `Widgets/Catalog/Habit/HabitWidgetView.xaml` | 6× hardcoded `FontFamily`, 9× hardcoded `CornerRadius` |
| **Power** | `Widgets/Catalog/Power/` | No token bindings |
| **Quotes** | `Widgets/Catalog/Quotes/QuotesWidgetView.xaml` | 1× hardcoded `FontFamily`, 1× hardcoded `CornerRadius` |
| **Rover** | `Widgets/Catalog/Rover/RoverWidgetView.xaml` | No token bindings |
| **Weather** | `Widgets/Catalog/Weather/WeatherWidgetView.xaml` | **13×** hardcoded `FontFamily`, **6×** hardcoded `CornerRadius` — worst offender |

---

### 2. ⚠️ Hardcoded `FontFamily` (should be `{DynamicResource AppFontFamily}` / `AppDisplayFontFamily}`)

The roadmap Phase 5A defines `AppFontFamilyPreference` as a Settings toggle (Segoe UI Variable → Inter → Bahnschrift → Cascadia Code). These hardcoded strings will **not respond** to that setting:

| File | Count | What's hardcoded |
|---|---|---|
| `Widgets/Catalog/Weather/WeatherWidgetView.xaml` | **13** | `"Segoe UI Variable Display..."` and `"Segoe UI Variable Text..."` |
| `Widgets/Catalog/Calendar/CalendarWidgetView.xaml` | **12** | Display + Text variants across month header, weekday headers, day cells |
| `Widgets/Catalog/Habit/HabitWidgetView.xaml` | **6** | Display + Text variants |
| `Widgets/Catalog/Quotes/QuotesWidgetView.xaml` | **1** | Text variant |

**Fix:** Replace with `FontFamily="{DynamicResource AppDisplayFontFamily}"` or `FontFamily="{DynamicResource AppFontFamily}"`.

---

### 3. ⚠️ Hardcoded `CornerRadius` (should be `{DynamicResource ControlCornerRadius}` / `TileCornerRadius`)

The roadmap Phase 3D + Phase 5A define `ControlCornerRadius` as a live Settings toggle (0px → 2px → 4px → 8px). These hardcoded values will **not react**:

| File | Hardcoded Count | Values |
|---|---|---|
| `Widgets/Catalog/Weather/WeatherWidgetView.xaml` | 6 | `"2"`, `"0,0,2,2"` |
| `Widgets/Catalog/Habit/HabitWidgetView.xaml` | 9 | `"2"`, `"4"`, `"0"` |
| `Widgets/Catalog/Pomodoro/PomodoroWidgetView.xaml` | 5 | `"2"`, `"0,0,2,2"` |
| `Widgets/Catalog/Photos/PhotosWidgetView.xaml` | 4 | `"2"`, `"6"`, `"19"` |
| `Widgets/Catalog/Notepad/NotepadWidgetView.xaml` | 5 | `"4"`, `"9"`, `"0"` |
| `Widgets/Catalog/Calendar/CalendarWidgetView.xaml` | 2 | `"4"`, `"2"` |
| `Widgets/Catalog/Media/MediaWidgetView.xaml` | 4 | `"2"`, `"0,0,2,2"` |
| `Widgets/Catalog/BrightnessControls/BrightnessControlsWidgetView.xaml` | 3 | `"2"`, `"0,0,2,2"` |
| `Widgets/Catalog/Network/NetworkWidgetView.xaml` | 2 | `"1.5"`, `"2"` |
| `Widgets/Catalog/CaffeineSleep/CaffeineSleepWidgetView.xaml` | 5 | `"4"`, `"9"`, `"1.5"` |
| `Widgets/Catalog/Quotes/QuotesWidgetView.xaml` | 1 | `"2"` |
| `Widgets/Catalog/Dino/DinoWidgetView.xaml` | 1 | `"4"` |
| `Widgets/WidgetStyles.xaml` | 2 | `"2"` (scrollbar thumb) |

> **Note:** Some values like `"19"` (Photos circle button) and `"9"` (Notepad pill) are intentionally round/pill-shaped and may be widget-specific by design. Use judgment: standard panel corners → token; pill/circle shapes → leave hardcoded.

---

### 4. ⚠️ Duplicated `BooleanToVisibilityConverter` (Roadmap Phase 3H item 5)

The roadmap explicitly calls for a single universal `<BooleanToVisibilityConverter x:Key="BoolToVis" />` in `WidgetStyles.xaml`. Currently it's **copy-pasted in 19 locations**:

```
AudioControls, BrightnessControls, CaffeineSleep, Calendar, Clock,
Habit, Markdown, Media, Network, Notepad, Photos, Pomodoro,
QuickControls, Quotes, Radio, Rover, Weather,
TileControl, SidebarRailControl, WidgetTabStrip
```

**Fix:** Declare once in `WidgetStyles.xaml`, then remove all per-widget local declarations.

---

### 5. ⚠️ Duplicated ScrollBar Styles (Roadmap Phase 3H item 4)

6 widgets still declare their own local `<Style TargetType="{x:Type ScrollBar}">` instead of inheriting from `WidgetStyles.xaml`:

| Widget |
|---|
| `Widgets/Catalog/Radio/RadioWidgetView.xaml` |
| `Widgets/Catalog/Notepad/NotepadWidgetView.xaml` |
| `Widgets/Catalog/Network/NetworkWidgetView.xaml` |
| `Widgets/Catalog/Markdown/MarkdownWidgetView.xaml` |
| `Widgets/Catalog/BrightnessControls/BrightnessControlsWidgetView.xaml` |
| `Widgets/Catalog/AudioControls/AudioControlsWidgetView.xaml` |

---

### 6. ⚠️ Future Phase 5A Tokens Not Yet Defined

The roadmap Phase 5A specifies these settings that need **new tokens** added to `Presentation/Themes/Tokens.xaml` and consumed via `{DynamicResource}`:

| Setting | Token Key (proposed) | Current State |
|---|---|---|
| Accent Color | `AccentColorHex` | Using runtime `SystemAccentColorPrimaryBrush` — no custom override yet |
| Tile Glass Opacity | `TileGlassOpacity` | Hardcoded opacity values across 11 files |
| Reveal Glow Toggle | `EnableFluentRevealGlow` | No token, controlled in code-behind |
| Animation Mode | `AnimationMode` | Exists in `MotionTokens.cs` but not XAML-resolvable |
| Atmospheric Auras | `EnableAtmosphericAuras` | No toggle token |
| Text Scale Factor | `TextScaleFactor` | No token |

---

### Summary Priority Matrix

| Priority | Category | Files Affected | Effort |
|---|---|---|---|
| 🔴 **High** | Hardcoded `FontFamily` → `{DynamicResource}` | 4 widgets (31 instances) | Medium — find-and-replace |
| 🔴 **High** | Zero-token widgets (Calendar, Weather, Habit, Clock, Quotes) | 5 widgets | Medium — systematic |
| 🟡 **Medium** | Hardcoded `CornerRadius` → `{DynamicResource}` | 13 files (~50 instances) | Medium — needs judgment for pill/circle vs panel |
| 🟡 **Medium** | Centralize `BoolToVis` converter | 19 files | Low — mechanical |
| 🟡 **Medium** | Centralize ScrollBar styles | 6 files | Low — mechanical |
| 🟢 **Future** | Phase 5A new tokens (glass opacity, accent, aura) | Tokens.xaml + many | Part of Settings Page build |

---
---

## Critic 2: The Jonathan Blow Architectural Teardown

> *"The purpose of a system is what it does, not what you wish it did. And what this system DOES is make every class in your codebase aware of every other class. That's not architecture — that's a phone tree."*
> — Jonathan Blow, if he reviewed this codebase.

This is a brutal, honest audit of the structural sins that no amount of `{DynamicResource}` tokenization will fix. These are the things that make the difference between "talented solo dev" and "senior systems architect."

---

### Sin 1: 🔴 The God Object — `MainWindow` is 5,508 Lines Across 6 Files

```
MainWindow.xaml.cs        986 lines    ← initialization, lifecycle, hotkeys, dialogs
MainWindow.DragDrop.cs  1,757 lines    ← rubber band, tile drag, snap math
MainWindow.Tiles.cs     1,312 lines    ← tile CRUD, resize, pin/unpin, batch ops
MainWindow.CanvasGroups.cs 662 lines   ← group creation, serialization, headers
MainWindow.Backdrops.cs   515 lines    ← mica, acrylic, video wallpaper
MainWindow.Sidebar.cs     276 lines    ← sidebar toggle, rail animations
                        ─────
                        5,508 lines    ← ONE class. Six partial files.
```

**Why it's damning:** Splitting a 5,500-line class into `partial` files doesn't make it smaller. It makes it *invisible*. You didn't decompose the responsibility — you just hid the corpse in six shallow graves. `MainWindow` is still one class, one constructor, one `this` pointer, one blast radius.

Jonathan Blow would say: *"You split the file so your editor doesn't crash. That is not architecture."*

**What a senior does:** Extract `CanvasDragDropController`, `TileManager`, `GroupManager`, `BackdropService` as first-class objects that receive only what they need via constructor parameters. MainWindow becomes a thin shell that wires them together. Each is independently testable.

---

### Sin 2: 🔴 Sixteen Hand-Rolled Singletons, Zero Dependency Injection

```csharp
// Your entire service layer:
AudioService.Instance
MonitorBrightnessService.Instance
NightLightService.Instance
DisconnectService.Instance
EthernetProvider.Instance
NativeWifiService.Instance
NetworkDataUsageService.Instance
NetworkHealthService.Instance
ThroughputService.Instance
PowerAwakeService.Instance
RadioAudioService.Instance
RadioBrowserClient.Instance
RadioCatalogService.Instance
StreamUrlProbeService.Instance
DailyWallpaperService.Instance
CinematicFadeService.Instance
WidgetStateStore.Default
MainWindow.Current                ← The king singleton
```

**Zero `IServiceProvider`. Zero `AddSingleton`. Zero `AddTransient`. Zero constructor injection.**

Every ViewModel reaches into the static void and grabs whatever it wants:

```csharp
public AudioControlsWidgetViewModel(TileModel model) : base(model)
{
    _audioService = AudioService.Instance;  // ← hard coupling to global state
}
```

**Why it's damning:** This means:
- **You cannot unit test any ViewModel in isolation.** Every test activates real Win32 audio APIs, real WiFi services, real brightness hardware. Your 168 tests? They test what they *can* — persistence, catalogs. The core ViewModels are **untestable**.
- **You cannot swap implementations.** Want a mock audio service for a demo mode? A fake network service for offline dev? Impossible without rewriting every constructor.
- **Startup order is invisible.** Which `Lazy<T>` initializes first? What happens when Service A's constructor touches Service B which isn't alive yet? You're one refactor away from a circular initialization deadlock and you won't know until runtime.

**Interface-to-class ratio: 22 interfaces across 205 classes = 10.7%.** Industry baseline for testable code is 30-50%. You have interfaces for `IWidgetViewModel`, `IRadioAudioService`, `ITile` — but the other 190+ classes? Concrete-to-concrete all the way down.

---

### Sin 3: 🔴 The Mega-Modal — `AcrylicModalWindow` is 3 Apps in a Trenchcoat

**1,876 lines of C#. 904 lines of XAML.** One class. Three completely unrelated features:

| Mode | What it does | Lines |
|---|---|---|
| `WeatherLocation` | Geocoding search with debounce, OpenMeteo + Photon API calls, auto-location, suggestion list | ~700 |
| `AddWebLink` | URL validation, favicon extraction, title parsing | ~250 |
| `AddRadioStation` | Radio browser search, stream URL probing, category tabs, direct URL paste | ~500 |

It even has **DTOs (data transfer objects) defined inside the code-behind**:

```csharp
// Line 1289 of a XAML code-behind file:
internal sealed record GeoResult(...)
internal sealed record OpenMeteoGeocodingResponse(...)
internal sealed record PhotonResponse(...)
internal sealed record PhotonFeature(...)
```

**Why it's damning:** The modal is making HTTP calls. It's parsing JSON. It's doing geocoding. It's probing audio streams. A UI control should not know what the Photon geocoding API response format looks like.

**What a senior does:** `WeatherLocationDialog`, `WebLinkDialog`, `RadioStationDialog` — three classes, each < 200 lines. DTOs live in `Core/Models/`. HTTP calls live in services. The dialog just binds to a ViewModel.

---

### Sin 4: 🟡 Event Subscription Leak Ratio — 317 `+=` vs 113 `-=`

```
Event subscriptions (+=):    317
Event unsubscriptions (-=):  113
Delta (potential leaks):     204
```

**204 event handlers that are subscribed but never explicitly unsubscribed.** Not all are leaks — some are lifecycle-bound (loaded once, lives forever). But a 35% unsubscribe rate means you're *hoping* the GC and WPF's weak event infrastructure save you, rather than *knowing* your cleanup is correct.

For a desktop app that runs for hours/days, this is how you get the "MetroHub is using 1.2 GB after running overnight" bug reports.

---

### Sin 5: 🟡 106 `Dispatcher.Invoke` / `BeginInvoke` Calls

Every service that does background work manually marshals back to the UI thread:

```csharp
Application.Current.Dispatcher.BeginInvoke(() => { ... });
```

**106 times.** This is the WPF equivalent of `setTimeout` spam. It means your services are not cleanly separated from the UI thread — they *know* they're running in a WPF app and explicitly depend on the Dispatcher.

**What a senior does:** Services raise events or use `IProgress<T>`. The *consumer* (View/ViewModel) decides how to marshal. Or use `CommunityToolkit.Mvvm`'s `ObservableObject` which automatically handles `INotifyPropertyChanged` marshaling.

---

### Sin 6: 🟡 `NetworkWidgetViewModel` — 2,615 Lines in a Single ViewModel

This one file is larger than many entire applications. It contains:

- WiFi scanning and connection logic
- Ethernet detection
- Throughput monitoring
- Data usage tracking
- Network health scoring
- Speed test orchestration
- Disconnect/reconnect flows
- UI state for 1,775 lines of XAML

It directly instantiates **6 singleton services** in its field declarations:

```csharp
private readonly EthernetProvider _ethernetProvider = EthernetProvider.Instance;
private readonly NativeWifiService _wifiService = NativeWifiService.Instance;
private readonly ThroughputService _throughputService = ThroughputService.Instance;
private readonly DisconnectService _disconnectService = DisconnectService.Instance;
private readonly NetworkHealthService _healthService = NetworkHealthService.Instance;
private readonly NetworkDataUsageService _dataUsageService = NetworkDataUsageService.Instance;
```

This isn't a ViewModel — it's a **network management dashboard monolith** that happens to implement `INotifyPropertyChanged`.

---

### Sin 7: 🟡 55 Hardcoded Color Hex Values in C# Code-Behind

```csharp
// Scattered across the codebase:
"#FF2D2D"   "#00E676"   "#1E1E2E"   "#0A0A12"   ...
```

55 color values baked directly into C# files, unreachable by any theming system, invisible to any Settings page, impossible to change without recompiling. Some of these are in ViewModels — meaning your business logic layer has opinions about what shade of green means "connected."

---

### Sin 8: 🟢 The Tests Don't Test What Matters

```
168 tests passing ✅
```

Sounds great. But what do they cover?

| What's tested | What's NOT tested |
|---|---|
| Radio catalog parsing | Any ViewModel lifecycle |
| Markdown persistence | Drag-and-drop behavior |
| File save/load | Tile resize/pin/unpin |
| Clock fonts | Any UI control interaction |
| Heartbeat timing | Group creation/deletion |
| App paths | Settings save/load roundtrip |

**Zero ViewModel constructor-to-teardown tests.** Because they can't — every ViewModel grabs `AudioService.Instance` or `NativeWifiService.Instance` in its constructor, which touches real hardware. You literally cannot `new up` a ViewModel in a test without a running Windows desktop with audio hardware and network adapters.

The tests you *do* have are good — persistence, catalog parsing, edge cases. But the core behavioral surface of the app (what happens when a user clicks, drags, resizes, groups, pins) is 100% untested.

---

### The Verdict

| Sin | Severity | Jonathan Blow Quote |
|---|---|---|
| God Object `MainWindow` (5,508 lines) | 🔴 Critical | *"Partial classes are how you lie to yourself about complexity."* |
| 16 manual singletons, zero DI | 🔴 Critical | *"If you can't test it, you don't know if it works. You just know it hasn't crashed yet."* |
| Mega-Modal (1,876 lines, 3 features) | 🔴 Critical | *"This isn't a dialog — it's a microservice with a close button."* |
| 204 potentially leaked event handlers | 🟡 Serious | *"Hope-driven garbage collection."* |
| 106 Dispatcher.Invoke calls | 🟡 Serious | *"Your services know they live in WPF. That's a dependency, not an architecture."* |
| NetworkViewModel 2,615 lines | 🟡 Serious | *"That's not a ViewModel, that's a screenplay."* |
| 55 hardcoded colors in C# | 🟡 Serious | *"You hardcoded aesthetic decisions into your logic layer. Interior designers don't weld furniture to the floor."* |
| Tests don't cover core behavior | 🟢 Important | *"168 green checkmarks testing the easy parts."* |

---

### What "Senior Lead Architect" Looks Like

1. **Introduce DI.** `Microsoft.Extensions.DependencyInjection`. Register services. Inject via constructors. Delete every `.Instance` property. This alone makes every ViewModel testable.
2. **Decompose MainWindow.** Extract `TileManager`, `CanvasDragDropController`, `GroupManager`, `BackdropManager`. MainWindow becomes < 300 lines.
3. **Split the Mega-Modal.** Three dialog classes. DTOs in `Core/Models/`. HTTP in services. Dialogs bind to ViewModels.
4. **Audit event handlers.** Every `+=` needs a corresponding `-=` in a cleanup path, or use `WeakEventManager`.
---

## Critic 3: Top 4 Remaining Blunders (Theming & Threading)

> Audited against master design tokens in `Tokens.xaml` and performance standards in `WPF_PERFORMANCE_Deepseek.md`.

---

### Blunder 1: 🟢 Canvas Drag-and-Drop & Marquee Selection Box Hardcoded to `#60CDFF` (Sky Blue) [FIXED]

**Files & Locations:**
- `src/MetroHub/MainWindow.xaml`:
  - Line 440: `<Border x:Name="DropSlotIndicator" Background="#3060CDFF">`
  - Line 443: `<DropShadowEffect ... Color="#60CDFF" />`
  - Line 452: `<Rectangle x:Name="RubberBandBox" Fill="#2560CDFF" />`
  - Line 465: `<Border x:Name="GroupDropPerimeterBorder" BorderBrush="#60CDFF">`
  - Line 466: `Background="#1060CDFF"`
  - Line 477: `<DropShadowEffect x:Name="GroupDropGlowEffect" ... Color="#60CDFF" />`
  - Line 536: `<DropShadowEffect ... Color="#60CDFF" />`

**The Problem:**
These are the central canvas interactions for the whole application. If a user selects Emerald Green, Purple, or Amber in Settings, dropping a tile, marquee selecting tiles, or hovering over groups still flashes hardcoded `#60CDFF` sky blue instead of their chosen accent color.

**The Fix:**
- Replace `#60CDFF` border and drop shadow colors with `{DynamicResource SystemAccentColorPrimaryBrush}` or `{DynamicResource SystemAccentColorSecondaryBrush}` (or `{DynamicResource SystemAccentColor}`).
- Replace alpha-blended fills (`#3060CDFF`, `#2560CDFF`, `#1060CDFF`) with centralized accent tint tokens or dynamic opacity brushes.

---

### Blunder 2: 🟢 "Today" Active Date Pill in Calendar & Habit Hardcoded to `#60CDFF` [FIXED]

**Files & Locations:**
- `src/MetroHub/Widgets/Catalog/Calendar/CalendarWidgetView.xaml:139`:
  - `<Border x:Name="TodayPill" ... Background="#60CDFF" />`
- `src/MetroHub/Widgets/Catalog/Habit/HabitWidgetView.xaml:344`:
  - `<Border x:Name="TodayPill" ... Background="#60CDFF" />`
- `src/MetroHub/Presentation/Themes/Tokens.xaml:142`:
  - `<DropShadowEffect x:Key="GlowDotSmall" ... Color="#60CDFF" x:Shared="False" />`

**The Problem:**
The active day indicator pill in both Calendar and Habit widgets is pinned to `#60CDFF`. When the app's accent theme changes, the calendar date indicator remains blue. Furthermore, `GlowDotSmall` in `Tokens.xaml` is hardcoded to `#60CDFF` rather than resolving from `SystemAccentColor`.

**The Fix:**
- In `CalendarWidgetView.xaml` and `HabitWidgetView.xaml`: Bind `TodayPill.Background` to `{DynamicResource SystemAccentColorPrimaryBrush}`.
- In `Tokens.xaml`: Link `GlowDotSmall.Color` to `{DynamicResource SystemAccentColor}` or provide dynamic accent glow tokens.

---

### Blunder 3: 🟢 Synchronous `Dispatcher.Invoke` in Background Tasks (UI Stutter & Deadlock Risk) [FIXED]

**Files & Locations:**
- `src/MetroHub/Presentation/Controls/RadioStationDialog.xaml.cs`:
  - Lines 379, 422: `Dispatcher.Invoke(() => ...)` during station search
  - Lines 526, 577: `Dispatcher.Invoke(() => ...)` during direct stream probing
- `src/MetroHub/Presentation/Controls/WebLinkDialog.xaml.cs`:
  - Lines 272, 281, 314: `Dispatcher.Invoke(() => ...)` during webpage scraping & title resolution

**The Problem:**
Background threads running network tasks (`Task.Run`) marshal back to the UI thread using synchronous `Dispatcher.Invoke`. This halts the background worker thread until the UI thread message queue finishes executing the delegate. If the UI thread is busy rendering an animation, handling window drag, or waiting on another lock, this introduces UI stutter, frame drops, or potential deadlocks.

Per `WPF_PERFORMANCE_Deepseek.md` Rule 6: *"The UI thread is for rendering and input. Everything else goes to a threadpool. Never block on Task.Result or Task.Wait(). Marshal non-blocking via InvokeAsync."*

**The Fix:**
Replace synchronous `Dispatcher.Invoke(() => { ... })` with non-blocking `Dispatcher.InvokeAsync(() => { ... })` or `await Dispatcher.InvokeAsync(...)`.

---

### Blunder 4: 🟢 Hardcoded Semi-Transparent Overlays in Local Widget Button Styles [FIXED]

**Files & Locations:**
- `src/MetroHub/Widgets/Catalog/AudioControls/AudioControlsWidgetView.xaml:37, 40, 273, 277` (`#18FFFFFF`, `#28FFFFFF`, `#1EFFFFFF`, `#25FFFFFF`)
- `src/MetroHub/Widgets/Catalog/CaffeineSleep/CaffeineSleepWidgetView.xaml:20, 21, 47, 48` (`#1CFFFFFF`, `#2AFFFFFF`, `#30FFFFFF`, `#48FFFFFF`)
- `src/MetroHub/Widgets/Catalog/Photos/PhotosWidgetView.xaml:17, 18, 43, 47` (`#22FFFFFF`, `#35FFFFFF`, `#3DFFFFFF`, `#55FFFFFF`)
- `src/MetroHub/Widgets/Catalog/Radio/RadioWidgetView.xaml:112, 118` (`#55FFFFFF`, `#75FFFFFF`)

**The Problem:**
Individual widgets continue to invent one-off hex colors for button hover, pressed, and card border states instead of consuming central design tokens. `Tokens.xaml` and `WidgetStyles.xaml` already declare standardized tokens:
- `FluentHoverBrush` (`#12FFFFFF`)
- `FluentPressedBrush` (`#1EFFFFFF`)
- `WidgetDividerBrush` (`#14FFFFFF`)
- `WidgetBorderBrush`

Local hardcoded hex values create inconsistent button hover brightness between widgets and prevent global opacity/glass adjustments from applying evenly across the hub.

**The Fix:**
Replace local hardcoded hover and pressed hex values with `{DynamicResource FluentHoverBrush}`, `{DynamicResource FluentPressedBrush}`, and `{DynamicResource WidgetDividerBrush}`.
