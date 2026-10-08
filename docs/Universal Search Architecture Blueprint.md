# Universal Search: Architecture Blueprint

Purpose: a build plan for a fast, safe, ranked search feature. It combines the installed-apps index with the Everything file search engine. This document uses generic names. Map each generic name to the matching class or file in the existing project.

## 0. How to use this document with the coding agent

1. Tell the agent to scan the project and map each generic component name (Section 3) to existing code.
2. For each component, the agent must report: reuse, extend, or create.
3. Build in the phase order of Section 13. Do not skip a phase gate.
4. Do not rename existing public types without approval.

## 1. Goals, non-goals, budgets

**Goals**

- Show the best result first, for any query.
- Stay responsive on every key press.
- Use bounded CPU and RAM. No growth over time.
- Fail in a safe way when Everything is missing or broken.

**Non-goals**

- Do not build a new file indexer.
- Do not search file contents in version 1.
- Do not send any query or path off the device.

**Budgets (treat each as a test that must pass)**

| Metric | Budget |
| --- | --- |
| Key press to first visible result (apps) | p95 under 50 ms |
| Key press to file results | p95 under 200 ms |
| UI thread work per key press | under 8 ms |
| CPU while idle (window hidden) | under 0.5 percent, no timers that spin |
| CPU peak during typing | one core, under 30 percent average |
| Working set growth over an 8 hour soak test | under 5 percent, after warm-up |
| Managed heap after 10,000 searches | returns to within 10 percent of baseline after a full GC in the test |
| Native handles and threads | constant count after warm-up |

## 2. Layered architecture

```
+-----------------------------------------------------------+
| L1  Presentation: SearchView (UI only, no logic)          |
+-----------------------------------------------------------+
| L2  Presentation logic: SearchViewModel                   |
|     input debounce, selection, commands, result binding   |
+-----------------------------------------------------------+
| L3  Application: SearchOrchestrator                       |
|     one search session at a time, cancel, tiers, merge    |
+-----------------------------------------------------------+
| L4  Domain: QueryParser, Ranker, Deduplicator,            |
|     CategoryPolicy, ExclusionPolicy (pure, no I/O)        |
+-----------------------------------------------------------+
| L5  Source adapters (all implement ISearchSource)         |
|     AppSource | FileSource (Everything) | RecentSource    |
|     ActionSource (optional)                               |
+-----------------------------------------------------------+
| L6  Infrastructure: EverythingGateway, AppIndexStore,     |
|     UsageStore, SettingsStore, IconCache, Logger, Metrics |
+-----------------------------------------------------------+
| L7  External: Everything process, OS shell, file system   |
+-----------------------------------------------------------+
```

**Dependency rule:** a layer may only call the layer directly below it. L4 depends on nothing. Interfaces live in L3 or L4. Implementations live in L5 and L6. This keeps each part testable with fakes.

## 3. Components and their single job

| Component | Job | Must not |
| --- | --- | --- |
| SearchView | Draw the box, list, groups, status bar | Hold state or call sources |
| SearchViewModel | Debounce input, call the orchestrator, expose results | Touch Everything or the file system |
| SearchOrchestrator | Run one search session. Cancel the old one. Merge results from sources in tiers | Know how a source works inside |
| QueryParser | Turn raw text into a SearchQuery object (see Section 4) | Do I/O |
| ISearchSource | Return scored candidates for a SearchQuery | Sort across sources |
| AppSource | Search the in-memory app index | Scan the disk during a search |
| FileSource | Build Everything queries, call the gateway, map results | Call native code directly |
| EverythingGateway | The only place that calls the Everything library. Owns the single worker thread and the health state | Leak library types to upper layers |
| AppIndexStore | Build and refresh the app index in the background | Block a search |
| Ranker | Compute one score per candidate | Do I/O |
| Deduplicator | Remove repeated items across sources | Change scores |
| UsageStore | Save open counts and last-open time | Block the UI thread |
| ExclusionPolicy | Decide which paths and extensions to hide | Read the disk |
| IconCache | Load icons on demand, bounded size | Grow without a limit |
| HealthMonitor | Track state of each source and publish it | Run when the window is hidden |
| Logger and Metrics | Structured logs and counters | Log raw queries or full paths by default |

## 4. Core data contracts

