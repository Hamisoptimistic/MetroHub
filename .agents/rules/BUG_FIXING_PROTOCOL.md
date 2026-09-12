---
trigger: manual
---

# Bug Fixing Protocol — Universal Systematic Debugging & Resolution Guide

> **CRITICAL DIRECTIVE**: When asked to fix ANY bug — UI glitch, data corruption, race condition, performance issue, crash, API misbehavior, or anything else — you MUST follow this protocol end-to-end. Do NOT skip steps. Do NOT guess at solutions. Do NOT apply surface-level patches. Treat every bug as a forensic investigation — understand the root cause BEFORE writing a single line of fix code.

---

## Phase 0: STOP AND LISTEN — Understand the User's Report

Before doing ANYTHING, extract these from the user's description:

1. **What is the expected behavior?** (What should happen)
2. **What is the actual behavior?** (What does happen)
3. **What are the exact reproduction steps?** (Click X, then Y, then Z)
4. **When did it start?** (After a specific change? Always broken?)
5. **Is it intermittent or 100% reproducible?**
6. **What environment?** (OS version, browser, hardware, display scale, etc.)

### Ask Clarifying Questions

If the user's report is vague or ambiguous, ASK. Don't assume. Examples:

| Vague report | Questions to ask |
|---|---|
| "it's broken" | What specifically is broken? What do you see vs. what you expect? |
| "it's slow" | How slow? Which action is slow? Was it fast before? When did it change? |
| "it crashes" | When exactly? What were you doing? Is there an error message? |
| "it's not syncing" | What data is wrong? Is it delayed, missing, or showing old values? |
| "it looks wrong" | Can you screenshot it? Which element? On which screen size? |

**Write down the precise symptom before proceeding:**

> ✅ GOOD: "When the user clicks YouTube's seekbar to jump to 2:30, MetroHub's seekbar resets to 0:00 for ~1.5 seconds, then snaps to the correct position."
>
> ❌ BAD: "Media player sync is broken"

---

## Phase 1: READ THE CODE — Full Context Before Diagnosis

### 1.1 Read the ENTIRE relevant file(s)

Do NOT skim. Do NOT read just the function you think is broken. Read the **entire file** top to bottom. You need to understand:

- **All state variables** and what they track
- **All event handlers** and when they fire
- **All timers/intervals** and their cadence
- **The complete data flow** from input → processing → output
- **Thread safety** (locks, dispatchers, async patterns, reentrancy)
- **Edge cases** already handled (or not)
- **Dispose/cleanup logic** (resource leaks cause delayed bugs)

### 1.2 Read connected files

Bugs rarely live in isolation. Also read:

- **View/UI code**: How does the UI consume the data? Bindings, event handlers, animations
- **Models/DTOs**: Is the data structure correct? Missing fields? Wrong types?
- **Services/APIs**: Is the data source reliable? What does it actually return?
- **Configuration**: Wrong settings, missing defaults, environment-specific values?

### 1.3 Read the standards/rules docs

Check `.agents/rules/` for any existing standards that govern the area you're debugging. The fix MUST comply with existing architecture patterns. Violating established patterns to "fix" a bug just creates new bugs.

### 1.4 Map the data flow

Draw the complete pipeline from source to symptom:

```
[Data Source] → [Processing Layer] → [State Management] → [UI Binding] → [Rendering]
```

For example:
- **Network bug**: API response → deserialization → cache → ViewModel → View
- **UI glitch**: User input → event handler → state update → PropertyChanged → render
- **File bug**: FileSystem → read/parse → transform → write → verify
- **Timing bug**: Timer tick → state query → calculation → UI dispatch → animation

Understanding the **complete chain** reveals where data can get lost, delayed, corrupted, or overwritten.

---

## Phase 2: REPRODUCE AND OBSERVE — Understand the Failure Mode

### 2.1 Reproduce the bug yourself

