# Bug Fixing Protocol — Systematic Debugging & Resolution Guide

> **CRITICAL DIRECTIVE**: When asked to fix a bug, you MUST follow this protocol end-to-end. Do NOT skip steps. Do NOT guess at solutions. Do NOT apply surface-level patches. Treat every bug as a forensic investigation — understand the root cause BEFORE writing a single line of fix code.

---

## Phase 0: STOP AND LISTEN — Understand the User's Report

Before doing ANYTHING, extract these from the user's description:

1. **What is the expected behavior?** (What should happen)
2. **What is the actual behavior?** (What does happen)
3. **What are the exact reproduction steps?** (Click X, then Y, then Z)
4. **When did it start?** (After a specific change? Always broken?)
5. **Is it intermittent or 100% reproducible?**
6. **What environment?** (Which OS, browser, player, device, etc.)

### Ask Clarifying Questions

If the user's report is vague, ASK. Don't assume. Example:

> User: "the media player is never in sync"
> BAD: Immediately start coding a fix
> GOOD: "When you say out of sync — does the seekbar jump back to 0:00? Does it show the wrong time? Does it lag behind? Does it happen on seek, pause, or play? Which browser/player?"

**Write down the precise symptom before proceeding.** Example from a real bug:

> **Symptom**: When user seeks on YouTube (clicks YouTube's seekbar to jump to a new position), MetroHub's seekbar resets to 0:00 briefly, then after ~1-2 seconds shows the correct position. Clicking play/pause "fixes" the sync.

---

## Phase 1: READ THE CODE — Full Context Before Diagnosis

### 1.1 Read the ENTIRE relevant file(s)

Do NOT skim. Do NOT read just the function you think is broken. Read the **entire file** top to bottom. You need to understand:

- All state variables and what they track
- All event handlers and when they fire
- All timers and their intervals
- The complete data flow from OS → ViewModel → View
- Thread safety (locks, dispatchers, async patterns)
- Edge cases already handled (or not)

### 1.2 Read the View/XAML code-behind

Bugs often live in the interaction between ViewModel and View. Read:
- How the View subscribes to ViewModel changes
- Mouse/input event handlers (drag, scrub, click)
- Animation code that might interfere with data binding

### 1.3 Read the standards/rules docs

Check `.agents/rules/` for any existing standards that govern the area you're debugging. The fix must comply with existing architecture.

### 1.4 Map the data flow

Draw a mental (or written) map:

```
OS Event (WinRT thread) 
  → Dispatcher.InvokeAsync 
    → SyncPlaybackState() 
      → SyncTimelineProperties() 
        → Updates PositionSeconds, ProgressRatio 
          → PropertyChanged fires 
            → View.OnViewModelPropertyChanged() 
              → UpdateProgressVisuals()
```

Understanding the **complete chain** reveals where data can get lost, delayed, or corrupted.

---

## Phase 2: REPRODUCE AND OBSERVE — Understand the Failure Mode

### 2.1 Add diagnostic logging (if needed)

Add `Debug.WriteLine` statements at critical points to trace what the OS is actually sending:

```csharp
System.Diagnostics.Debug.WriteLine(
    $"[MediaWidget] TIMELINE: pos={timeline.Position}, " +
    $"lastUpdate={timeline.LastUpdatedTime}, " +
    $"status={playback?.PlaybackStatus}, " +
    $"stored={_lastTimelinePosition}");
```

### 2.2 Write diagnostic scripts

Create PowerShell/Python scripts that directly query the same OS APIs your code uses. This isolates whether the bug is in YOUR code or in the OS/browser behavior.

Example — querying GSMTC directly from PowerShell:
```powershell
# Check what the OS is actually reporting
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$manager = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync().GetAwaiter().GetResult()
$session = $manager.GetCurrentSession()
$timeline = $session.GetTimelineProperties()
Write-Host "Position: $($timeline.Position)"
Write-Host "LastUpdated: $($timeline.LastUpdatedTime)"
```

### 2.3 Identify the REAL source of the problem

The bug is almost never where you first think it is. Common traps:

| What it looks like | What it actually is |
|---|---|
| "The UI shows wrong data" | The OS is sending wrong/stale data |
| "The timer is broken" | An event handler is overwriting the timer's work |
| "The position resets to 0" | The OS sends a transient 0 during state transitions |
| "It works sometimes" | Race condition between multiple async code paths |
| "It fixes itself after a click" | The click triggers a re-sync that masks the real issue |

---

## Phase 3: ROOT CAUSE ANALYSIS — Go Deep, Not Wide

### 3.1 Ask "WHY?" five times (Five Whys)

```
WHY does the seekbar reset to 0:00?
  → Because SyncTimelineProperties receives position=0 from the OS

WHY does the OS send position=0?
  → Because Chromium sends a transient 0:00 snapshot during seek transitions

WHY does our code accept it?
  → Because we have no way to distinguish "real 0:00" from "transient glitch 0:00"

WHY can't we distinguish them?
  → Because we don't track the FRESHNESS of incoming updates (LastUpdatedTime)

WHY don't we track freshness?
  → Because the original code relied only on position value, not metadata
```

**The root cause is at the BOTTOM of the chain**, not the top.

### 3.2 Identify ALL code paths that touch the broken state

Search for every place the broken variable is read or written:

```
grep -n "PositionSeconds" MediaWidgetViewModel.cs
grep -n "_lastTimelinePosition" MediaWidgetViewModel.cs
```

Often bugs exist because **two different code paths** both modify the same state and one overwrites the other's work.

### 3.3 Check thread safety

If multiple threads can modify the same state:
- Is there a lock?
- Is the lock held for the ENTIRE read-modify-write cycle?
- Are WinRT events being marshaled to the UI thread correctly?
- Can an event handler fire DURING another event handler?

---

## Phase 4: RESEARCH — Learn From Others Who Solved This

### 4.1 Search strategy (use ALL of these)

When the bug involves OS APIs, browser behavior, or third-party interactions, you MUST research how others handle it:

1. **GitHub Code Search**: Find open-source projects using the same API
   - Search for function names: `GetTimelineProperties`, `TimelinePropertiesChanged`
   - Search for class names: `GlobalSystemMediaTransportControlsSession`
   - Look at projects like ModernFlyouts, EarTrumpet, Rainmeter NowPlaying

2. **Read the actual source code** of the component causing the bug
   - For Chromium bugs: Read `system_media_controls_win.cc` in the Chromium source
   - For Windows API bugs: Read Microsoft documentation AND the WinRT source
   - Don't just read docs — read the IMPLEMENTATION to understand edge cases

3. **Search forums and issue trackers**:
   - GitHub Issues on related projects
   - Stack Overflow with exact API names
   - Reddit (r/csharp, r/wpf, r/dotnet)
   - Microsoft Developer Community

4. **Search with specific error patterns**:
   - `"TimelinePropertiesChanged" "position" "zero" seek`
   - `"GSMTC" "chromium" "position resets"`
   - `site:github.com GetTimelineProperties position sync`

### 4.2 Read reference implementations

Find 2-3 open-source projects that do the same thing and study their approach:

```
# Example: Reading ModernFlyouts' GSMTC implementation
https://github.com/ModernFlyouts-Community/ModernFlyouts/blob/main/ModernFlyouts.Core/Media/Control/GSMTCMediaSession.cs
```

Look for:
- Do they have anti-glitch logic?
- Do they use LastUpdatedTime?
- Do they poll or only use events?
- How do they handle seek?

### 4.3 Read the OS/browser source code

For Chromium GSMTC behavior, the authoritative source is:
```
chromium/src/chrome/browser/ui/views/frame/system_media_controls_win.cc
```

This reveals EXACTLY what Chromium sends to the OS during seek/pause/play — no guessing needed.

---

## Phase 5: DESIGN THE FIX — Architecture Before Code

### 5.1 Write down the fix strategy BEFORE coding

Document:
1. **What is the root cause?** (one sentence)
2. **What is the fix approach?** (conceptual, not code)
3. **What are the defense layers?** (never rely on a single check)
4. **What edge cases must be handled?**
5. **What existing behavior must be preserved?**

### 5.2 Design with defense-in-depth

Never rely on a single check. Layer multiple independent guards:

```
Layer 1: Freshness Gate (LastUpdatedTime comparison)
   → Catches: stale/out-of-order OS updates
   
Layer 2: Anti-Glitch (position threshold check)  
   → Catches: transient 0:00 resets even if LastUpdatedTime unavailable

Layer 3: Seek Recovery Window (aggressive re-polling)
   → Catches: delayed convergence after any large position jump
```

If Layer 1 fails (player doesn't report LastUpdatedTime), Layer 2 catches it.
If Layer 2 fails (glitch is to a non-zero position), Layer 3 catches it.

### 5.3 Consider performance impact

- Don't add high-frequency polling permanently — use time-windowed recovery
- Don't hold locks longer than necessary
- Don't dispatch to UI thread when hidden (`_isHubVisible` check)
- Cancel previous async operations before starting new ones

---

## Phase 6: IMPLEMENT — Write the Fix

### 6.1 Make surgical changes

- Change ONLY what needs to change
- Don't refactor unrelated code in the same commit
- Preserve all existing comments and documentation
- Add NEW comments explaining the WHY of your fix, not the WHAT

### 6.2 Name things clearly

```csharp
// BAD
private int _counter = 0;
private long _ts = 0;

// GOOD  
private DateTimeOffset _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
private long _seekRecoveryUntil = 0;
```

### 6.3 Handle ALL edge cases

For every check you add, ask:
- What if this value is null/default/zero?
- What if this is called from a different thread?
- What if the session object is disposed mid-call?
- What if two events fire simultaneously?

### 6.4 Reset state cleanly on track changes

When the track changes (new song/video), ALL sync state must be reset:
```csharp
_lastTimelinePosition = TimeSpan.Zero;
_lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
_suppressExternalPositionUpdatesUntil = 0;
_seekRecoveryUntil = 0;
```

Forgetting to reset even ONE field causes ghost state from the previous track to corrupt the new one.

---

## Phase 7: VERIFY — Prove It Works

### 7.1 Build first, test second

```bash
dotnet build --no-restore -c Release
```

Fix ALL warnings and errors before testing.

### 7.2 Test the exact reproduction steps

Go back to the user's original report and test EXACTLY what they described:
1. Play a YouTube video
2. Let it play for 10+ seconds
3. Click YouTube's seekbar to jump to a different position
4. Watch MetroHub's seekbar — does it track correctly?
5. Pause on YouTube, then resume — does MetroHub sync?

### 7.3 Test edge cases

- Seek to the beginning (0:00)
- Seek to near the end
- Rapid successive seeks (click-click-click)
- Seek while paused
- Switch between YouTube tabs
- Switch from YouTube to Spotify to VLC
- Close the browser tab while playing

### 7.4 Test that you didn't break anything else

- Does play/pause still toggle correctly?
- Does the seekbar still animate smoothly during normal playback?
- Does scrubbing (dragging the seekbar) still work?
- Does track change still show the new track info?
- Does the glow effect still update?

---

## Phase 8: DOCUMENT — Update Standards and Rules

### 8.1 Update the relevant standards doc

If your fix establishes a new pattern or protocol, document it in `.agents/rules/WIDGET_LIFECYCLE_STANDARDS.md` or the relevant standards file so future code follows the same approach.

### 8.2 Add inline code comments

Explain the WHY, not the WHAT:

```csharp
// BAD comment
// Check if position is less than 1.5 seconds
if (incomingPos <= TimeSpan.FromSeconds(1.5))

// GOOD comment  
// Chromium emits transient 0:00 during seek/pause transitions.
// Reject any position <1.5s when we're already at ≥2.5s on a long track.
if (incomingPos <= TimeSpan.FromSeconds(1.5) && 
    _lastTimelinePosition >= TimeSpan.FromSeconds(2.5))
```

---

## Anti-Patterns — What NOT To Do

### ❌ DON'T: Apply surface-level patches
```
"The position resets to 0? Just ignore zeros!"
→ This breaks actual track-start detection and new track transitions.
```

### ❌ DON'T: Add arbitrary delays
```
"Just add a 500ms delay before updating the UI"
→ This makes the seekbar feel laggy and doesn't fix the root cause.
```

### ❌ DON'T: Bypass performance optimizations
```
"Just poll the OS every 50ms forever"
→ This drains battery and wastes CPU. Use time-windowed recovery instead.
```

### ❌ DON'T: Fix symptoms instead of causes
```
"The UI flickers? Just add a debounce!"
→ Find out WHY it flickers. The debounce masks a real data integrity issue.
```

### ❌ DON'T: Skip the research phase
```
"I'll just try different approaches until something works"
→ You'll waste hours and probably introduce new bugs. Read the source code
  of the component causing the issue FIRST.
```

### ❌ DON'T: Assume the OS/browser is correct
```
"The API says position is 0, so it must be 0"
→ APIs can send transient, stale, or out-of-order data. Verify with 
  multiple reads and freshness indicators.
```

---

## Real-World Case Study: GSMTC Media Seekbar Sync

### The Bug
When user seeks on YouTube, MetroHub's seekbar resets to 0:00 briefly, then recovers after 1-2 seconds.

### Phase 1 — Read the Code
Read all 1300+ lines of `MediaWidgetViewModel.cs`, the View code-behind, and the XAML. Mapped the complete data flow from OS events → ViewModel → View.

### Phase 2 — Reproduce
Identified that the issue was 100% reproducible: play YouTube video → seek → seekbar jumps to 0:00 → eventually recovers.

### Phase 3 — Root Cause
- Chromium's SMTC implementation sends transient `Position=0` during seek transitions
- The old code used a `_consecutiveZeroCount` to detect this, but it had three fatal flaws:
  1. Chromium doesn't always send exactly 0 — it sends stale old positions too
  2. The verification poll could receive another stale update, creating a loop
  3. No freshness ordering — couldn't tell if an update was newer or older

### Phase 4 — Research
- Read Chromium's `system_media_controls_win.cc` source to understand exactly what it sends
- Studied ModernFlyouts' `GSMTCMediaSession.cs` for reference patterns
- Searched GitHub, Stack Overflow, and Microsoft docs for GSMTC timeline sync approaches
- Discovered that `LastUpdatedTime` is the canonical freshness signal most projects ignore

### Phase 5 — Design
Three-layer defense:
1. **Freshness Gate**: Compare `LastUpdatedTime` — reject updates older than what we already have
2. **Anti-Glitch**: Position threshold check for transient zeros
3. **Seek Recovery**: Time-windowed aggressive polling after large position jumps

### Phase 6 — Implement
- Replaced `_consecutiveZeroCount` with `_lastAcceptedOsUpdateTime` (freshness tracking)
- Added `_seekRecoveryUntil` for time-windowed recovery
- Made timer tick do full OS sync during recovery window
- Reset all freshness state on track change and user-initiated seek

### Phase 7 — Verify
- Built with 0 warnings, 0 errors
- Published to Desktop folder
- User tests: seek on YouTube, pause/resume, track change, scrubbing

### Phase 8 — Document
- Updated `WIDGET_LIFECYCLE_STANDARDS.md` Section 12 with the new protocol
- Added comprehensive inline comments explaining the freshness gate and recovery logic

---

## Quick Reference Checklist

```
□ Read user's bug report carefully, ask clarifying questions
□ Read the ENTIRE relevant source file(s), not just the "broken" function
□ Map the complete data flow from input to output
□ Add diagnostic logging to trace actual runtime behavior
□ Write diagnostic scripts to isolate OS/API behavior from your code
□ Ask "WHY?" five times to find the true root cause
□ Search GitHub for open-source projects handling the same API/scenario
□ Read the source code of the OS/browser component involved
□ Design a multi-layer defense (never single-point-of-failure)
□ Implement surgically — change only what needs changing
□ Build and verify zero warnings/errors
□ Test the exact reproduction steps from the user's report
□ Test edge cases and regressions
□ Update standards docs and inline comments
□ Publish and inform user
```
