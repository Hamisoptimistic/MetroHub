# Markdown Widget — Architecture & Status

Separate widget (Notepad untouched). Write/Read tabs. Type/paste in the tile + open `.md` files.
Plain code blocks (no SyntaxHigh). Local-only images. Raw text persisted, never the rendered doc.

## Package

- `MdXaml` **1.27.0** stable (MIT, targets net6.0/netcore3.0/net45 — the net6 asset rolls forward
  onto net10.0-windows, zero native deps).
- Rejected: `Markdig.Wpf` (archived 2021), `MdXaml.SyntaxHigh`/`Html`/`Svg`/`AnimatedGif`
  subpackages (weight for unused features), WebView2 (~100 MB+ runtime to render text).
- API surface used: `new MdXaml.Markdown()`, `engine.Transform(md)` → `FlowDocument`,
  `Markdown.DocumentStyle` for MetroHub theming.
- Engine facts worth remembering: it parses `details`-style tags, it loads images over HTTP
  (`ImageLoaderManager` + `HttpClient`), and it decodes `data:image/png|jpg|jpeg;base64,…` natively.
  **The gate is therefore the only thing enforcing "offline, local images only."**

## Persistence (three-way split)

Document text must never live in `layout.json`: a typing pause used to re-serialize and fsync the
whole hub layout (all tiles, all documents, `WriteThrough` + `FlushFileBuffers`).