Before fixing anything, **confirm you can trigger the bug**. If you can't reproduce it:
- Ask the user for more specific steps
- Check if it's environment-specific (screen resolution, OS version, hardware)
- Check if it's timing-dependent (race condition)
- Check if it requires specific data/state (empty list, long string, special characters)

### 2.2 Add diagnostic logging

Add `Debug.WriteLine` / `console.log` / `print` statements at critical decision points to trace what's ACTUALLY happening at runtime:

```csharp
// C# / WPF example
System.Diagnostics.Debug.WriteLine(
    $"[ComponentName] METHOD: input={inputValue}, " +
    $"currentState={_stateVar}, " +
    $"decision={someCondition}");
```

```javascript
// JS example
console.log(`[ComponentName] handler: event=${e.type}, state=`, this.state);
```

**Log at EVERY branch point** — you need to see which code path is executing and why.

### 2.3 Write diagnostic scripts to isolate the source

Create standalone scripts that test the underlying API/system independently of your application code. This isolates whether the bug is in YOUR code or in the OS/library/API/browser.

**Examples:**

| Bug type | Diagnostic approach |
|---|---|
| OS API returns wrong data | Write a script that calls the API directly and logs results |
| CSS layout broken | Create a minimal HTML file with just the broken element |
| Database query wrong | Run the raw query in a SQL client and check results |
| Network timing issue | Use `curl`/`fetch` to call the endpoint directly and measure timing |
| File parsing bug | Write a script that parses the file and dumps the parsed structure |

### 2.4 Narrow down the scope

Use **binary search debugging**: If you have a long pipeline, check the data at the MIDPOINT.
- If data is correct at midpoint → bug is in the second half
- If data is wrong at midpoint → bug is in the first half

Repeat until you find the exact point where correct data becomes incorrect.

---

## Phase 3: ROOT CAUSE ANALYSIS — Go Deep, Not Wide

### 3.1 Ask "WHY?" five times (Five Whys Technique)

Never stop at the first answer. Keep asking WHY until you reach the fundamental cause:

```
WHY does the UI show wrong data?
  → Because the ViewModel has wrong data

WHY does the ViewModel have wrong data?
  → Because an event handler overwrote it with stale data

WHY did the event handler have stale data?
  → Because the OS/API sends out-of-order updates during transitions

WHY does our code accept out-of-order updates?
  → Because we have no freshness/ordering check on incoming data

WHY don't we have a freshness check?
  → Because the original code assumed events always arrive in order
  
ROOT CAUSE: Missing freshness validation on incoming async data
```

**The root cause is ALWAYS at the bottom of the chain, not the top.**

### 3.2 Identify ALL code paths that touch the broken state

Search for every place the broken variable/state is read or written:

```bash
# Find all references to the broken state
grep -n "BrokenVariable" *.cs
grep -n "BrokenVariable" *.xaml
```

Common bug patterns:
- **Two writers, one wins**: Two code paths both modify the same state, and one overwrites the other's work
- **Stale closure**: An async callback captures a variable that changed before the callback executes
- **Missing lock**: Two threads read-modify-write the same state without synchronization
- **Event storm**: An event handler triggers the same event again, causing infinite recursion or rapid re-entry
- **Dispose race**: Object is disposed while an async operation is still using it

### 3.3 Classify the bug type

| Category | Common causes | Fix strategy |
|---|---|---|
| **Data correctness** | Wrong calculation, off-by-one, type coercion | Fix the math, add assertions |
| **Race condition** | Missing lock, async ordering, event reentrancy | Add synchronization, use freshness tokens |
| **Stale data** | Cache not invalidated, old event applied after new one | Add versioning/epoch, freshness gates |
| **Resource leak** | Missing dispose, uncancelled timer, dangling event handler | Add cleanup in Dispose, use `using`, cancel tokens |
| **UI glitch** | Wrong binding, animation conflict, layout thrashing | Fix binding, isolate animations, batch updates |
| **Performance** | O(n²) loop, unnecessary allocations, blocking UI thread | Profile, optimize hot path, move work off UI thread |
| **Crash/exception** | Null reference, index out of bounds, disposed object | Add null checks, validate inputs, guard async paths |

