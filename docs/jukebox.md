# YouTube Jukebox Widget — Architecture Plan & Build Tracker

> **Goal:** user types a song name → taps a result → audio streams from YouTube through
> BASS → track pins itself to the widget list → tap anytime replays it.
> Status: **PLAN — not yet implemented.** Check boxes as phases land.

---

## 0. Progress tracker

- [x] Phase 0 — Packages + native plugin wired (`MetroHub.csproj`, `lib/native/win-x64`)
- [x] Phase 1 — `YoutubeAudioResolver` service (`Core/Radio/`)
- [x] Phase 2 — `JukeboxWidgetSettings` + `WidgetJsonContext` entry (history model)
- [x] Phase 3 — `JukeboxWidgetViewModel` (search, play, history)
- [x] Phase 4 — `JukeboxWidgetView.xaml` (search box, results, history, now-playing bar)
- [x] Phase 5 — Registry + `TileControl.xaml` DataTemplate + sizes (`PortraitXL` 4×8 new)
- [x] Phase 6 — Offline-safe tests (history round-trip, re-resolve, autoplay, epoch) — 67/67 green
- [ ] Phase 7 — Release publish + live verification (search → play → restart → replay)

## 8b. Playback engine (2026-09-27 rewrite)

Earlier builds hand-rolled a "push pump" (sniff 64 KB → `BASS_StreamPutFileData`) and started
playback on a fixed 6/10 s timer. That was the root cause of "plays half a second then stops":
playback began on an almost-empty buffer while YouTube throttled the single long-lived GET to
~12 KB/s (the log shows a 27-45 KB/s burst only right after a reconnect).

The player now uses:

- `Core/Radio/YoutubeChunkedDownloader.cs` — short HTTP `Range` requests (512 KB-2 MB). Each
  fresh request restarts YouTube's fast throttle window; if the live rate collapses mid-chunk it
  re-opens the range from the last received byte. Rolling 2.5 s rate windows decide this.
- `Core/Radio/ChunkedAudioBuffer.cs` — thread-safe growable buffer. BASS's `FILEREADPROC` pulls
  from it and **blocks** for late data; it returns 0 only at a genuine end of stream (0 = EOF in
  BASS, which is what makes a slow moment end the track).
- `JukeboxAudioService` — creates the stream with **`StreamSystem.Buffer`** (BASS runs its own
  download thread, buffers, stalls and resumes). Playback starts only after an **adaptive
  prebuffer** (128 KB preferred, 32 KB minimum, 15 s cap), never on a bare timer.
- End detection: `EndOfStreamReached` fires only when the download actually completed; an
  interrupted stream never triggers autoplay-next. The native stream is freed off the sync thread.

## 8. Debug log (temporary)

- `Core/Radio/JukeboxLog.cs` — one file, one-line call sites. Writes
  `%LocalAppData%\MetroHub\Logs\jukebox.log` + debugger output. `Enabled` flag to silence.
  Disabled automatically inside unit tests (static ctor) so test runs stop polluting the log.
- Player errors now also surface in the tile status line (previously silent).
- Startup logs decoder-plugin presence (`opus=`/`aac=` + base dir) — answers "is bassopus
  actually deployed?" directly from the log.
- Opus→AAC auto-fallback: `FileFormat` reject on an Opus pick retries once with AAC
  (covers missing-plugin installs without user action).
- Push-download fallback: when BASS's own downloader rejects known-good bytes, our HttpClient
  (proven instant by Probe lines) pumps them into BASS via `BufferPush` + `StreamPutFileData`.
- Attempt order (fastest reliable first): push-low (~48 kbps, starts instantly off the
  64 KB sniff duplexed as ~10 s prebuffer) → push-Opus → push-AAC → direct opens last
  (BASS stalls 30–90 s on throttled googlevideo before failing).
- Single manifest fetch per tap (`ResolveStreamsAsync` returns low+opus+aac URLs together).
- `basswebm.dll` (34 KB) added: Opus-in-WebM needs the WebM demuxer, a separate plugin from
  the Opus decoder. Both `bassopus` and `basswebm` handles logged at startup alongside AAC.
