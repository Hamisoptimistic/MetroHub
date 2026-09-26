# Focus & Ambient Radio Widget — Technical Architecture & Master Plan
====================================================================

## Executive Summary
This document defines the complete architectural blueprint, user experience specification, and phase-by-phase implementation roadmap for MetroHub's **Focus Radio & Ambient Sounds** widget (`Huge` 8x6 layout).

Authored from first principles and grounded in the exact source code of MetroHub:
* **True Canvas Geometry:** Exact `Huge` size is **504 × 376 px** (`(SpanX * 64) - 8` by `(SpanY * 64) - 8` per `TileModel.cs:261`). Main station grid receives **236px** vertical height and **126px** per column across 4 columns.
* **Audio Engine:** Modern Windows 10/11 `Windows.Media.Playback.MediaPlayer` (WinRT), natively handling remote HTTP/HTTPS, Icecast, and Shoutcast streams without legacy WPF pack URI security locks (`0x80131509`).
* **Settings & State Architecture:** Singleton `IRadioAudioService` manages global playback state (station, play/pause, volume, mute) across all widget instances. Per-tile settings (`TileModel.SettingsJson`) store local UI preferences (`LastSelectedCategory`).
* **Asset Convention:** Embedded under `Assets\Radio\radio_catalog.json` with `<Resource Include>` in `MetroHub.csproj`.
* **UI Structure:** Seamless border-to-border 4-column station grid matching the top `WidgetTiles` strip.

---

## 1. System Architecture & Decisions Log

| Decision Area | Architectural Choice | First-Principles & Codebase Rationale |
| :--- | :--- | :--- |
| **Widget Grid Size** | `Huge` (8x6: **504 × 376 px**) | Matches `TileModel.cs:261` exactly: `Width = (8*64)-8 = 504px`, `Height = (6*64)-8 = 376px`. Top bar: 76px, Player bar: 64px, Main grid: 236px height (126px per column). |
| **Audio Engine** | Modern Windows 10/11 `Windows.Media.Playback.MediaPlayer` | Built into Windows (`net10.0-windows10.0.19041.0`); 0 extra NuGet packages; natively supports remote HTTP/HTTPS, Icecast, Shoutcast, AAC/MP3 live streams without legacy WPF pack URI security locks (`0x80131509`). Verified working on live Icecast & Shoutcast hosts in spike tests. |
| **Stream Catalog Storage** | Embedded Application Resource | Embedded at `Assets\Radio\radio_catalog.json` as `<Resource Include>` in `MetroHub.csproj` for 100% offline day-1 startup. |
| **Live Stream "Pause"** | Clean Socket Disconnect with 120ms Fade | Live 24/7 Icecast/Shoutcast streams cannot pause a timeline. Socket is cleanly torn down to save user bandwidth/data. Resumes live on Play. |
| **Station Switching** | `CancellationTokenSource` + 180ms Debounce | Prevents socket pile-up, audio overlapping, and MediaFoundation locks when a user rapidly clicks multiple station tiles. |
| **Station Tiles Layout** | Seamless Border-to-Border Grid | 0 margin, 0 outer padding. Tiles pack border-to-border with 1px subtle hairline dividers, matching the top `WidgetTiles` philosophy. 126px cell width with `TextTrimming="CharacterEllipsis"` for long titles. Hover reveals Fluent border; active tile shows vibrant accent border. |
| **Top Category Bar** | Extended `WidgetTiles` with Horizontal Scroll | Exact visual parity with Network Widget (hardware-accelerated sliding green indicator and hover effects), supporting overflow categories. |
| **Bottom Player Bar** | Left-to-Right Balanced Layout | **Left:** Prev, Play/Pause (accent circular), Next; **Center:** Station Name + Bitrate badge; **Right:** Mute + Fluent Volume Slider. |
| **Custom Station Addition** | Dedicated `+` Tile Placeholder | UI placeholder tile at the end of each category grid. Custom URL input & CRUD logic will be wired in a subsequent phase per user request. |
| **Multi-Instance & Volume Architecture** | Application Singleton `IRadioAudioService` | Audio playback, Volume, and Mute are app-wide global states managed by the singleton service. All widget ViewModels bind to this service. Per-tile `TileModel.SettingsJson` stores local `LastSelectedCategory`. |

---

## 2. 50 Edge Cases, Failure Modes & Resilience Matrix