Keep these as small, immutable types.

- **SearchQuery**: RawText, NormalizedText, Tokens, Mode (All, Apps, Files, Folders, Path), CategoryFilter, MaxPerCategory, SessionId.
- **Candidate**: Id, DisplayName, FullPathOrKey, Category, SourceId, Modified (nullable), Size (nullable), IsFolder.
- **ScoredResult**: Candidate, Score, MatchKind, Reasons (debug only, off in release).
- **SourceState**: Ready, Starting, Loading, Degraded, Unavailable, plus a reason code.
- **SearchSnapshot**: SessionId, Groups, IsFinal, ElapsedMs, SourceStates.

Rule: the UI binds to SearchSnapshot only. It never sees Candidate objects from a source.

## 5. Search pipeline, step by step

1. **Input.** The view model receives text. It waits 120 to 180 ms after the last key press (debounce).
2. **Guard.** If the text is empty, show Recent and Pinned items from memory. Stop. Do not call Everything.
3. **Parse.** QueryParser normalizes the text (Section 7.1) and detects the mode: a prefix such as an app, file, or folder marker, a drive path, or a UNC path.
4. **Start a session.** The orchestrator creates a new SessionId and a new cancellation token. It cancels the old session first, and it disposes the old token source after the old session ends.
5. **Tier 1 (fast, in memory).** AppSource and RecentSource answer. Publish a SearchSnapshot with IsFinal = false. Target under 50 ms.
6. **Tier 2 (Everything).** FileSource runs scoped queries through the gateway (Section 6). Use a hard timeout of 400 ms for the first pass.
7. **Merge.** Collect candidates. Remove duplicates. Score. Group by category. Keep only the top N per group.
8. **Publish.** Send the final SearchSnapshot to the UI thread. Ignore any snapshot whose SessionId is not the latest.
9. **Record.** Metrics only (timings, counts). No raw text.

**Progressive display rule:** the UI must never remove a visible result that is still valid just to re-add it. Update the list in place with a stable key. This stops flicker and extra layout work.

## 6. Everything integration rules

### 6.1 Gateway rules

- The native library keeps global state. Therefore **serialize all calls on one dedicated worker thread**. Use a bounded queue with capacity 1 for pending searches. A new request replaces the waiting one (latest wins).
- Never call the library from the UI thread.
- Request only the fields you need (name, path, and optionally size and date). Do not request all fields.
- Set a hard result cap on every query. Never ask for all matches.
- Wrap every native call in error handling. Convert library error codes into SourceState values.
- Copy results into managed memory at once. Do not hold native pointers after the call returns.
- Use a fixed, reusable buffer for path text. Do not allocate a new large buffer on each result.

### 6.2 Health state machine

```
Unavailable --probe ok--> Starting --index loaded--> Ready
Ready --call fails or timeout--> Degraded --3 fails--> Unavailable
Degraded --success--> Ready
Unavailable --probe--> (backoff: 1s, 2s, 4s ... max 30s)
```

- Probe only while the search window is visible, or once at app start. Do not poll in the background.
- If the Everything index is still loading, show a clear status. Do not show zero results as if nothing exists.
- After Everything restarts, the gateway must reconnect without an app restart.

### 6.3 Query building

- Use **safe mode** by default. The gateway escapes special characters that the user did not mean as syntax.
- Offer **power mode** by an explicit prefix. In power mode, pass the text to Everything as is.
- Run one scoped query per category (documents, images, media, code, folders, other). Run them one after another on the gateway thread, or as one combined query when the user mode is narrow. Do not open parallel native calls.
- Add the exclusion filters from ExclusionPolicy to every query. Keep the filter text short. A very long query is slow.
- Do not run a file query for fewer than 2 characters, unless the user sets a prefix.

### 6.4 Environment checks at start-up

- Everything not installed, not running, or the Lite build (no IPC). Show a setup message with a link. Do not crash.
- Library file missing or wrong CPU type. Log it and disable FileSource. Keep AppSource alive.
- Everything and the app run at different permission levels. Test it. If it fails, tell the user in plain words.
- Named instance of Everything. Read the instance name from settings.

## 7. Ranking and relevance

### 7.1 Normalize first

