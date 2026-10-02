# MetroHub — Engineering Health Review

> **Date:** 30 September 2026
> **Scope:** Full source tree, `src/MetroHub` (203 `.cs` files) and `tests/MetroHub.Tests`.
> **Excluded:** `Widgets/Catalog/Network/NetworkWidgetViewModel.cs` (known god-class, excluded by request). Its 25 `catch` clauses are included in raw totals but excluded from ranked findings.
> **Method:** Four independent deep audits (error handling, memory/lifetime, concurrency/performance, tests/CI) plus a manual pass on security and layering. Every claim below is cited to a file and line that was actually read.

---

## 1. Executive summary

### Grade: **B− (≈7/10)** — *A− architecture, C− engineering process.*

Ignoring the god-class, the app does not fall apart. The foundation holds up better than most projects at this size: layering is genuinely enforced, persistence is atomic and actually tested, concurrency has no deadlock topology, and the "0.0% CPU when hidden" claim turned out to be real rather than aspirational.

What drags the grade down is not structure. It is **inconsistency and diagnostics** — specifically, a well-designed failure-handling primitive that is used at 6% of the places it's needed, and a diagnostic pipeline that records nothing usable in Release builds.

### Scorecard

| Area | Grade | One-line summary |
|---|---|---|
| Architecture & layering | **A−** | Core reaches outside itself in 3/62 files; only 8 files reference `MainWindow.` |
| Persistence & data integrity | **A−** | Atomic `.tmp → File.Replace → .bak` with real corruption recovery, genuinely tested |
| Concurrency & threading | **B+** | No deadlock topology; `ConfigureAwait` discipline consistent across 100+ sites |
| Performance | **B+** | 0.0% CPU verified real; animation on composition thread; sync disk I/O in show/hide |
| Memory & lifetime | **B** | No guaranteed production leak — but one static event, no finalizer, brittle |
| Security | **B** | Hard parts done right (XML escape, `SecureString`); one shell-injection path |
| Testing | **C** | 194 tests, good craft where present — but the riskiest file has zero |
| CI / build engineering | **D+** | Bare `restore → build → test`, no gates of any kind |
| **Error handling & diagnostics** | **D+** | 240 empty catches; 0 stack traces ever logged; Release logs almost nothing |

---

## 2. The pattern that explains the whole review

Across four independent audits, the same shape appeared repeatedly:

> **A correct pattern exists, and is applied to roughly half the places it is needed.**

| Correct pattern | Applied | Not applied |
|---|---|---|
| `Safe.Try` with context string | 27 call sites | **475 `catch` blocks** |
| `async void` fully wrapped in try/catch | `PowerWidgetViewModel:239`, `MediaWidgetViewModel:289`, `ThroughputService:256` | `NetworkHealthService:207`, `PhotosWidgetViewModel:112`, `MainWindow.xaml.cs:658,671` |
| Named event handler + matching `-=` | 10 widget types verified | `MediaWidgetViewModel:265` (anonymous lambda, un-removable) |
| Fire-and-forget with exception observation | `NativeMethods:286`, `StorageService:348`, `MonitorBrightnessService:268`, +4 | `TileManager:699,710,852`, `SidebarRailControl:922`, `SidebarPinningService:231`, +6 |
| Log with stack trace | — | Every production log path (see §4.1) |

Nothing here suggests the patterns are unknown — several were deliberately designed. The problem is that they were never made the *default*, so application is a per-site decision that defaults to the lazy option.

---

## 3. Verified strengths

### 3.1 Layering is enforced, not just intended