### A. Networking & Stream Transport (1–12)
1. **Network Disconnection During Playback:** Wi-Fi drops or Ethernet unplugged. Player enters `Buffering` state with timeout before signaling error without crashing.
2. **IP Address Switching (Wi-Fi to Ethernet Transition):** Socket connection breaks as OS routes change. Engine detects `MediaFailed` and auto-reconnects cleanly.
3. **DNS Resolution Latency & Timeout:** Stream hostname lookup stalls on slow DNS servers. Engine wraps connection in a cancellation timeout.
4. **Geo-Blocked or Region-Restricted Streams:** Server responds with HTTP 403 Forbidden. Widget marks station tile with warning state and notifies user.
5. **Slow / High-Jitter Connections:** Network bandwidth drops below stream bitrate. WinRT `BufferingStarted` fires and updates UI state.
6. **HTTP to HTTPS Redirects:** Some Icecast servers redirect unencrypted HTTP to SSL ports. MediaSource resolver follows 301/302 redirects seamlessly.
7. **Non-Standard Streaming Ports:** Stations hosted on ports like `:8000`, `:8835`, `:9050` (e.g. Shoutcast/Centova). Verified working in WinRT spike tests.
8. **Shoutcast Non-Standard ICY Metadata Handshake:** Tested and confirmed working via WinRT `MediaSource.CreateFromUri` across ViaStreaming, Centova, and FastCast4u hosts.
9. **Dead Stream / 404 Not Found:** Station URL went dark. Handled via `MediaFailed` event; notifies user and allows one-click skip to next station.
10. **SSL Certificate Expiration on Radio Hosts:** Radio servers with expired certs handled without crashing the host process.
11. **VPN Connect / Disconnect While Playing:** Socket abruptly aborted by TAP/TUN adapter reconfiguration. Auto-reconnection logic restarts socket stream.
12. **Metered Network Usage Detection:** When Windows is on a "Metered Connection", user is informed if streaming 320 kbps high-bitrate audio.

### B. Audio Pipeline & Windows Hardware (13–22)
13. **Audio Output Device Unplugged (Headphones Disconnected):** Default audio endpoint changes to laptop speakers. Windows Core Audio routes smoothly without throwing unhandled exceptions.
14. **Bluetooth Audio Latency / Re-pairing:** Bluetooth devices reconnect with latency. WinRT MediaPlayer manages device routing automatically.
15. **Exclusive Audio Mode Conflict:** Another application claims exclusive audio device lock. Engine catches `MediaFailed` with `0x88890004` (`AUDCLNT_E_DEVICE_IN_USE`) and shows "Audio Device In Use".
16. **System Sleep / Modern Standby (S0ix) / Hibernate:** PC enters sleep while radio is playing. On system resume, stream socket is re-established live.
17. **Volume Mismatch on Startup:** User set volume to 10% in widget. Widget sets `player.Volume` linearly without altering master Windows volume.
18. **Digital Pops & Clicks on Abrupt Stops:** Sudden cut of an audio waveform causes harsh speaker pop. Engine applies a 120ms volume fade-down before stopping.
19. **Resource Leaks on Stream Switching:** Calling `Dispose()` on old `MediaSource` before assigning a new one prevents memory accumulation.
20. **Zero-Volume Playback Resource Waste:** Volume set to 0 for more than 10 minutes. Engine pauses network stream to prevent wasting user internet bandwidth silently.
21. **High Sample Rate Output (96kHz / 192kHz DACs):** Audio engine plays through Windows Core Audio WASAPI mixer without pitch distortion.
22. **Mono vs Stereo Streams:** Vintage radio streams in mono correctly route to both stereo output channels.

### C. Concurrency, Threading & State Machine (23–30)
23. **Rapid Tile Clicking (Clicking 10 Stations in 2 Seconds):** Debounced with 180ms timer and atomic cancellation of the preceding stream initialization.
24. **Simultaneous Play and Pause Click (Double Click Glitch):** State machine enforces atomic state transitions (`Idle` -> `Connecting` -> `Playing` -> `Stopping` -> `Idle`).
25. **Dispatcher Thread Starvation:** Media events raised on worker threads are dispatched to UI thread using `DispatcherPriority.Normal` to guarantee smooth 60/120fps UI rendering.
26. **Disposal During Active Playback:** Widget removed or app closed while stream is connecting. Destructor safely cancels active tokens and disposes player.
27. **Station Switch While Muted:** Next station starts in Muted state without un-muting unexpectedly.
28. **Category Tab Switch While Playing:** Switching category tabs does NOT interrupt playback of the currently playing station. The active station remains playing in the background.
29. **Next Button at End of Category List:** Clicking "Next" on the last station loops back smoothly to the first station in the current category.
30. **Previous Button at Beginning of Category List:** Clicking "Previous" on the first station wraps around to the last station in the current category.