- Unicode normalization (NFC). Case-insensitive compare. Remove accents for compare only.
- Trim spaces. Collapse repeated spaces. Limit the query to 256 characters.
- Tokenize on spaces, dots, dashes, underscores, and camel-case boundaries.
- Support IME input: do not search while text composition is still in progress. Search when composition ends.

### 7.2 Score formula

Score = MatchQuality + CategoryBoost + UsageBoost + RecencyBoost + LocationBoost - NoisePenalty

| Part | Rule |
| --- | --- |
| MatchQuality | Exact name 100. Prefix 80. Word prefix 60. Initials match (for example, the first letters of each word) 55. Contains 40. Path only 10 |
| CategoryBoost | Apps +10 when the query is one token and matches an app prefix. Folders +5 for path-like queries |
| UsageBoost | min(25, 5 x open count), with exponential decay (half-life 30 days) |
| RecencyBoost | Up to +10 for files changed in the last 7 days, fading to 0 |
| LocationBoost | +15 in user folders (Desktop, Documents, Downloads) and pinned folders |
| NoisePenalty | -30 for cache, temp, build output, and dependency folders. -20 for hidden and system files. -10 for paths deeper than 8 levels |

**Rules**

- The score must be a pure function. Same input gives the same output.
- Break ties in a fixed order: higher score, then shorter path, then name (ordinal), then full path. Results must not jump between runs.
- Fuzzy and typo match: use it for AppSource only, and only when exact tiers give fewer than 3 results. Cap the edit distance at 2. Do not use fuzzy match on Everything results.
- Keep all weights in one settings object. Do not scatter numbers in code.

### 7.3 Grouping

Fixed group order is not best. Order the groups by their top score. Show at most 5 items per group at first, with a Show more action. Keep a hard cap on total items in memory (for example 200).

## 8. CPU and memory safety rules

Treat each rule as a code review gate.

### 8.1 CPU spike control

1. Debounce all input. Cancel old work at once.
2. Use a bounded queue (latest wins). Never queue unlimited searches. This follows Little's Law: if arrival rate is higher than service rate, a queue grows without a limit unless you drop work.
3. Use one worker thread for Everything. Use at most 2 background tasks for in-memory work. No thread-per-key-press.
4. Do not use blocking waits on the UI thread. Do not use `.Result` or `.Wait()`.
5. Build regular expressions once (static, compiled only if measured to help). Never create them in a loop.
6. Avoid LINQ chains and string concatenation in the scoring loop. Use simple loops, spans, and pooled buffers.
7. Update the UI in one batch per snapshot. Do not raise one change event per item.
8. Use UI virtualization for the result list. Cap visible rows.
9. When the window is hidden: cancel all work, stop timers, stop probing, stop icon loading.
10. Run the app index refresh at low thread priority. Throttle file watcher events (batch for 2 seconds). Never rebuild the full index on every event.
11. Respect power saver mode: increase debounce and reduce result caps.
12. Follow Amdahl's Law: measure first. Do not parallelize a part that is a small share of total time.

### 8.2 RAM leak control

1. Every event subscription needs a matching unsubscribe. Prefer weak events or a subscription object that implements IDisposable.
2. Every cancellation token source is disposed after use. Every task has an owner.
3. No static field holds a view model, view, or result list.
4. Every cache has: a maximum item count, a maximum byte size, and a time-to-live. Use LRU eviction. This applies to the result cache, the icon cache, and the path cache.
5. Do not keep old SearchSnapshot objects. Keep only the current one and the previous one (for diff).
6. Replace the list by a diff-update. Do not keep appending to one collection.
7. Native resources (handles, buffers) use SafeHandle or a try/finally pattern with a finalizer fallback.
8. Use array pooling for large temporary buffers. Return buffers in a finally block.
9. Do not call a forced garbage collection in production code. Fix the cause instead.
10. Do not keep strings for every file on the PC. The app index holds apps only. File data stays in Everything.
11. Icons: load on demand for visible rows only. Freeze image objects. Cap the cache (for example 256 icons).
12. Dispose timers and background loops when the feature shuts down.

## 9. Edge case matrix