---

## Phase 4: RESEARCH — Learn From Others Who Solved This

### 4.1 When to research

Research is MANDATORY when:
- The bug involves **OS APIs, browser behavior, or third-party libraries** you don't control
- The bug is a **known limitation** of a platform (e.g., WinRT async delivery quirks)
- You've never seen this class of bug before
- Your first fix attempt didn't work

### 4.2 Where to search (use ALL of these)

**1. GitHub Code Search** — Find how other projects handle the same API/pattern:
```
site:github.com "FunctionName" "language:csharp"
site:github.com "APIName" fix OR workaround OR issue
```
Look at well-known open-source projects in the same domain.

**2. Read the source code** of the component causing the bug:
- Browser bugs → Read Chromium/Firefox source
- Windows API bugs → Read WinRT/Win32 source or Microsoft reference source
- Library bugs → Read the library's GitHub repo
- Don't just read docs — read the IMPLEMENTATION to understand edge cases

**3. Search forums and issue trackers:**
- GitHub Issues on the library/framework you're using
- Stack Overflow with exact API names and error messages
- Reddit (relevant subreddits: r/csharp, r/wpf, r/webdev, r/reactjs, etc.)
- Microsoft Developer Community, Mozilla Bugzilla, Chromium bug tracker

**4. Search with specific error patterns:**
```
"ExactErrorMessage" site:stackoverflow.com
"APIName" "unexpected behavior" OR "bug" OR "workaround"
"ClassName.MethodName" returns wrong OR incorrect OR stale
```

### 4.3 Study reference implementations

Find 2-3 open-source projects that solve the same problem and study their approach:
- What patterns do they use?
- What edge cases do they handle that you don't?
- What anti-glitch / defensive logic do they have?
- Did they file issues about the same problem you're seeing?

### 4.4 Read official documentation critically

Documentation describes the **intended** behavior. Bugs often exist in the gap between intended and actual behavior. Look for:
- "Note" and "Important" callouts (often describe quirks)
- "Known issues" sections
- Version-specific behavior changes
- Threading/concurrency warnings

---

## Phase 5: DESIGN THE FIX — Architecture Before Code

### 5.1 Write down the fix strategy BEFORE coding

Answer these questions in writing:

1. **What is the root cause?** (One sentence)
2. **What is the conceptual fix?** (Not code — the idea)
3. **What defense layers are needed?** (Never single-point-of-failure)
4. **What edge cases must be handled?**
5. **What existing behavior must be preserved?**
6. **What is the performance impact?**

### 5.2 Design with defense-in-depth

Never rely on a single check. Good fixes have **multiple independent safety layers**:

```
Layer 1: Input validation     → Reject obviously invalid data at entry point
Layer 2: Freshness/ordering   → Reject stale or out-of-order data
Layer 3: Sanity check         → Detect impossible state transitions
Layer 4: Recovery mechanism   → Self-heal if bad state is detected
```

If Layer 1 fails to catch a bad input, Layer 2 should catch it.
If Layer 2 also fails, Layer 3 should detect the impossible state.
If everything fails, Layer 4 recovers gracefully.

### 5.3 Consider the blast radius

Before implementing, ask:
- **What else could this fix break?** Trace all callers of modified functions
- **Does this change any public API contracts?** (Return types, side effects, timing)
- **Does this affect performance?** (Extra allocations, more frequent polling, longer locks)
- **Is this fix forward-compatible?** (Will it still work when the OS/library updates?)

### 5.4 Choose the minimal effective fix

Prefer the smallest change that fully fixes the root cause. Don't:
- Refactor unrelated code in the same change
- "Improve" code style while fixing a bug
- Add features alongside bug fixes

---

## Phase 6: IMPLEMENT — Write the Fix

### 6.1 Make surgical changes