| What | Where | Written when |
| :--- | :--- | :--- |
| The user's own document | `tab.SourceFilePath` (unchanged) | Save / Save As / Ctrl+S, every dirty file-backed tab on the autosave debounce, and before a tab is closed or the hub hides/exits (mtime guard kept) |
| Widget state mirror | `%LocalAppData%\MetroHub\config\widgets\markdown\{tileId}.json` (+ `.bak` under `backups\widgets\markdown\`) | Idle autosave (1.5 s), immediate on tab switch/close/mode toggle/hide/exit — holds unsaved edits |
| Layout + groups | `%LocalAppData%\MetroHub\config\layout.json` | Only the slim pointer below, once per tile; otherwise untouched |

- One state file **per Markdown tile**, keyed by `TileModel.Id` — two widgets never share a file.
  Not per tab: that would mean N files, orphan churn on close, and a rename per tab retitle.
- `TileModel.SettingsJson` carries only `{ "schemaVersion": 2, "stateRef": "<tileId>" }`, which is
  what keeps `LayoutHistoryService` snapshots and undo/redo working while stripping document text.
  A side effect worth knowing: **undo/redo no longer reverts document text.**
- Load chain: state file → its `.bak` → legacy inline payload in `SettingsJson` (one-way migration,
  both shipped shapes: multi-tab collection and the original single-note fields) → blank "Notes" tab.
- Orphan state files (tile id absent from the layout) are pruned **at startup only**, so
  unpin → undo in the same session still finds its document.
- The state file is an autosave mirror, written with a temp file + atomic move and *without* a
  forced flush. Layout/groups/settings keep `StorageService`'s durable write. (Follow-up: the two
  atomic writers could be unified — `StorageService.SaveAtomic` is still inline.)

### Same split, now applied to Notepad and Habit

Both had the identical defect in different shapes, and both now use the same
`WidgetStateStore` + slim-pointer pattern:

| Widget | State file | Why it mattered |
| :--- | :--- | :--- |
| Notes & Tasks | `config\widgets\notepad\{tileId}.json` | A 400 ms typing debounce called `SaveSettings()` → `SaveGroupsAndLayout()`: two `WriteThrough` + `FlushFileBuffers` writes of the whole layout (note body included) on every pause. |
| Habit | `config\widgets\habit\{tileId}.json` | `SaveSettings()` → `SaveGroupsAndLayout()` fired **immediately** on every day toggle, mark-failed, clear, reset-all and habit setup — plus on `Pause()`, so every hub hide wrote the whole layout twice. |

- Notepad keeps its 400 ms typing debounce; it now reaches `SaveContent()` (state file only).
- Habit's payload is small, so content saves stay immediate and only the layout pointer is debounced.
- Habit's legacy read path stays case-insensitive (`PropertyNameCaseInsensitive`), because pre-split
  payloads were PascalCase — the slim schema-2 payload is camelCase. Both load.
- Both write `{ "schemaVersion": 2, "stateRef": "<tileId>" }` into `SettingsJson`, and both are
  diff-guarded, so the every-hub-hide `Pause()` is free when nothing changed.

## How it renders (direct, no HTML)

- `engine.Transform(markdownText)` produces a native WPF `FlowDocument` (selectable ClearType text).
- Widget owns a `FlowDocumentScrollViewer` for the Read tab — own scrolling/padding/tile chrome.
- One shared static `Markdown` engine for app lifetime (lazy-created inside the first idle callback).
- Styling via `engine.DocumentStyle`: `AppFontFamily`, white headers, accent links, dark code blocks.
- Post-transform passes (all two-phase collect → mutate, all UI-thread): `MarkdownCodeBlocks`
  (fences → plain `Paragraph Tag=CodeBlock`), `MarkdownTaskLists` (hide bullets, unify ballot font),
  `MarkdownInlineCode` (CodeSpan → padded `Border` chip).

## Startup / threading

- `Transform` creates WPF objects → **must run on the UI thread** (no `Task.Run`).
- First render is deferred to `DispatcherPriority.ApplicationIdle` in the view's `Loaded` handler.
- Edits re-render on a 300 ms `DispatcherTimer` debounce (`Background` priority), not per keystroke.
- `IsVisibleChanged`: off-screen tiles skip the idle render until first visible.
- Keyboard shortcuts are `InputBindings` on the view (routed from the focused descendant), so no
  widget needs `Focusable="True"` and nothing steals focus from the canvas. `Ctrl+Z`/`Ctrl+Y` stay
  with the editor; the editor's undo history is **cleared whenever the document under it is
  replaced**, or Ctrl+Z would paste one tab's text into another.

## Defensive gates (untrusted pasted input)

Pure pre-checks (µs string scans) run before every transform, cheapest first, all in the testable
`MarkdownRenderGate`:

- Length > 100 KB → refuse, "too long for preview" (Write tab still fully usable).
- Code masked first (fences + variable-length inline spans) so no later step can rewrite code.
- HTML entities decoded **before** the raw-HTML strip, so `&lt;details&gt;` cannot survive the gate
  as an entity and be re-materialized as live markup afterwards; script/style blocks go with their
  contents, `<br>` becomes a hard break, autolinks and prose comparisons are preserved.
- Reference links resolved (the engine parses neither usages nor definitions).
- Remote / `data:` / missing / relative-without-a-base image paths → placeholder text lines;
  > 10 images → placeholder; surviving local paths resolved against the source `.md` directory.
- Tables: > 20 rows → first 20 + "+N more rows", > 8 columns → 8 + "+N columns". A block only counts
  as a table when the separator row is pipe-delimited and the header has ≥ 2 cells — otherwise prose
  that merely brackets a `---` rule between pipe-ish lines got truncated.
- Nesting: quote depth > 3 flattened, code-like indent > 12 clamped (list markers exempt).

## Files

- `MarkdownWidgetSettings.cs` — slim layout payload (`schemaVersion`, `stateRef`) + legacy reader.
- `MarkdownWidgetState.cs` — state-file payload: tabs, active index, mode, `MaxTabs`.
- `MarkdownWidgetViewModel.cs` — tabs, per-tab dirty tracking, split save intents, gates, render.
- `MarkdownRenderGate.cs` — pure gate/sanitize helpers (no WPF types).
- `MarkdownQuarantine.cs`, `MarkdownCodeBlocks.cs`, `MarkdownTaskLists.cs`, `MarkdownInlineCode.cs` —
  post-transform hardening.
- `MarkdownLog.cs` — removable debug log, **off in Release**, written to
  `%LocalAppData%\MetroHub\logs\markdown.log`, rotated after 512 KB.
- `MarkdownWidgetView.xaml` (+`.xaml.cs`) — themed reader, watermark, InputBindings, undo isolation.
- `WidgetStateStore.cs` (Core/Services) — per-widget state files + pruning.
- Registry: `WidgetRegistry` entry (`id: markdown`, Productivity, Mega/Huge/Canvas/Full, default Mega).

## Tests (real counts)

`dotnet test` → **150/150 passing** (91 pre-existing + 47 Markdown + 6 Notepad + 6 Habit). The suites
covering this split:

| File | Tests | Covers |
| :--- | ---: | :--- |
| `MarkdownWidgetTests.cs` | 28 | Pure gate: length, code masking, entity/tag ordering, image policy, table row/column caps and the pipe-prose classifier, nesting, front matter, nested + reference links, task lists, local path resolution |
| `MarkdownRegressionTests.cs` | 8 | STA/post-transform: fence → plain paragraph (no AvalonEdit), code in lists, task-list bullets + ballot font, mixed lists, inline-code chips, quarantine fallback, empty hint |
| `MarkdownPersistenceTests.cs` | 11 | State store round-trip/rollover/prune, per-tab write-back, close-tab flush and locked-file refusal, both legacy migrations, state-file precedence, tab cap, undo-reset signal, autosave not touching the layout payload |
| `NotepadPersistenceTests.cs` | 6 | Typing writes only the state file, note + tasks round-trip, legacy inline migration, state-file precedence, hub-hide flush, unchanged payload not rewritten |
| `HabitPersistenceTests.cs` | 6 | Day toggle writes only the state file, habit + grid round-trip, legacy PascalCase migration, state-file precedence, repeated lifecycle saves, unchanged payload not rewritten |

## Follow-ups (known, not done)

- **Still on the old path**: Dino, Network, Radio, Pomodoro, CaffeineSleep, BrightnessControls,
  AudioControls, Rover, Weather, Media (and Clock/Photos/Quotes/Stub, which call
  `SaveGroupsAndLayout()` straight from `SaveSettings()`). The first group never persists promptly at
  all — their state only reaches disk when something unrelated writes the layout, so a hard kill
  loses it. The second group saves rarely enough to be harmless today, but pays the same full-layout
  write.
- Widget saves still run on the UI thread. A single background writer would remove the stall from
  the input path entirely.
- The three save flows (Markdown, Notepad, Habit) are now the same shape three times over — a shared
  base class would collapse them.
- Tab-strip close animation is still a 120 ms fade before the command runs; the guards make it safe
  but a synchronous close with a separate transition would be simpler.
- `ClockFontTests` is a scratch harness: it renders PNGs to a hard-coded absolute path and asserts
  nothing.