| Case | Required behavior |
| --- | --- |
| Empty or spaces only | Show recent and pinned items. No file query |
| One character | Apps and recents only |
| Very long text or paste of 10,000 characters | Truncate to 256. No crash |
| Special characters and filter syntax typed by accident | Safe mode escapes them. No wrong filter, no exception |
| Query is a full path or drive path | Path mode: check the path, show the item or folder, then children |
| Network path or removable drive | Show results, but check existence with a short timeout only on select |
| Result file was deleted after indexing | On select, check existence. Show a clear message. Remove it from the list |
| Path longer than 260 characters | Support long paths. Do not throw |
| Non-Latin names, mixed scripts, right-to-left text | Correct match, correct display |
| Same name in many folders | Show the folder as a second line. Keep stable order |
| Same app from the start menu and from an installer folder | Deduplicate. Keep the start menu entry |
| Everything not running when the user types | Show app results at once. Show a status for files |
| Everything crashes during a query | Timeout, Degraded state, apps still work |
| Everything index still loading | Show a loading status. No false empty state |
| Thousands of matches | Hard cap. Show a hint to refine the query |
| User types fast, then pauses, then types again | Only the last session may publish |
| Sleep and resume, remote session, screen lock | Reconnect and re-check health on resume |
| App uninstalled while the window is open | Verify on launch. Remove the entry. Refresh the app index |
| Symbolic links and junctions | Do not follow in the app. Show them as normal items |
| Access denied on open | Show a clear message. Do not crash |
| Low memory | Clear caches first. Reduce caps |
| Keyboard only use, screen reader, high contrast | Full support. Every action has a key |
| Settings file corrupt | Use defaults. Rename the bad file. Log it |

## 10. Usage learning

- Store: item key, open count, last open time. Use a small local database or file.
- Write in batches on a background thread. Never write on each key press.
- Apply decay when you read, not by a timer.
- Limit the table size. Remove the oldest and lowest entries above the limit.
- Offer a Clear history command. Store keys as paths. Do not store query text.

## 11. Observability and privacy

- Log events and timings. Do not log the raw query or full paths by default. If debug mode is on, show it in the UI and reset it at restart.
- Metrics: search count, p50 and p95 time per tier, cancel count, timeout count, source state changes, cache hit rate, queue drops.
- All data stays on the device. Make this a stated rule in the code review.
- Let the user edit the exclusion list. Include sensitive folders (for example, password stores and private key folders) in the default list.

## 12. Test and verification plan

1. **Unit tests**: parser, ranker, deduplicator, exclusion policy. Use fake sources. Add property tests: the score is stable, the sort is stable, and no input throws.
2. **Contract tests**: every ISearchSource must pass the same test set (cancel, timeout, empty, error).
3. **Integration tests**: real Everything on a test folder. Cases: not running, loading, killed during a query, restarted.
4. **Benchmarks**: the scoring loop and the full pipeline with a fixed data set of 100,000 candidates. Keep the results in the repository. Fail the build on a worse result than the budget.
5. **Soak test**: a script types random queries for 8 hours, with the window opening and closing. Record working set, managed heap, handle count, thread count, and CPU every minute.
6. **Leak check**: take memory snapshots before and after 10,000 searches. Compare. Look for retained view models, event handlers, token sources, and cache growth.
7. **CPU profile**: run a typing test. Check that the UI thread stays under budget and that nothing runs when the window is hidden.
8. **Chaos test**: random delays, random native errors, and random cancel calls.

## 13. Build phases and gates

| Phase | Work | Gate to pass |
| --- | --- | --- |
| 1 | Contracts, QueryParser, Ranker, Deduplicator, fakes, unit tests | All unit tests pass. Scoring benchmark within budget |
| 2 | AppSource with a background AppIndexStore | Apps appear under 50 ms. No disk scan during a search |
| 3 | EverythingGateway: worker thread, health state machine, safe query builder | Chaos test passes. No handle or thread growth |
| 4 | FileSource, ExclusionPolicy, category queries | Result quality review on 30 sample queries |
| 5 | Orchestrator: sessions, cancel, tiers, merge, progressive display | No stale snapshot ever shown. No flicker |
| 6 | View model and view: grouping, virtualization, status messages, keyboard, accessibility | UI thread budget met |
| 7 | UsageStore and learning | Ranking improves in a replay test. Writes are batched |
| 8 | Hardening: soak, leak check, CPU profile, edge case matrix | All Section 1 budgets met |

## 14. Engineering laws applied

