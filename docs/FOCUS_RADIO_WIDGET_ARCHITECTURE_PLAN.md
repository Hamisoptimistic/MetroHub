# Focus & Ambient Radio Widget — Technical Architecture & Master Plan
====================================================================

## Executive Summary
This document defines the complete architectural blueprint, user experience specification, and phase-by-phase implementation roadmap for MetroHub's **Focus Radio & Ambient Sounds** widget (`Huge` 8x6 layout).

Authored from first principles and grounded in the exact source code of MetroHub:
* **True Canvas Geometry:** Exact `Huge` size is **504 × 376 px** (`(SpanX * 64) - 8` by `(SpanY * 64) - 8` per `TileModel.cs:261`). Main station grid receives **236px** vertical height and **126px** per column across 4 columns.
* **Audio Engine:** Solution A — The Audio App Gold Standard: **BASS Audio Engine** via `ManagedBass` and native 64-bit `bass.dll` + `bass_aac.dll`, completely replacing WinRT `MediaPlayer` to eliminate Media Foundation buffer stalls and range-probe deadlocks on endless live chunked streams (e.g. Radio.co / Birdsong FM).
* **Settings & State Architecture:** Singleton `IRadioAudioService` manages global playback state (station, play/pause, volume, mute) across all widget instances. Per-tile settings (`TileModel.SettingsJson`) store local UI preferences (`LastSelectedCategory`).
* **Asset Convention:** Embedded under `Assets\Radio\radio_catalog.json` with `<Resource Include>` in `MetroHub.csproj`.
* **UI Structure:** Seamless border-to-border 4-column station grid matching the top `WidgetTiles` strip.

---

## 1. System Architecture & Decisions Log

| Decision Area | Architectural Choice | First-Principles & Codebase Rationale |
| :--- | :--- | :--- |
| **Widget Grid Size** | `Huge` (8x6: **504 × 376 px**) | Matches `TileModel.cs:261` exactly: `Width = (8*64)-8 = 504px`, `Height = (6*64)-8 = 376px`. Top bar: 76px, Player bar: 64px, Main grid: 236px height (126px per column). |
| **Audio Engine** | Solution A: BASS Audio Engine (`ManagedBass` + Native BASS 2.4 x64) | Replaces WinRT `MediaPlayer` to eliminate Media Foundation buffer stalls on endless chunked streams (e.g. Radio.co / Birdsong FM). Provides direct socket streaming, decoupled 5s net buffer, 10s timeout, custom User-Agent, instant ICY metadata, and 120ms hardware volume fade. |
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

---

## 5. Solution A: BASS Audio Engine Migration Blueprint & Technical Specification

### 5.1 Context & Technical Root Cause Analysis
During live testing across diverse radio streams, stations such as **Birdsong FM** (`https://a1.radio.co/s5c5da6a36/listen`) and **9128.live** (`https://streams.radio.co/s0aa1e6f4a/listen`) exhibited persistent buffering or failure to initialize under Windows Media Foundation (`Windows.Media.Playback.MediaPlayer`).

#### The Media Foundation Bottleneck:
1. **Container Probing & Range Requests:** Windows Media Foundation's `IMFSourceResolver` issues HTTP `HEAD` and byte-range probe requests (`Range: bytes=0-1`) to detect media container length, Xing headers, and seekability.
2. **Chunked Stream Deadlock:** Live edge streams (such as Radio.co, AzuraCast, and Icecast chunked relays) respond with `Transfer-Encoding: chunked` and `Accept-Ranges: none`. Media Foundation stalls waiting for fixed stream headers that do not exist in continuous live audio.
3. **Missing Desktop User-Agent:** WinRT `MediaPlayer` does not send standard desktop browser `User-Agent` headers by default, triggering 403 Forbidden or silent connection drops from Cloudflare and edge CDNs protecting audio servers.

#### The BASS Gold Standard:
Used by the world's leading audio players (**AIMP, MusicBee, XMPlay, Winamp, RadioBOSS**), the **BASS Audio Engine** by Un4seen Developments is specifically architected for endless internet audio streams:
* **Direct Socket Transport:** Reads directly from raw TCP/HTTP sockets without requiring file length or seek tables.
* **Decoupled Buffer Architecture:** Separates the network reception buffer (`BASS_CONFIG_NET_BUFFER`) from the audio output mixing buffer (`BASS_CONFIG_BUFFER`), eliminating buffer underruns during network jitter.
* **Hardware WASAPI Direct Output:** Renders through Windows Core Audio WASAPI endpoints with sub-millisecond precision and native volume fading.