- Throttle survival: 256 KB prebuffer before first play, per-5 s download-rate logging,
  stall counter (4+ underruns / 30 s) fires `StallStormDetected` → VM auto-drops once per
  track to lowest-bitrate Opus (~48 kbps ≈ 6 KB/s, fits under almost any throttle).
- To strip: delete `JukeboxLog.cs` and its `JukeboxLog.*` one-liners.

---

## 1. Research log (what this plan stands on)

| Source | Finding that shaped the plan |
|---|---|
| YoutubeExplode GitHub releases (6.6–6.6.2, Apr–Aug 2026) | Library is alive; 6.6 switched to the `ANDROID_VR` client to bypass YouTube's PO-token requirement; 6.6.2 fixed download errors. Update path exists when YouTube breaks things. |
| YoutubeExplode open issues (#902 cipher manifest, #706 slow n-sig, #962 per-video failures — all `bug/caused by youtube`) | Breakage is periodic and per-video, not total. Design consequence: retry + graceful per-item error states, never assume resolve succeeds. |
| youtube_explode_dart README (sister project, same protocol) | Confirms the core trick: `audio stream url` can be **streamed directly** — no download step. This is what keeps RAM flat. |
| WPF-UI docs + source (`lepoco/wpfui`, `AutoSuggestBox.cs`) | `ui:AutoSuggestBox` already ships in WPF-UI 3.0.4 (referenced): `Text`, `PlaceholderText`, `MaxSuggestionListHeight`, `ClearButtonEnabled`, popup `ListView` suggestions. **Zero new UI packages.** |
| Microsoft Learn — AutoSuggestBox guidelines | Canonical 3-event pattern: `TextChanged` (query only on `UserInput`) → `SuggestionChosen` → `QuerySubmitted`. Debounce keystrokes; never search on programmatic text sets. |
| un4seen.com add-ons page | `BASSOPUS` (Opus decode), `BASSmix` (later), `BASSenc` (later). Sizes measured §2. |
| `.agents/rules/` (all 7 read: onboarding, design system, lifecycle brief, bug protocol, publish, fonts, typography) | Checklist §7, design tokens §5, `Stop-Process` before publish, no shadows on text <20px, 20px gutters, hairline dividers. |

---

## 2. Package decision — with measured sizes (net10.0, what actually ships)

| Package / binary | Shipped size (measured 2026-09-27) | Transitive deps on net10.0 | Verdict |
|---|---|---|---|
| `YoutubeExplode` 6.6.2 | **1,540 KB** (`YoutubeExplode.dll`) | **none** (empty target group; the Bcl/ STJ deps in the nuspec apply to netstandard2.0 only) | ✅ take |
| `ManagedBass` (already in app) | 104 KB wrapper | — | already paid |
| `bass.dll` + `bass_aac.dll` (already in `lib/`) | 163 + 235 KB | — | already paid |
| `bassopus.dll` (new, `BASSOPUS` 2.4.3.3) | **<200 KB** (zip is 185 KB download) | — | ✅ take |
| **Total new footprint** | **~1.7 MB** | 0 new packages beyond the one | not bloat |

Rejected: `YoutubeExplode.Converter` (drags FFmpeg — we stream, never download/convert),
`yt-dlp` binary (external process, ~40 MB, overkill), any UI package (WPF-UI already has
`AutoSuggestBox`).

Why Opus matters: YouTube's highest-bitrate audio is Opus; without `bassopus` we settle for
AAC (the workaround my earlier test used). One `Bass.PluginLoad("bassopus.dll")` next to the
existing `bass_aac` line, same `BassLoader` pattern.

---

## 3. Architecture

### 3.1 `YoutubeAudioResolver` (`Core/Radio/YoutubeAudioResolver.cs`, singleton)

- Owns **one static `YoutubeClient`** for app lifetime (Microsoft HttpClient guidance: reuse,
  don't churn sockets).
- `SearchAsync(query, topN: 8, ct)` — `await foreach` over `Search.GetVideosAsync`, break after
  8. Never enumerate the open-ended result stream (unbounded network + RAM).
- `ResolveAudioUrlAsync(videoId, ct)` — `GetManifestAsync` → Opus audio-only highest bitrate →
  **fallback** AAC/MP4 highest bitrate (works even if `bassopus` missing) → null with reason.
  Retry once on transient failure, then surface a friendly status string.
- **Never persist or cache stream URLs** — they expire in hours. Persist `videoId`, re-resolve
  on every tap, resolve-then-play inside 60 s.
- All awaits `ConfigureAwait(false)`; UI-bound assignments marshalled once per operation.

### 3.2 Playback — reuse, don't rebuild

- `IRadioAudioService` singleton stays the single player (per radio plan: volume/mute global).
  Jukebox builds a `RadioStation { Id = "yt:"+videoId, Name, StreamUrl = resolved, Category = "jukebox" }`
  and calls `PlayStationAsync`. Pause/resume/stop/fade/debounce come free.
- Shares the existing 180 ms debounce + 120 ms fade + NetAgent UA + 5 s ring buffer.

### 3.3 History model (settings, source-gen JSON)

```csharp
record JukeboxTrack { VideoId, Title, Channel, ThumbnailUrl, Duration, LastPlayedAt, PlayCount }
class JukeboxWidgetSettings { List<JukeboxTrack> History (cap 50), string? LastVideoId }
```

- Dedupe by `VideoId`: replay bumps to top + `PlayCount++`, no duplicates.
- Cap 50 (bounded settings payload, bounded list RAM).
- Register in `WidgetJsonContext` (onboarding checklist point 6).

### 3.4 ViewModel (`Widgets/Catalog/Jukebox/JukeboxWidgetViewModel.cs`)

- `[ObservableProperty] SearchText, SearchResults (ObservableCollection), History, IsSearching,
  StatusMessage, CurrentTrack`.
- `[RelayCommand] SearchAsync, PlayResultAsync, PlayHistoryAsync, RemoveTrack, ClearHistory,
  TogglePlayPause`.
- Freshness discipline (bug-protocol §3.2): monotonic `_searchEpoch`; late search batches with
  stale epoch are dropped, never rendered. One `CancellationTokenSource` per burst, cancelled on
  next keystroke and in `Dispose(bool)` / `Pause()`.
- `Pause()` cancels in-flight search; no timers while idle (playback state arrives via existing
  service events, not polling).

### 3.5 View (`JukeboxWidgetView.xaml`)

- `<widgets:WidgetCard Padding="0" Background="Transparent">` (onboarding point 2; matches
  Network/Media/Photos precedent).
- `ui:AutoSuggestBox` (`PlaceholderText="Search YouTube…"`, `MaxSuggestionListHeight`, clear
  button) + results popup; history `ListView` (default `VirtualizingStackPanel` compositon —
  do NOT disable virtualization), `TextTrimming="CharacterEllipsis"`, full title in `ToolTip`.
- Thumbnails: `BitmapImage DecodePixelWidth="96"` — a 1280px YouTube thumb decodes to ~5 MB,
  at 96px ≈ 37 KB. This is the single biggest RAM lever in the widget.
- Docked now-playing bar per DESIGN_SYSTEM §9.2 (`#14000000`, `CornerRadius="0,0,2,2"`,
  `Padding="20,0,20,0"`), hairline divider §9.3 (`#14FFFFFF`, `Margin="0"`), 20px gutters §15,
  no shadows on text <20px, `SnapsToDevicePixels`/`UseLayoutRounding` everywhere,
  `AutoHideScrollBehavior` on the history list.

### 3.6 Sizes, registration, menu (onboarding points 3–5)

- `AllowedSizes`: new `WidgetSize.PortraitXL` (4×8) added to the shared palette — tall
  portrait fits search + scrolling history. `Large` + `Mega` also allowed.
  Default: `PortraitXL`. (User-confirmed 2026-09-27.)
- History: **uncapped** (user-confirmed). Dedupe by `VideoId` still applies. Cost note:
  each track is ~200 bytes of JSON, so even 1,000 tracks is ~200 KB in settings —
  acceptable; virtualization keeps list UI flat.
- Thumbnails: **off** (user-confirmed) — text-only rows, zero `BitmapImage`s. Thumbnail URL
  still stored per track (~60 bytes) so it can be switched on later with no migration.
- `WidgetRegistry`: `Id: "jukebox"`, `DisplayName: "YouTube Jukebox"`, `Icon: SymbolRegular.MusicNote2_24`-family (verify name at implementation), `Category: "Sound"`.
- `TileControl.xaml`: `xmlns:jukebox` + `DataTemplate DataType={x:Type jukebox:JukeboxWidgetViewModel}`.
- Context menu: Play/Pause + Clear history (`Tag="WidgetCustomMenu"`); exclude from press-down
  animation target like other interactive widgets.

---

## 4. Memory / CPU budget (why this stays lean)

| Surface | Bound | How |
|---|---|---|
| Audio bytes | ~100–500 KB | Streamed into BASS's own 5 s ring buffer; never `DownloadAsync`, never a full-file `MemoryStream` |
| Thumbnails | ≤ ~2 MB worst case | `DecodePixelWidth=96` + 50-item history cap + virtualization |
| Search | 8 items max | Break out of `IAsyncEnumerable` early; CTS kills stale bursts |
| Idle CPU | ~0 | No timers/polling; search on demand; playback events only |
| Sockets | 1 client | Singleton `YoutubeClient`, app lifetime |
| Settings payload | KBs | 50 tracks × small record, source-gen JSON, no reflection |

Latest-C# notes: `record` DTOs with `init`, `required` where load-bearing, `await using`
where applicable, `ArgumentNullException.ThrowIfNull`, `慣` — no: `Math.Clamp` on counts,
`CancellationToken` plumbed through every async method, `ObjectDisposedException` guards on
post-dispose callbacks.

---

## 5. Failure modes (from the issue tracker, designed-in)

1. YouTube changes internals (PO token, cipher) → resolver returns typed failure → status bar
   shows "YouTube changed — player update pending", history still browsable. Fix path is a
   package version bump, no code change.
2. Per-video resolve failure (#962 pattern) → skip that item with inline error, rest of list works.
3. Slow n-sig decipher (#706) → 15 s resolve timeout → AAC fallback attempt → error state.
4. Offline → search disabled with status text; history + replay-old-nothing (replay needs net,
   say so in the message).
5. Age-restricted / embed-blocked / deleted → resolve returns null → "unavailable" badge on item.
6. Expired URL → impossible by construction (never stored).

---

## 6. Test plan (offline-safe suite stays green)

- History: dedupe bump-to-top, 50-cap trim, settings JSON round-trip through `WidgetSerializer`.
- Resolver: Opus-preferred / AAC-fallback / null-reason chain against **fake manifests**
  (no network in unit tests).
- Epoch test: stale search batch dropped, only newest renders.
- Network live check (Sofia test) stays a **manual** step in Phase 7, not in CI — YouTube in
  unit tests turns the suite red on airplanes.

---

## 7. Open questions for you

1. ~~Sizes OK (`LargeWide` default + `Large` + `Mega`)?~~ Decided: `PortraitXL` 4×8 (new) + `Large` + `Mega`. (2026-09-27)
2. ~~History cap 50, or smaller (25)?~~ Decided: uncapped. (2026-09-27)
3. ~~Thumbnails on (nicer, ~2 MB) or text-only (leanest)?~~ Decided: off, text-only. (2026-09-27)
4. Search trigger — Decided: **Enter to search** (one request per search). (2026-09-27)
5. Song end — Decided: **autoplay next** history item (needs end-of-stream signal from player). (2026-09-27)
6. Player sharing — Decided: **shared singleton** with radio (song stops radio and vice versa). (2026-09-27)
   **REVISED 2026-09-27: independent `JukeboxAudioService`** (own BASS channel, own volume/mute,
   true pause-keeps-position). Radio and songs never interfere.
7. Progress display — Decided: **none** (title + play/pause only; no position timer = less CPU). (2026-09-27)
8. Row click — Decided: **single-click plays**, right-click removes from history. (2026-09-27)