- **203** source files total.
- `Core/` — **62 files, only 3 reach outside Core**:
  - `TileModel.cs:159` — a model constructs its own ViewModel (a smell, see §5.7)
  - `SidebarPinningService.cs:10` — Core imports `Presentation.Controls`
  - `WidgetHeartbeatService.cs:5` — Core → `Widgets.Messaging` (defensible: it is the widgets' heartbeat)
- **Only 8 files in the entire app reference `MainWindow.`** The stated goal in `WidgetViewModelBase.cs:50` — *"Eliminates direct coupling from widget ViewModels to MainWindow.Current"* — actually held.

### 3.2 Persistence is the best-engineered part of the app

- `StorageService.SaveAtomic` / `WidgetStateStore`: `.tmp → File.Replace → .bak` with a health check before rotation (`StorageService.cs:90-119`) and a fallback chain.
- Corrupt-primary recovery: `.bak` → `.tmp` → `PreserveCorruptFile` → starter template (`StorageService.cs:203-232`).
- **Tested for real.** `HabitPersistenceTests.cs` runs genuine `File.Replace`/`.bak` rotation against `%TEMP%\MetroHub_HabitTests_<guid>` sandbox directories — not mocked.

### 3.3 Concurrency has no deadlock topology

Specifically checked the risky candidates:

- No lock held across `await` — `ThroughputService.cs:256-272` releases `_lock` before the first `await`.
- No nested lock pairs anywhere.
- `MediaWidgetViewModel.RunOnUi` (`:1134-1146`) is called strictly outside `lock(_stateLock)` and uses non-blocking `Dispatcher.InvokeAsync`.
- `RadioAudioService.GetSpectrumLevels` (`:631-644`) uses `Volatile.Read`, never taking its own gate while the spectrum processor's could be held.
- **Zero** `.Wait()` / `.GetAwaiter().GetResult()` sync-over-async, with one verified-safe exception: `StorageService.Flush():488` `task.Wait(1000)` at shutdown — deadlock-free because `_flushGate` is released before `Wait`.
- `ConfigureAwait(false)` discipline is consistent across 100+ call sites in `Core/**`.
- Locking strategy: private `object _lock`/`_gate` per service; `Interlocked`/`ConcurrentDictionary`/`Volatile` for hot counters.

### 3.4 "0.0% CPU when hidden" is implemented, not marketing

One broadcast message stops:

| Stops | Evidence |
|---|---|
| 1 Hz heartbeat (the app's only periodic dispatcher timer) | `WidgetHeartbeatService.cs:66-76` ← `MainWindow.xaml.cs:867` |
| All `FileSystemWatcher`s | `MainWindow.xaml.cs:875` → `InstalledAppsService.cs:79-91` |
| Low-level keyboard + WinEvent hooks | `MainWindow.xaml.cs:826`, `:876` |
| Video wallpaper GPU decode | `MainWindow.xaml.cs:879` |
| Weather 30-min `PeriodicTimer` | `WeatherWidgetViewModel.cs:345-351` |
| Photos slideshow timer | `PhotosWidgetViewModel.cs:132-135,544-548` |
| Rover 100 ms sprite timer | `RoverWidgetViewModel.cs:544-548` |
| Dino + Speedometer `CompositionTarget.Rendering` | `DinoWidgetViewModel.cs:178-209`, `SpeedometerArcControl.cs:97-116` |
| Throughput + latency timers | `ThroughputService.cs:111-122` |
| Network health probes (5 short-circuits) | `NetworkHealthService.cs:39,209,215,225,255,266,290,294` |
| Aura render loop | `AtmosphericAuraControl.xaml.cs:174-187` |

Supporting evidence: all **16** `new DispatcherTimer` sites checked — none are left armed when hidden (the rest are self-stopping debounces or interaction-scoped). `PowerAwakeService.cs:109-114` blocks on a kernel wait (`WaitOne(Timeout.Infinite)`), not a poll. No `Thread.Sleep`, no `WebClient`, no sync `.GetResponse()`, and `GC.Collect` is explicitly banned (`NativeMethods.cs:97`).

**Three honest exceptions:**
1. **Radio keeps decoding** — `RadioWidgetViewModel` has no `Pause()`/`Resume()` override, so BASS burns CPU while dormant. Probably intentional (music should keep playing), but it falsifies a literal 0.0%.
2. **Media SMTC subscriptions stay live** — `MediaWidgetViewModel.Pause()` (`:1061-1072`) only releases the thumbnail surface; event subscriptions remain.
3. **The diagnostics logger writes to disk while hidden** — `HiddenDiagnosticsLogger.cs:76-84`.

### 3.5 Animation is done on the right thread

Continuous animation rides `CompositionTarget.Rendering` with explicit attach/detach (`DinoWidgetViewModel.cs:195-234`, `SpeedometerArcControl.cs:210-257`, `AtmosphericAuraControl.cs:122-146`, `SmoothScrollBehavior.cs:187/248`) — **not** 60 fps `DispatcherTimer`s. The only sub-100 ms timer in the codebase is drag auto-scroll (`MainWindow.DragDrop.cs:64-67`, 16 ms), which self-stops when the drag ends. Frozen pens, cached `FormattedText`, `Stopwatch.GetTimestamp()`, and early-return-on-minute-equality in the clock.

### 3.6 Memory: no guaranteed production leak in the normal lifecycle

Every live tile-removal path traced and verified:

| Path | Evidence |
|---|---|
| Unpin / Delete / context menu | `TileManager.cs:436-440` → `t.Teardown(); tiles.Remove(t);` |
| Undo/redo snapshot restore | `TileManager.cs:130-135` |
| Delete group + tiles | `CanvasGroupManager.cs:683-688` |
| App exit | `MainWindow.xaml.cs:1091-1098` |
| Drawer bulk-unpin | `AllAppsDrawerControl.xaml.cs:809` |

Also verified: **no orphaned timers** (every per-widget timer stopped in `Dispose`; all `CompositionTarget.Rendering` hooks have matching `-=`); `WeakReferenceMessenger` used correctly throughout (`IsActive` toggled per `ObservableRecipient` pattern, non-capturing lambdas where the recipient is static); all 10 `IDisposable` types have call sites; image caches bounded (`CatalogItemModel.cs:85-86`, `MaxCachedIcons = 64`, eviction at `:158-167`).

### 3.7 Security on the difficult parts

- **`EscapeXml` (`NativeWifiService.cs:787-796`) is textbook-correct** — `&` escaped *first*, then `< > " '`.
- **WiFi passwords handled properly:** `SecureString` → `Marshal.SecureStringToGlobalAllocUnicode` → `Marshal.ZeroFreeGlobalAllocUnicode` in a `finally` (`NativeWifiService.cs:624-661`), then nulled.
- **Path traversal blocked:** `WidgetStateStore.Sanitize` (`:177`) runs before any `Path.Combine` on widget/tile IDs (`:168-174`).
- `Uri.EscapeDataString` used consistently for query construction (`WeatherLocationService.cs:199,244`, `RadioBrowserClient.cs:116,140`, `WebFaviconService.cs:276,284`).
- Shared static `HttpClient` with `PooledConnectionLifetime` rather than per-call instances (`WebFaviconService.cs:25-32`, `SpeedTestService.cs:15-32`).

### 3.8 Widget fault isolation is a genuinely good design

- `WidgetRegistry.cs:440-447` — a widget that throws becomes `StubWidgetViewModel { Label = "Widget Error: {name}", BoxColor = "#DC2626" }`: a visible red tile, never a blank.
- `WidgetHeartbeatService.cs:97-107` — iterates `GetInvocationList()` and isolates *each* subscriber with context, so one bad widget cannot kill the heartbeat. Tested at `WidgetFaultIsolationTests.cs:45-78`.
- `BackdropManager.cs:304-309` — on video-host failure, tears down and posts a "missing codec" toast.
- `Safe.cs:22-34,117-124` — the logger itself is double-guarded; a throwing logger can never escape.

---

## 4. Weaknesses, ranked

### 🔴 W1 — Diagnostics do not work in Release builds

**This is the single most important finding.** When something goes wrong in production, there is nothing to diagnose it with.

| Issue | Evidence |
|---|---|
| No `DEBUG` symbol defined → all `Debug.WriteLine` compiled out | `MetroHub.csproj` (SDK Release default defines only `TRACE`) |
| **103 `Debug.WriteLine` call sites** lost; **83 of them log only `ex.Message`** — no type, no stack | `StorageService.cs:503,520,573,590,638,655`; `AudioService.cs:81,171,198,291,399,498,551,601` |
| **The one Release-safe logger writes only `[{context}] {Type}: {Message}`** — the full exception with stack goes to `Debug.WriteLine`, i.e. nowhere | `Safe.cs:24-29` vs `:29` |
| **Captured log has 78,563 lines, 0 exception entries, 0 stack traces** — only `[TRANSITION]` and `[MEMORY DIAGNOSTIC SNAPSHOT]` | `logs/hidden_diagnostics.log` |
| `DispatcherUnhandledException` **never sets `args.Handled = true`** — it is a logger, not a guard, so a recoverable UI exception still kills the app | `App.xaml.cs:39-48` |
| **No `TaskScheduler.UnobservedTaskException` handler anywhere** (repo-wide grep: 0 matches) | — |
| Crash log **overwritten** on each crash (`File.WriteAllText`, not append) | `App.xaml.cs:28`, `:39` |
| No user notification on UI-thread crash — `MessageBox` appears only on the startup path | `App.xaml.cs:94-107` |

**Consequence:** combined with W2 and W5, a production failure produces an app that dies silently, writes a log with no exception detail, and overwrites its previous crash record.

---

### 🔴 W2 — 240 empty `catch` clauses

**Counts across `src/`:**

| Metric | Total | Excl. god-class |
|---|---|---|
| `catch` blocks | **475** | 450 |
| ├ bare `catch` (no type/variable) | **316** | 298 |
| └ typed `catch (...)` | **159** | 152 |
| **Bare bodies empty or comment-only** | **240** | 224 |
| `Safe.Try` call sites | **27** | 27 |
| `Safe.TryAsync` call sites | **0** | 0 |
| `Debug.WriteLine` call sites | **103** | 102 |
| `throw;` rethrows | 3 | 3 |

> Counts are reproducible: `catch\s*(\([^)]*\))?\s*\{` → 475; `catch\s*\{` → 316; `Safe\.Try\s*(<[^>]*>)?\s*\(` → 27. The 27 `Safe.Try` sites are *alternatives* to `catch`, not a subset of it — so the primitive is used alongside 475 catch blocks, roughly one site in eighteen.

Breakdown of the 316 bare catches: **200** are single-line `catch { }`; of the 114 multi-line, hand-verified individually → **7 truly empty**, **33 comment-only**, **72 with real recovery code**.

**Worst clusters:** `IconExtractorService.cs` (16), `EthernetProvider.cs` (11), `RadioAudioService.cs` (10), `MainWindow.xaml.cs` (10, all silent), `MonitorBrightnessService.cs` (8).

**Most consequential instances:**

- 🔴 **Silent layout/data loss on exit** — `MainWindow.xaml.cs:1062,1072,1088,1098` and `App.xaml.cs:162,174` swallow `SaveGroupsAndLayout()` / `StorageService.Flush()` failures with bare `catch { }`. The user loses layout with no warning and no trace.
- 🔴 **Central serializer swallows every JSON error** — `WidgetSerializer.cs:35` `catch { return null; }`, then each consumer silently resets config: `HabitWidgetViewModel.cs:181`, `QuotesWidgetViewModel.cs:169`.
- 🟠 **Export failure invisible** — `MainWindow.Tiles.cs:632-639`: `ExportLayout` returns `bool`; on `false` nothing is shown, and the `catch` is Debug-only.
- 🟠 **19 of 27 `Safe.Try` sites discard the bool result** with no retry, fallback, or feedback (`WidgetViewModelBase.cs:34,77,89,93,120`; `WidgetStateStore.cs:101,114,123,129,145`; `StorageService.cs:49,109,135,141,397,414,488,710`; `HubState.cs:35`).
- 🟡 **Speed-test failures report as "no data"** — `SpeedTestService.cs:353,541,579,625`: `try { await Task.WhenAll(...); } catch { }` drops aggregated worker failures wholesale.

---

### 🟠 W3 — The highest-risk file has zero tests

`Core/Services/GridPlacementService.cs` — **2,342 lines** of pure static collision/placement math (~40 public methods: `PlaceAndResolveCollisions:615`, `PlaceClusterAndResolveCollisions:763`, `ResolveResizeExpansion:242`, `IsRegionFree:101`, `FindNearestAvailableSlot:174`, `SanitizeAndSnapAll:973`). It is the **second-largest file in the app** after the excluded god-class.

A grep for `GridPlacement|PlaceAndResolve|IsRegionFree|FindNearestAvailableSlot|SanitizeAndSnapAll|DoTilesOverlap|ResolveResizeExpansion` across `tests/` returns **no matches**.

This is the highest-defect-probability code in the repository — and it is trivially unit-testable: pure functions, no I/O, no WPF, no static mutable state.

**Also untested:** `StorageService` durability paths (`SaveAtomic:62-150`, corrupt-primary recovery `:203-232`, `PreserveCorruptFile:394`, `ExportLayout`/`ImportLayout`) — its only 3 tests hit the happy path and **rewrite the developer's real `%LocalAppData%` config** (`PersistenceAndDecouplingTests.cs:25,63,97`). Plus `MainWindow.DragDrop.cs` (1,982 lines, 2 tests), all of `Core/Network/*`, `Core/Display/*`, and every UI/automation test (none exist).

**Contrast:** `WidgetStateStore` has a documented test seam (`:63-68`) and is well tested. The pattern for doing this right already exists in the repo.

---

### 🟠 W4 — The fault-isolation design itself leaks

`WidgetViewModelBase.cs:27` — the **base constructor** does:

```csharp
WidgetHeartbeatService.SecondTick += OnSecondTickInternal;
```

- The only `-=` is at `:119`, inside `Dispose(bool)`, guarded by `if (disposing)`.
- **There is no finalizer.** Grep for `^\s*~\w+\s*\(` across `src/` returns zero matches — so `GC.SuppressFinalize(this)` at `:110` is a no-op.
- If `Dispose()` is never called, `Dispose(bool)` can never run. The instance is rooted **forever** by the static `SecondTick` multicast delegate (`WidgetHeartbeatService.cs:19`) and ticks every second for the life of the process — silently, because the tick body is exception-guarded.

**The failure path is real and explicitly modelled.** `WidgetDefinition.cs:29-46` and `WidgetRegistry.cs:442-446` wrap construction in `Safe.Try(..., fallback: null) ?? new StubWidgetViewModel(...)`. If a derived constructor throws *after* the base ctor ran, or `Initialize()` throws, the half-built VM is discarded and replaced by the Stub — **never disposed**.

> **Note:** `WidgetFaultIsolationTests.cs:81-105` asserts exactly this scenario (factory throws → Stub returned). The test that proves the fault isolation also demonstrates the leak.

**Concrete triggers:**
- `RoverWidgetViewModel.cs:183-185` calls `LoadSettings(model.SettingsJson)` directly in its constructor, outside any `Safe.Try`. Malformed JSON is sufficient.
- `QuickControlsWidgetViewModel.cs:57-59` constructs three more `WidgetViewModelBase` descendants in its constructor — one failure roots four.

**Empirical proof of the mechanism:** tests construct widget VMs and never dispose them — `HabitPersistenceTests` (7×), `RadioWidgetTests` (8×), `MediaWidgetSanityTests` (5×), `ClockFontTests` (1×). Each is permanently rooted in `SecondTick`; the delegate grows monotonically for the whole test process.

---

### 🟠 W5 — `async void` on non-handler paths, with a top-level handler that doesn't handle

Ten `async void` methods: `MainWindow.Tiles.cs:400`, `ThroughputService.cs:256`, `NetworkHealthService.cs:207`, `WeatherLocationDialog.xaml.cs:224,396,420`, `RadioStationDialog.xaml.cs:615`, `PowerWidgetViewModel.cs:239`, `PhotosWidgetViewModel.cs:339`, `MediaWidgetViewModel.cs:289`.

The dangerous ones are unguarded:

- **`NetworkHealthService.cs:207`** — `async void` with **no try/catch**, ending in an unguarded `HealthChanged?.Invoke(status)` at `:331`. Any throwing subscriber = process termination.
- **`MainWindow.xaml.cs:658,671`** — `Dispatcher.InvokeAsync(async () => ...)` inside `WndProc`. `ApplyConfiguredBackdrop()` runs *before* the first `await`, so a throw on a `WM_SETTINGCHANGE`/`WM_DISPLAYCHANGE` broadcast crashes the app.
- **`PhotosWidgetViewModel.cs:112-116`** — async-void lambda dispatched from a thread-pool `System.Threading.Timer`.

Combined with W1 (`Handled` never set, no `UnobservedTaskException`), every one of these is fatal rather than recoverable.

---

### 🟡 W6 — Synchronous disk I/O on the UI thread in the show/hide hot path

- **Show/hide logging:** `MainWindow.xaml.cs:819` (show), `:852` (hide) → `HiddenDiagnosticsLogger.cs:193-207` → `Process.Refresh()`, two `GetGuiResources` P/Invokes, then `File.AppendAllText` under `lock(_lock)` — all on the UI thread, before the dispatcher pumps the storyboard.
- **Hide writes widget state inline:** `MainWindow.xaml.cs:867` sends `HubVisibilityChangedMessage(false)` *synchronously*, so widget `Pause()` runs inline on widgets with persistence, triggering state serialization and disk persistence. No measurement, no watchdog.

**Related:** the heartbeat (`WidgetHeartbeatService.cs:37`, `:92-108`) isolates subscribers by exception but has **no time budget and no slow-tick telemetry**. One widget doing slow work in `OnSecondTick` stalls the dispatcher for its full duration — silently. Worse, a widget that throws *every second* costs a synchronous UI-thread `File.AppendAllText` every second.

---

### 🟡 W7 — CI has no quality gates, and some tests cannot fail

`.github/workflows/ci.yml` — `windows-latest`, .NET `10.0.x`, `restore → build --no-restore → test --no-build`. Absent: `-c Release`, warnings-as-errors, analyzers, lint/format, coverage, artifact upload, dependency caching, concurrency/cancel-in-progress. No `.editorconfig`, no `Directory.Build.props`, no `TreatWarningsAsErrors`, no analyzers/StyleCop.

**Tests that cannot fail:**
- `ClockFontTests.cs:26-151` and `RoverWidgetTests.cs:198-263` — **zero assertions**, and they write PNGs to a hardcoded developer path: `C:\Users\HamB\.gemini\antigravity-ide\brain\a6e02d95-…\scratch\renders`.
- `AppSettingsTests.cs:137-141` — sets `TileCornerRadius`, then asserts it equals what it just set.
- `AppSettingsTests.cs:122-128` — re-implements the production guard instead of calling it.
- `WeatherLocationServiceTests.cs:124-127,136,147,158` — assert only `Assert.NotNull(dlg)`.

**Non-determinism in the default run:**
- Live internet + real audio: `RadioAudioServiceTests.cs:63,89,126,156` (ice1.somafm.com, radio.co) and `RadioBrowserAndProbeTests.cs:422-436`, with wall-clock `Task.Delay(1000)` waits and no `[Trait]` gate.
- Hard wall-clock perf assertion: `AudioSpectrumProcessorTests.cs:217` asserts `msPerFrame < 0.05`; `:190` asserts exactly 0 bytes allocated over 1,000 frames.
- Raw STA threads that swallow assertion failures by crashing the host: `RoverWidgetTests.cs:60-62`, `ClockFontTests.cs:148-150` do `thread.Start(); thread.Join();` with no exception capture (unlike the correct pattern in `WpfTestHost.cs:39-51`).

**Test inventory:** 28 files (26 with tests), **194 test methods** (187 `[Fact]` + 7 `[Theory]`) → ~253+ executed cases.

---

### 🟡 W8 — Shell argument injection

`SidebarRailControl.xaml.cs:539`:

```csharp
Process.Start(new ProcessStartInfo("powershell.exe",
    $"-NoExit -Command \"Set-Location '{folder}'\"") { UseShellExecute = true });
```

`folder` is interpolated into a single-quoted PowerShell string with no escaping. NTFS permits `'` and `;` in directory names, so a directory named `x'; Start-Process calc; '` is creatable and would execute. Gated by `Directory.Exists(folder)`, so it requires a planted directory — local, user-driven, **low-to-medium** severity. The `cmd.exe /K cd /d "{folder}"` fallback at `:543` has the same shape (lower risk; quotes protect `&`, but `%VAR%` expansion still applies).

**Also:** `LocationService.cs:125` calls `http://ip-api.com/json/...` — the user's IP and coarse location go out over plain HTTP. (ip-api's free tier is HTTP-only, so this is likely a constraint rather than an oversight.)

---

### 🟢 W9 — Remaining structural notes

| # | Issue | Evidence |
|---|---|---|
| 1 | **`MediaWidgetViewModel` uses an un-removable `this`-capturing lambda** on `Model.PropertyChanged`; `Dispose` never detaches it. Every other widget uses a named handler with matching `-=` (10 verified). | `MediaWidgetViewModel.cs:265-282`, `:1512-1539` |
| 2 | **Dead-code tile-removal path skips `Teardown()`** — currently unreachable (`CategoryControl` is never instantiated), but a landmine if wired up. | `CategoryControl.xaml.cs:176-183` |
| 3 | **`CinematicFadeService` overlay leak** — if `BeginAnimation` throws after `overlay.Show()`, the catch doesn't call `DismissOverlay`, so the window and two `SystemEvents` subscriptions survive to exit. | `CinematicFadeService.cs:195`, `:197-200` |
| 4 | **Diagnostics log has no rotation or size cap** — unbounded `File.AppendAllText`. Only one log in the app is capped (`MediaWidgetViewModel.cs:820-823` 256 KB). | `HiddenDiagnosticsLogger.cs:193-207` |
| 5 | **Core layering violations** — `TileModel.cs:159` (a *model* constructs its own ViewModel and caches it), `SidebarPinningService.cs:10` (Core → `Presentation.Controls`), `WidgetHeartbeatService.cs:5` (Core → Widgets). | — |
| 6 | **`IWidgetViewModel.Teardown()` has zero call sites** — disposal goes exclusively through `TileModel.Teardown()`. Dead contract surface. | `IWidgetViewModel.cs:20` |
| 7 | **`SaveLayoutSync`/`SaveGroupsSync`/`SaveSettingsSync` are dead code** (no callers), and take `lock(WriteLock)` then re-enter it via `SaveAtomic` — safe only because `Monitor` is reentrant. | `StorageService.cs:507,577,642`, `:64` |
| 8 | **`ReleaseComObject` not in a `finally`** — delayed release only, not a permanent leak. | `AudioService.cs:393` |
| 9 | **CTS race** — `existingCts.Cancel(); existingCts.Dispose();` while a `Task.Delay(..., cts.Token)` may still hold a registration. | `MonitorBrightnessService.cs:282-286` |
| 10 | **Prerelease dependency pinned** — `ManagedBass 4.1.0-prerelease`; blanket `NoWarn NU1701`; `System.Management 9.0.0` on a `net10.0` target. | `MetroHub.csproj:22,24,10` |
| 11 | **Win32 hook re-install deferred to `Background` priority**, so a show→hide inside one dispatcher pump could tear down before reinstall. Idempotency guard makes the failure a *missing* hook, not a leak. | `MainWindow.xaml.cs:811-816`, `:1120` |

---

## 5. Remediation plan

Ordered by bug-catch-per-hour. None of these require architectural change.

### P0 — Stop flying blind (small effort, largest payoff)

1. **Make Release diagnostics real.**
   - `Safe.cs:24-29` → log `ex.ToString()` (includes stack), not `{ex.Message}`.
   - `App.xaml.cs:39` → set `args.Handled = true` for recoverable exceptions.
   - `App.xaml.cs:28,39` → append to `crash.log` with rotation instead of overwriting.
   - Add `TaskScheduler.UnobservedTaskException` handler.
   - Decide deliberately on `DEBUG`/`TRACE` in Release, or migrate the 103 `Debug.WriteLine` sites that matter to `Safe.Logger`.

2. **Route the data-loss catches through `Safe.Try`.** Priority subset: `MainWindow.xaml.cs:1062,1072,1088,1098`, `App.xaml.cs:162,174`, `WidgetSerializer.cs:35`, `MainWindow.Tiles.cs:639`.

### P1 — Cover the risk

3. **Test `GridPlacementService`.** Table-driven tests for `PlaceAndResolveCollisions`, `IsRegionFree`/`DoTilesOverlap`, `FindNearestAvailableSlot`, `ResolveResizeExpansion` (incl. `CanDisplace:240` locked-tile refusal), `PushLowerGroupsDown`, `SanitizeAndSnapAll`. Highest payoff of any gap.

4. **Add a `StorageService` durability suite behind a path-injection seam** (mirror `WidgetStateStore.cs:63-68`): `.tmp → File.Replace → .bak` rotation, "don't rotate a corrupt target" (`:114-119`), `.bak`/`.tmp` recovery (`:203-223`), `PreserveCorruptFile` cap, `ExportLayout`/`ImportLayout`. Must run in a sandbox, not `%LocalAppData%`.

### P2 — Close the lifetime hole

5. **Move the `SecondTick` subscription out of the base constructor** into `Initialize`, **or** wrap construction with `try { … } catch { vm.Dispose(); throw; }` at `WidgetDefinition.cs:29` and `WidgetRegistry.cs:442`. Also fix `RoverWidgetViewModel.cs:183` to guard `LoadSettings`.
6. **`MediaWidgetViewModel.cs:265`** → convert the anonymous lambda to a named handler and `-=` it in `Dispose`.
7. **Add `Teardown()` to `CategoryControl.xaml.cs:176`** before it ever gets wired up.

### P3 — Engineering guardrails

8. **CI gates:** `-c Release`, `TreatWarningsAsErrors`, an `.editorconfig`, dependency caching.
9. **Trait-gate the live-network tests** — `[Trait("Category","Online")]` + `dotnet test --filter` so CI is deterministic while keeping live-stream tests opt-in.
10. **Delete or fix the assertion-free tests** (`ClockFontTests.cs:26`, `RoverWidgetTests.cs:198`) — remove the hardcoded developer path.
11. **Add a heartbeat tick-duration watchdog** with slow-tick telemetry; move hide-time state persistence off the show/hide frame.
12. **Fix `SidebarRailControl.xaml.cs:539`** — validate against an allowlist of path characters, or pass the folder via `-WorkingDirectory`/an environment variable instead of interpolating into `-Command`.

---

## 6. Appendix — what was and wasn't examined

**Examined:** `src/MetroHub` (all 203 `.cs` files via targeted reads and repo-wide greps), both `.csproj`, `MetroHub.sln`, `.github/workflows/ci.yml`, all 28 test files, `logs/hidden_diagnostics.log`.

**Excluded:** `NetworkWidgetViewModel.cs` (2,991 lines) — by request. Note for context: its 25 `catch` clauses and its role as the sole owner of the `ThroughputService`/`NetworkHealthService` pause contract (`.Pause()`/`.Resume()` at `:2884-2903`) are counted in totals but not itemised.

**Not examined:** runtime profiling (no CPU/memory traces were captured — the 0.0% CPU assessment is code-derived, not measured), third-party package vulnerability scanning, accessibility, localization, and packaging/signing.