---

### 5.2 Modern .NET 10 & Windows Standards Architecture

```
+-----------------------------------------------------------------------------------+
|                           MetroHub Radio Architecture                             |
+-----------------------------------------------------------------------------------+
|  [RadioWidgetView.xaml] <---> [RadioWidgetViewModel.cs]                           |
|                                       |                                           |
|                           [IRadioAudioService] (Public API Preserved)             |
|                                       |                                           |
|                           [RadioAudioService.cs]                                  |
|                                       |                                           |
|                           [ManagedBass (v4.1.0-prerelease - .NET 10 Native)]      |
|                                       |                                           |
|         [NativeLibrary.SetDllImportResolver] (Modern .NET 10 P/Invoke)            |
|                                       |                                           |
|   +-----------------------------------+-----------------------------------+       |
|   | bass.dll (x64 v2.4.18)            | bass_aac.dll (x64 v2.4.7)         |       |
|   | Core MP3/OGG/HTTP Engine          | AAC / AAC+ / M4A Decoder Plugin   |       |
|   +-----------------------------------+-----------------------------------+       |
|                                       |                                           |
|                       [Windows Core Audio / WASAPI]                               |
+-----------------------------------------------------------------------------------+
```

#### Component Stack:
* **Target Framework:** `net10.0-windows10.0.19041.0` (x64 architecture).
* **Managed Wrapper:** `ManagedBass` (v4.1.0-prerelease via NuGet) — latest cutting-edge release featuring explicit, first-class `.net10.0` TFM support.
* **Native Binaries:** Official Un4seen 64-bit binaries:
  * `bass.dll` (Core engine: MP3, OGG, WAV, Icecast, Shoutcast, HTTP/HTTPS direct streaming).
  * `bass_aac.dll` (Addon plugin: AAC, AAC+, ADTS, and MP4 container streaming).
* **Native Binary Layout in Repository:**
  ```
  d:\MetroHub\
  ├── lib\
  │   └── native\
  │       └── win-x64\
  │           ├── bass.dll
  │           └── bass_aac.dll
  ```
* **MSBuild Deployment Rule (`MetroHub.csproj` & `MetroHub.Tests.csproj`):**
  ```xml
  <ItemGroup>
    <None Include="..\..\lib\native\win-x64\*.dll" Link="runtimes\win-x64\native\%(Filename)%(Extension)">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <Visible>false</Visible>
    </None>
    <None Include="..\..\lib\native\win-x64\*.dll" Link="%(Filename)%(Extension)">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <Visible>false</Visible>
    </None>
  </ItemGroup>
  ```
* **Modern .NET P/Invoke Dynamic Resolver (`BassLoader.cs`):**
  Uses .NET's `NativeLibrary.SetDllImportResolver` on `typeof(Bass).Assembly` to guarantee that native calls resolve the 64-bit DLLs regardless of test runner working directories or shadow copy folders.

---

### 5.3 Engine Configuration & "Never Stall" Audio Formula

#### 1. Custom Desktop Browser User-Agent
Prevents CDN drops and Cloudflare bot challenges by identifying as a modern desktop client:
```csharp
Bass.NetAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 MetroHub/1.0";
```

#### 2. Network & Buffer Tuning
```csharp
// 5,000ms (5s) network buffer in RAM to absorb cellular/Wi-Fi packet jitter
Bass.Configure(Configuration.NetBuffer, 5000);

// 2,000ms audio output buffer (must be smaller than NetBuffer per BASS spec)
Bass.Configure(Configuration.BufferLength, 2000);

// Pre-buffer threshold: start playback as soon as 75% of pre-buffer is full (< 1.2s start)
Bass.Configure(Configuration.NetPreBuf, 75);

// 10-second timeout for dead servers or hanging DNS lookups
Bass.Configure(Configuration.NetTimeout, 10000);

// Automatically follow default Windows audio device changes (e.g. plugging/unplugging headphones)
Bass.Configure(Configuration.IncludeDefaultDevice, true);
```