| Law or principle | How it applies here |
| --- | --- |
| Single Responsibility (SOLID) | One job per component (Section 3) |
| Open/Closed | Add a new source by a new ISearchSource. Do not edit the orchestrator |
| Liskov and Interface Segregation | Every source follows the same small contract |
| Dependency Inversion | Upper layers use interfaces. Infrastructure plugs in at start-up |
| Law of Demeter | The UI talks only to its view model |
| Little's Law | Bounded queues. Latest request wins |
| Amdahl's Law | Profile before any optimization |
| Postel's Law | Accept messy input. Produce clean output |
| Hyrum's Law | Keep the UI contract small. Hide library types |
| Fail fast, degrade gracefully | Timeouts and the health state machine |
| Principle of Least Astonishment | Stable sort order. Clear status messages |
| KISS and YAGNI | No content search or plugin system in version 1 |

## 15. Prompt to give the coding agent

```
Read the project and map it to the attached blueprint.
1. List each generic component and the existing code that matches it. Mark each as reuse, extend, or create.
2. List conflicts between the blueprint and the current code.
3. Propose the smallest set of changes for Phase 1 and wait for my approval.
Rules: follow the layer rules, the CPU and RAM rules (Section 8), and the budgets (Section 1). Do not add a new public dependency without asking. Write tests with each phase. Report measured numbers at each phase gate.
```

## 16. Package choice: Voidtools.Everything.Net

This project uses the NuGet package `Voidtools.Everything.Net`, version 0.1.3. It replaces any hand-written native calls in Section 6. Everything else in this blueprint stays the same.

**What the package gives**

- A typed .NET wrapper for the Everything SDK.
- Search text goes straight to Everything. Everything filters and macros work as written.
- The native SDK DLLs come as runtime assets (x64 and ARM64). Do not copy a DLL by hand.
- Optional dependency injection helpers.
- It targets .NET 8.

**What the package does not give**

- It does not contain the search engine. Everything must be installed and running.
- It does not work with Everything Lite (no IPC).
- It is a young package (version 0.1.3). Treat it as a part that can change or be replaced.

**Rules for the agent**

1. Add the package to the project that holds the infrastructure layer (L6) only. No other project may reference it.
2. `EverythingGateway` is the only class that uses the package types. No package type may appear in an interface, a Candidate, or a view model.
3. Read the package's own interfaces and README first. Use its real class names. Do not guess them.
4. Keep the single worker thread and the latest-wins queue from Section 6.1. Do not assume the package is safe for parallel calls, unless its documentation says so.
5. Use its dependency injection helper only if the project already uses a container. Register it in the existing start-up code.
6. Confirm that the project targets .NET 8 or later. If not, stop and report it.
7. Confirm that the correct native DLL lands in the output folder for each CPU type. Add a start-up check that logs a clear error if it is missing.
8. Pin the exact version in the project file. Do not use floating versions.
9. Add a contract test that runs the gateway against fakes, so a package change cannot break the app silently.

**Exit plan** If the package becomes a problem, replace only the inside of `EverythingGateway`. The two other options are the `EverythingSearchClient` package (no native DLL) or direct SDK calls. No other layer changes.

## 17. Protecting the existing search

The new work must not break the search the app has today. Use this method.

1. **Freeze current behavior.** Before any change, write characterization tests for the existing search: sample queries and the results they give today. These tests must stay green.
2. **Wrap, do not rewrite.** Put the existing search behind the `ISearchSource` contract as `LegacySearchSource`. Its inside does not change.
3. **Add the new parts beside it.** `FileSource` (Everything) and the new orchestrator are new code. They do not edit the legacy code.
4. **Use a feature flag.** The setting `UseNewSearch` is off at first. When it is off, the app takes the old path only. When it is on, the new orchestrator runs.
5. **Add a kill switch.** If the new path throws or times out three times in a row, the app returns to the old path for the session, and logs the reason.
6. **Compare in shadow mode.** In a debug build, run both paths and log only the differences in result order and count (no raw queries). Use this to tune the ranker.
7. **Switch on by stages.** First your own build, then a small group, then everyone. Remove the old path only after one full release with no fallback events.

**Gate:** the characterization tests pass with the flag off and with the flag on. If a test differs on purpose, record the reason in the test.