- Change ONLY what needs to change
- Preserve all existing comments and documentation unrelated to the bug
- Don't rename variables or restructure code unless directly required by the fix
- Keep the diff as small and focused as possible

### 6.2 Name new things clearly

Variable and method names should describe their PURPOSE:

```csharp
// ❌ BAD — cryptic names
private int _cnt = 0;
private long _ts = 0;
private bool _flag = false;

// ✅ GOOD — self-documenting names
private DateTimeOffset _lastAcceptedUpdateTime = DateTimeOffset.MinValue;
private long _recoveryWindowExpiresAt = 0;
private bool _isInSeekRecovery = false;
```

### 6.3 Handle ALL edge cases

For every check you add, ask:
- What if this value is `null` / `default` / `0` / `empty`?
- What if this is called from a different thread?
- What if the object is disposed mid-operation?
- What if two events fire simultaneously?
- What if the user does this action rapidly (spam-clicking)?
- What if the data source is unavailable or returns an error?

### 6.4 Reset state cleanly on context changes

When context changes (new data source, new document, new session, navigation):
ALL accumulated state from the previous context MUST be reset. Forgetting to reset even ONE field causes ghost state from the previous context to corrupt the new one.

### 6.5 Add comments explaining WHY, not WHAT

```csharp
// ❌ BAD — restates the code
// Check if position is less than 1.5 seconds
if (incomingPos <= TimeSpan.FromSeconds(1.5))

// ✅ GOOD — explains the reasoning
// Chromium emits transient 0:00 during seek/pause transitions.
// Reject any position <1.5s when we were at ≥2.5s on a long track.
if (incomingPos <= TimeSpan.FromSeconds(1.5) &&
    _lastKnownPosition >= TimeSpan.FromSeconds(2.5))
```

---

## Phase 7: VERIFY — Prove It Works

### 7.1 Build / compile first

```bash
dotnet build -c Release     # C#
npm run build               # JS/TS
cargo build                 # Rust
```

Fix ALL warnings and errors. A fix that doesn't compile is not a fix.

### 7.2 Test the exact reproduction steps

Go back to the user's original report and test EXACTLY what they described. Don't test something slightly different and assume it covers the case.

### 7.3 Test edge cases

Create a mental checklist based on the bug category:

**For data/sync bugs:**
- Empty data, null data, maximum data
- Rapid successive operations
- Operation during state transition
- Operation while disconnected/reconnected

**For UI bugs:**
- Different window sizes / DPI scales
- Rapid clicking / keyboard mashing
- Focus changes (alt-tab, minimize, restore)
- Dark mode / light mode / high contrast

**For performance bugs:**
- Small dataset (1 item) vs large dataset (10,000 items)
- Cold start vs warm cache
- Single operation vs burst of operations

**For crash bugs:**
- Reproduce the original crash — it should no longer occur
- Try adjacent scenarios that might trigger similar crashes
- Verify error handling doesn't swallow important exceptions

### 7.4 Test for regressions

Your fix must not break anything that was previously working. Test:
- The happy path (normal usage)
- Features adjacent to the fix
- Any feature that shares code/state with the fixed area

### 7.5 Test on the user's environment

If the bug was environment-specific, verify the fix works in that exact environment.

---

## Phase 8: DOCUMENT — Update Standards, Code, and Rules

### 8.1 Update standards docs

If your fix establishes a new pattern, defensive technique, or protocol, document it in the relevant `.agents/rules/*.md` file so ALL future code follows the same approach.

### 8.2 Add inline code comments

Every non-obvious defensive check should have a comment explaining:
- **What attack/failure it guards against**
- **Why this specific threshold/value was chosen**
- **What happens if this guard is removed** (so nobody "cleans it up" later)

### 8.3 Publish and inform the user

Deploy the fix and tell the user:
- What the root cause was (briefly)
- What you changed (files and concept)
- How to test it
- Any caveats or known limitations

---

## Anti-Patterns — What NOT To Do When Fixing Bugs