#### 3. AAC Plugin Dynamic Loading
```csharp
int aacPlugin = Bass.PluginLoad("bass_aac.dll");
```
Registers AAC/M4A decoders directly into the BASS stream factory. Any `Bass.CreateStream(url, ...)` call will seamlessly handle both MP3 and AAC streams with identical code.

---

### 5.4 Lifecycle, Thread Safety & State Machine

#### Preservation of `IRadioAudioService`:
The public contract of `IRadioAudioService` remains **100% identical**:
* Properties: `CurrentStation`, `IsPlaying`, `IsBuffering`, `Volume`, `IsMuted`, `LastErrorMessage`.
* Methods: `PlayStationAsync(station, ct)`, `Pause()`, `Resume()`, `TogglePlayPause()`, `Stop()`, `SetVolume(vol)`, `SetMuted(muted)`.
* Events: `CurrentStationChanged`, `PlaybackStateChanged`, `BufferingStateChanged`, `VolumeChanged`, `MuteStateChanged`, `ErrorOccurred`.
* `RadioWidgetViewModel.cs` and `RadioWidgetView.xaml` require **ZERO** code changes.

#### Stream Creation & Flags:
```csharp
int stream = Bass.CreateStream(
    station.StreamUrl, 
    0, 
    BassFlags.StreamBlock | BassFlags.AutoFree, 
    null, 
    IntPtr.Zero);
```
* `BassFlags.StreamBlock`: Streamed in real-time chunks; zero memory accumulation on 24/7 playback.
* `BassFlags.AutoFree`: Automatically frees internal resources when the channel is stopped.

#### Real-Time Stall & Buffering Telemetry:
```csharp
// Sync procedure retained in a private field to prevent Garbage Collection
_stallSyncProc = (handle, channel, data, user) =>
{
    // data == 0: Stream stalled (network underrun)
    // data == 1: Stream resumed
    bool isBuffering = (data == 0);
    IsBuffering = isBuffering;
};
Bass.ChannelSetSync(stream, SyncFlags.Stall, 0, _stallSyncProc, IntPtr.Zero);
```

#### Live ICY Metadata Extraction (Track Title & Artist):
```csharp
_metaSyncProc = (handle, channel, data, user) =>
{
    IntPtr tagsPtr = Bass.ChannelGetTags(channel, TagType.ICY);
    if (tagsPtr != IntPtr.Zero)
    {
        string? icy = Marshal.PtrToStringAnsi(tagsPtr);
        // Extracts StreamTitle='...' for real-time station display
    }
};
Bass.ChannelSetSync(stream, SyncFlags.Metadata, 0, _metaSyncProc, IntPtr.Zero);
```

#### Hardware Volume Fade & Pop/Click Elimination:
To prevent harsh digital clicks when stopping or switching stations:
```csharp
// Smooth 120ms logarithmic hardware volume fade-out
Bass.ChannelSlideAttribute(_currentStream, ChannelAttribute.Volume, 0f, 120);
await Task.Delay(120, CancellationToken.None);
Bass.ChannelStop(_currentStream);
```

#### Clean Socket Teardown:
On `Pause()` or `Stop()`, `Bass.ChannelStop(_currentStream)` immediately tears down the underlying TCP socket. No background bandwidth or server connections linger.

---

### 5.5 Step-by-Step Implementation Roadmap