### D. WPF Rendering, Layout & Performance (31–40)
31. **Efficient Idle Execution:** When playback is paused or running, no unbounded background polling loops or heavy layout recalculations run.
32. **Seamless Border-to-Border Layout:** Radio tiles pack tightly in a 504px canvas (4 columns of 126px) with 0 outer margin and 1px hairline dividers.
33. **Hover Animation Efficiency:** Tile hover reveal borders use frozen brushes and static storyboards, eliminating allocation churn on mouse move.
34. **Text Truncation in 126px Cells:** Station names exceeding 126px cell width use `TextTrimming="CharacterEllipsis"` with full title in `ToolTip`.
35. **DPI Awareness (100%, 125%, 150%, 200%):** Vector Fluent glyphs (`SymbolRegular`) render crisp with zero pixelation or blurry edges across multi-monitor setups.
36. **Font Fallbacks:** Display titles fall back cleanly to `Segoe UI Variable` / `Segoe UI` if custom fonts are missing.
37. **Scrollbar Auto-Hide:** Uses MetroHub's universal 4px floating scrollbar style that remains invisible until mouse movement/scroll.
38. **Touchscreen Tap Support:** Tiles support touch tap and mouse click identically via standard RoutedEvents.
39. **Keyboard Navigation & Accessibility:** Arrow keys move between tiles; Enter/Space activates station.
40. **Window Dragging Over Tiles:** Clicking a tile plays the radio; clicking the card margin or header allows dragging the widget.

### E. Persistence, Settings & Serialization (41–50)
41. **Corrupted Settings Recovery:** If `TileModel.SettingsJson` contains malformed JSON, deserializer catches the error and initializes default settings cleanly.
42. **First-Run Cold Launch:** Clean default state if no settings exist on the tile yet.
43. **Settings Isolation:** Each Radio widget tile instance stores its own UI view state (`LastSelectedCategory`) in its own `TileModel.SettingsJson`.
44. **Special Characters in Station Names:** Handles emojis, Japanese kanji (e.g. for Zen Garden), and unicode without serialization bugs.
45. **URL Encoding / Trailing Slashes:** Links with trailing slashes, commas, or query parameters parsed safely.
46. **Schema Upgrades / Versioning:** Adding new fields to `RadioWidgetSettings` in future updates maintains backwards-compatible JSON deserialization.
47. **Multi-Instance Synchronization:** All widget instances share the same singleton `IRadioAudioService` and broadcast Play/Pause, Station, and Volume changes across all instances.
48. **Volume Clamping:** Volume persisted as double `0.0` to `1.0`; clamped upon load to prevent out-of-range slider states.
49. **Category Selection Persistence:** Remembers the active category tab on app restart via `TileModel.SettingsJson`.
50. **Thread-Safe Settings Saving:** Widget settings updates are invoked through the standard `SaveSettings()` mechanism on `WidgetViewModelBase`.

---

## 3. UI/UX Wireframe & Real Pixel Canvas Math

```
+-----------------------------------------------------------------------------------+
|  [Huge: 8x6]  AMBIENT & FOCUS RADIO (Exact Canvas: 504px x 376px)                 |
+-----------------------------------------------------------------------------------+
| TOP CATEGORY BAR (Height: 76px, Extended WidgetTiles with Smooth Horizontal Pan)  |
|  [ Ambient (15) ]  |  [ Nature (10) ]  |  [ Lo-Fi / Chill (10) ]  |  [ Coding (5) ] |
|  ============= sliding green indicator (Hardware-Accelerated TranslateTransform)   |
+-----------------------------------------------------------------------------------+
| MAIN TILES SECTION (Height: 236px, Seamless 4-Col Grid: 126px per column)         |
|                                                                                   |
|  +----------------+----------------+----------------+----------------+            |
|  | Birdsong FM    | Rain & Thunder | Ambi Nature HD | Ocean Waves    |            |
|  | [128k]         | [128k]         | [320k]         | [160k]         |            |
|  +----------------+----------------+----------------+----------------+            |
|  | Rainy Day      | Pure Nature    | Zen Garden     | Sleep Waves    |            |
|  | [NOW PLAYING]  | [160k]         | [160k]         | [64k]          |            |
|  | (Accent Border)|                |                |                |            |
|  +----------------+----------------+----------------+----------------+            |
|  | Nature Rain    | NTS Field Rec. |       +        |                |            |
|  | [128k]         | [256k]         |  (Add Stream)  |                |            |
|  |                |                |  (Placeholder) |                |            |
|  +----------------+----------------+----------------+----------------+            |
+-----------------------------------------------------------------------------------+
| BOTTOM FLUENT PLAYER BAR (Height: 64px, Acrylic Backdrop, Hairline Divider)       |
|                                                                                   |
|  [ |< ] [ ( > ) ] [ >| ]      SomaFM Drone Zone [LIVE 128k]     [ MUTE ] [====O=] |
|   Prev   Play/Pause  Next       (Station Name & Bitrate Badge)     Mute    Volume |
+-----------------------------------------------------------------------------------+
```