### ❌ DON'T: Guess and patch

```
"I think the problem might be here... let me try changing this value"
→ You'll waste hours trying random changes. UNDERSTAND the root cause first.
```

### ❌ DON'T: Fix symptoms instead of causes

```
"The UI flickers? Just add a 200ms debounce!"
→ The debounce masks a data integrity issue. Find WHY it flickers.
```

### ❌ DON'T: Add arbitrary delays/sleeps

```
"Just await Task.Delay(500) before updating"
→ This makes the app feel sluggish and doesn't fix the root cause. 
  The delay might work on your machine but fail on slower/faster ones.
```

### ❌ DON'T: Catch and swallow exceptions

```csharp
try { DoThing(); } catch { } // "Fixed the crash!"
→ You silenced the symptom. The underlying corruption still happens.
```

### ❌ DON'T: Assume external data sources are correct

```
"The API says the value is 0, so it must be 0"
→ APIs, OS events, and hardware can send transient, stale, or corrupted data.
  Always validate with freshness checks, sanity bounds, and multi-sample verification.
```

### ❌ DON'T: Skip the research phase

```
"I'll just try different approaches until something works"
→ Other developers have already solved this. Read their code. Read the 
  source of the component causing the issue. 30 minutes of research 
  saves 3 hours of trial-and-error.
```

### ❌ DON'T: Make the fix bigger than it needs to be

```
"While I'm in here, let me also refactor this and rename that..."
→ Every line you change is a line that could introduce a new bug.
  Fix the bug. Only the bug. Nothing else.
```

### ❌ DON'T: Ignore thread safety

```
"It works in my tests so it's fine"
→ Race conditions are intermittent by nature. If multiple threads 
  touch the same state, add proper synchronization even if you 
  can't reproduce a race in testing.
```

---

## Quick Reference Checklist

```
PHASE 0 — LISTEN
  □ Extract precise symptom from user report
  □ Ask clarifying questions if anything is vague
  □ Write down expected vs. actual behavior

PHASE 1 — READ
  □ Read the ENTIRE relevant source file(s) top to bottom
  □ Read connected files (View, Model, Service, Config)
  □ Read existing standards/rules docs
  □ Map the complete data flow from source to symptom

PHASE 2 — REPRODUCE
  □ Confirm you can trigger the bug
  □ Add diagnostic logging at critical decision points
  □ Write standalone diagnostic scripts to isolate the source
  □ Use binary search to narrow down where data goes wrong

PHASE 3 — ROOT CAUSE
  □ Apply Five Whys to reach the fundamental cause
  □ Search for ALL code paths that touch the broken state
  □ Check for race conditions, stale data, missing resets
  □ Classify the bug type (data, race, leak, UI, perf, crash)

PHASE 4 — RESEARCH
  □ Search GitHub for open-source projects handling the same scenario
  □ Read the source code of any external component involved
  □ Search Stack Overflow, Reddit, issue trackers for known issues
  □ Study 2-3 reference implementations for their defensive patterns

PHASE 5 — DESIGN
  □ Write the fix strategy before writing code
  □ Design defense-in-depth (multiple independent safety layers)
  □ Consider blast radius — what else could this break?
  □ Choose the minimal effective fix

PHASE 6 — IMPLEMENT
  □ Make surgical changes — only what's needed
  □ Use clear, self-documenting names for new variables
  □ Handle all edge cases (null, empty, concurrent, disposed)
  □ Reset all accumulated state on context changes
  □ Add comments explaining WHY, not WHAT

PHASE 7 — VERIFY
  □ Build with zero warnings/errors
  □ Test the exact reproduction steps from user's report
  □ Test edge cases relevant to the bug category
  □ Test for regressions in adjacent features

PHASE 8 — DOCUMENT
  □ Update standards docs with new patterns/protocols
  □ Add inline code comments on non-obvious defensive checks
  □ Inform user of root cause, fix, and how to test
```