```
+-----------------------------------------------------------------------------------+
|                      Solution A Implementation Phases                             |
+-----------------------------------------------------------------------------------+
|  [Phase 1: Dependencies, Native Binaries & Modern .NET Loader Wiring]             |
|   - Add ManagedBass (v4.1.0-prerelease) to MetroHub.csproj & MetroHub.Tests.csproj |
|   - Download & stage official Un4seen x64 bass.dll & bass_aac.dll in lib/native  |
|   - Configure MSBuild CopyToOutputDirectory rules                                 |
|   - Implement BassLoader with NativeLibrary.SetDllImportResolver                  |
|                                                                                   |
|  [Phase 2: Core BASS Audio Service Refactoring (RadioAudioService.cs)]            |
|   - Implement BASS initialization, NetAgent, buffers, and AAC plugin loading      |
|   - Implement PlayStationAsync with 180ms debounce & cancellation token           |
|   - Implement SyncFlags.Stall (IsBuffering) & SyncFlags.Metadata (ICY tags)       |
|   - Implement 120ms ChannelSlideAttribute fade-out on Pause/Stop                  |
|   - Implement thread-safe Volume & Mute channel attribute controls                |
|                                                                                   |
|  [Phase 3: Live Verification & Testing on Problematic Streams]                    |
|   - Test Birdsong FM (Radio.co chunked stream)                                    |
|   - Test 9128.live (Radio.co 320k stream)                                         |
|   - Test SomaFM Drone Zone & Groove Salad (Icecast MP3)                           |
|   - Test Torontocast & Centova streams                                            |
|   - Verify instant playback start (< 1.5s) and zero stall loops                   |
|                                                                                   |
|  [Phase 4: Unit Test Suite Modernization & Zero Regression Verification]          |
|   - Update RadioAudioServiceTests.cs with BASS initialization verification        |
|   - Run complete test suite (dotnet test) across all 46 existing tests            |
|   - Verify clean process shutdown and 0 native memory leaks                       |
+-----------------------------------------------------------------------------------+
```

#### Phase 1: Dependencies, Native Binaries & Modern .NET Loader Wiring
1. Add `ManagedBass` (version `4.1.0-prerelease`) NuGet package to `src\MetroHub\MetroHub.csproj` and `tests\MetroHub.Tests\MetroHub.Tests.csproj`.
2. Stage official 64-bit Un4seen native binaries (`bass.dll` and `bass_aac.dll`) in `lib\native\win-x64\`.
3. Configure MSBuild `<None>` items in `MetroHub.csproj` and `MetroHub.Tests.csproj` to copy the DLLs to the output directory (`PreserveNewest`).
4. Create `MetroHub.Core.Radio.BassLoader` implementing `NativeLibrary.SetDllImportResolver` for `typeof(Bass).Assembly`.
5. Call `BassLoader.Register()` in `App.xaml.cs` on startup.

#### Phase 2: Core BASS Audio Service Refactoring (`RadioAudioService.cs`)
1. Refactor `RadioAudioService.cs` from WinRT `MediaPlayer` to the BASS pipeline while strictly preserving the `IRadioAudioService` contract.
2. Initialize BASS on default audio endpoint (`-1`, `44100Hz`).
3. Apply `NetAgent`, `NetBuffer` (5000ms), `BufferLength` (2000ms), `NetPreBuf` (75%), and `NetTimeout` (10000ms).
4. Load `bass_aac.dll` plugin for full AAC/M4A support.
5. Implement `PlayStationAsync` with 180ms debounced cancellation, `Bass.CreateStream`, `SyncFlags.Stall` callback, and `SyncFlags.Metadata` callback.
6. Implement `ChannelSlideAttribute` volume fade (120ms) in `Pause()` and `Stop()`.
7. Handle volume changes via `Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, ...)`.
8. Implement proper cleanup in `Dispose()` calling `Bass.Free()`.

#### Phase 3: Live Verification & Testing on Problematic Streams
1. Verify live playback of **Birdsong FM** (`https://a1.radio.co/s5c5da6a36/listen`): confirm audio starts cleanly within 1.5 seconds without buffering lock.
2. Verify live playback of **9128.live** (`https://streams.radio.co/s0aa1e6f4a/listen`).
3. Verify live playback of **SomaFM Drone Zone** and **SomaFM Groove Salad** (Icecast MP3).
4. Verify dynamic Windows audio endpoint switching (unplugging/plugging audio device).
5. Verify zero CPU spikes and stable RAM footprint during continuous streaming.

#### Phase 4: Unit Test Suite Modernization & Zero Regression Verification
1. Update `RadioAudioServiceTests.cs` to test BASS-driven volume clamping, mute toggling, rapid station debouncing, and live stream connectivity.
2. Run full solution test suite (`dotnet test`) to verify all 46 tests pass.
3. Validate clean app launch and UI responsiveness in the running widget.

---

### 5.6 Mandatory Checkpoint & Permission Gate
> [!IMPORTANT]
> **GATEWAY NOTICE:** Per user instructions, implementation will **NOT** begin automatically. 
> The architecture and step-by-step phases documented above must be reviewed and explicitly authorized by the user before executing **Phase 1**.