---

## 4. Phase-by-Phase Implementation Roadmap

### Phase 1: Core Domain Models & Embedded Catalog Resource
- Create `MetroHub.Core.Radio` models:
  - `RadioStation`: `Id`, `Name`, `StreamUrl`, `BitrateKbps`, `Category`, `Icon`.
  - `RadioCategory`: `Id`, `DisplayName`, `Icon`, `Stations`.
- Create bundled `Assets\Radio\radio_catalog.json` containing all 40 verified streams across the 4 categories.
- Add `<Resource Include="Assets\Radio\radio_catalog.json" />` in `MetroHub.csproj`.
- Build `RadioCatalogService` to load the embedded catalog resource.

### Phase 2: High-Performance Audio Playback Service
- Implement `RadioAudioService` (singleton `IRadioAudioService`) encapsulating modern Windows 10/11 `Windows.Media.Playback.MediaPlayer`:
  - Direct `MediaSource.CreateFromUri` supporting Icecast/Shoutcast/HLS/MP3/AAC.
  - Native buffering diagnostics (`BufferingStarted`, `BufferingEnded`).
  - Methods: `PlayAsync(RadioStation station)`, `Pause()`, `Stop()`, `SetVolume(double vol)`, `SetMute(bool mute)`.
  - Cancellation token & 180ms debounce on station switching.
  - 120ms fade-out before disconnect.
  - Reactive state events: `PlaybackStateChanged`, `BufferingChanged`, `VolumeChanged`, `PlaybackFailed`.

### Phase 3: RadioWidgetViewModel & Settings
- Create `RadioWidgetSettings` record: `LastSelectedCategory`.
- Create `RadioWidgetViewModel` inheriting from `WidgetViewModelBase`:
  - Observable properties: `CurrentCategory`, `CurrentStation`, `IsPlaying`, `IsBuffering`, `Volume`, `IsMuted`.
  - Relay commands: `SelectStationCommand`, `TogglePlayPauseCommand`, `NextStationCommand`, `PreviousStationCommand`, `ToggleMuteCommand`.
  - Volume & Playback synchronizes with singleton `IRadioAudioService`.
  - Persistence: Persists `LastSelectedCategory` via `TileModel.SettingsJson` using `WidgetSerializer`.
  - Loop logic for Next/Previous within the active category.

### Phase 4: Modern Fluent WPF View (`RadioWidgetView.xaml`)
- Row 0: Category navigation using `WidgetTiles` and `WidgetTile` (76px, matching Network widget layout).
- Row 1: Seamless 4-Column Station Grid (236px height, 126px per column) with 0 outer margin/padding, 1px hairline dividers, Fluent hover reveal border, active accent border, bitrate badge, character ellipsis for long names, and the `+` placeholder tile at the end.
- Row 2: Bottom Player Bar (64px height):
  - Left: Prev, circular Play/Pause accent button, Next.
  - Center: Station name marquee + `[LIVE]` bitrate badge.
  - Right: Fluent Mute button + volume slider with volume percentage tooltip.

### Phase 5: Registration & System Wiring
- Register widget definition in `WidgetRegistry.cs`:
  - ID: `radio`
  - DisplayName: `Focus & Ambient Radio`
  - AllowedSize: `WidgetSize.Huge` (504 × 376 px)
  - Icon: `SymbolRegular.Radio24`
  - Category: `Lifestyle`
- Register `RadioWidgetSettings` in `WidgetJsonContext.cs` for fast AOT JSON serialization.

### Phase 6: Automated Live Stream & Integration Testing, Verification & Build
- Integration suite: Automated live playback test across representative streams in all 4 categories (Icecast, Shoutcast, Radio.co, AzuraCast).
- Verify state transitions, rapid tile switching, and singleton volume synchronization across ViewModels.
- Verify exact 504 × 376 px canvas layout in running app.
- Run complete test suite (`dotnet test`) and compile release binaries.
