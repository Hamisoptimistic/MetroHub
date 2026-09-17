# MetroHub Performance Standards

Every new widget, feature, service, or control must comply with these rules. These exist because we've already paid for these lessons in production.

## Why This Exists

We've already fixed these bugs once. Don't reintroduce them.

| Bug | Cause | Cost |
|---|---|---|
| 4–8% idle CPU | DispatcherTimer at 1 Hz for idle work | Removed, rewrote timers |
| 2 GB disk bloat | string.GetHashCode() cache filenames | Randomized per process, duplicate files |
| 22,914 UI events / 30s | UIA tree walks on every layout | WM_GETOBJECT intercept |
| Unbounded RAM growth | Unbounded Dictionary caches | LRU + capacity caps |
| GDI handle churn | Unfrozen Freezables | Freeze everywhere |
| Working set thrashing | EmptyWorkingSet() on minimize | Deleted |
| Memory leaks | Lambda event subscriptions | Named handlers + Dispose |

## 1 - Timers

Do not use DispatcherTimer for anything that doesn't need UI-thread affinity. DispatcherTimer fires on the UI thread. Every tick is a UI thread wake, even if the tick body does nothing.

Allowed uses of DispatcherTimer:
- Animation sequencing where the tick must run on the UI thread.
- One-shot deferrals (Interval set once, then Stop() inside the tick).

Banned uses:
- Polling hardware (network, battery, media session).
- Periodic measurement (RAM HUD, throughput).
- Watchdogs, retry loops, heartbeats.

Use instead:

    private System.Threading.Timer? _timer;

    private void StartTimer()
    {
        _timer = new System.Threading.Timer(_ =>
        {
            var value = PollHardware();
            if (value == _lastValue) return;
            _lastValue = value;
            Application.Current?.Dispatcher.InvokeAsync(() => UpdateUi(value));
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

Cadence guidance:
- Hardware polling: 5-15 seconds minimum.
- Network reachability: 15-30 seconds, or event-driven via NetworkChange.
- Clock display: once per minute if showing HH:mm. Once per second only if showing seconds.
- Throughput: 1-2 seconds, and only marshal on change.

Every timer must be stoppable. Expose Pause() and Resume() and call them from HideScreen() and ShowScreen().

## 2 - Event Subscriptions

Never subscribe to a long-lived publisher with an anonymous lambda. Lambdas capture the subscriber and prevent it from being collected.

    // BAD - leaks the ViewModel when the model outlives it
    model.PropertyChanged += (s, e) => UpdateUi(e.PropertyName);

    // GOOD - named handler, unsubscribed in Dispose
    model.PropertyChanged += OnModelPropertyChanged;

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateUi(e.PropertyName);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
        }
        base.Dispose(disposing);
    }

Publishers that require unsubscription: INotifyPropertyChanged, static services (AudioService.Instance, ThroughputService.Instance), NetworkChange.NetworkAddressChanged, NetworkChange.NetworkAvailabilityChanged, Application.Current.*, any singleton with an event.

Verify before merge: grep the new file for += and -=. Every += should have a matching -= in Dispose or Pause.

## 3 - Freezable Hygiene

Brushes, Geometries, Transforms, and Pens are Freezable. If a Freezable is not frozen and is assigned to a UI element, WPF registers a change-notification callback that keeps the element alive and forces cross-thread synchronization.

Rule: Any Freezable that outlives the method that created it must be Freeze()d before use.

    var brush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
    brush.Freeze();
    element.Background = brush;

Cache frozen instances:

    // BAD - allocates 60 times per second during animation
    element.Background = new SolidColorBrush(groupColor);

    // GOOD - cache by color, freeze on creation
    private static readonly Dictionary<Color, Brush> _brushCache = new();

    private static Brush GetFrozenBrush(Color c)
    {
        if (_brushCache.TryGetValue(c, out var b)) return b;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        _brushCache[c] = brush;
        return brush;
    }

Never Freeze() a Freezable that will be mutated. Clone first, then freeze.

## 4 - Caches Must Be Bounded

Every cache - in-memory or on disk - must have a hard upper bound. Unbounded caches are how desktop apps slowly consume a gigabyte over a session.

Approved patterns:
- Fixed-capacity Dictionary with LRU eviction.
- MemoryCache from Microsoft.Extensions.Caching.Memory with SizeLimit.
- Preallocated ring buffer for time-series data.

Banned:
- Raw Dictionary that grows from user actions or external events.
- ConcurrentDictionary with no eviction.
- Static collections populated from discovery operations.

Every cache must log its size at debug level on insert and evict.

Disk caches:
- Filenames must be deterministic. Use SHA-256, not GetHashCode().
- Include a version prefix (v5_, not v4_) so old formats can be swept on upgrade.

    // BAD - randomized per process, produces duplicate files every launch
    string hash = Math.Abs(keyPath.ToLowerInvariant().GetHashCode()).ToString();

    // GOOD - deterministic across processes, reboots, machines
    string hash = ComputeDeterministicHash(keyPath);  // SHA-256, first 16 hex chars

## 5 - BitmapSource Decoding

Never load a BitmapSource at native resolution and then scale it with ScaleTransform. Decode at the size you'll actually display.

    // BAD - decodes at 256x256, then displays at 24x24
    image.Source = new BitmapImage(new Uri(path));

    // GOOD - decodes at 24x24 directly
    var bmp = new BitmapImage();
    bmp.BeginInit();
    bmp.UriSource = new Uri(path);
    bmp.DecodePixelWidth = 24;
    bmp.CacheOption = BitmapCacheOption.OnLoad;
    bmp.EndInit();
    bmp.Freeze();
    image.Source = bmp;

Decode sizes:
- All Apps drawer icons: 24-32px
- Desktop tile icons: match the display size x DPI scale
- Wallpapers: Math.Min(ActualWidth, 2560) - never ActualWidth * 1.5
- Thumbnails: the actual thumbnail size

Every BitmapSource must be Freeze()d before being shared across threads or cached.

## 6 - Threading

The UI thread is for rendering and input. Everything else goes to a threadpool.

- Filesystem enumeration: Task.Run
- Win32 API calls that block: Task.Run
- HTTP, DNS, socket ops: await with ConfigureAwait(false)
- Decoding images: Task.Run (frozen source)
- Shell enumeration (SHGetFileInfo, IShellItemImageFactory): Task.Run

Marshal only the final result to the UI thread:

    // BAD - entire body on UI thread
    private async void Button_Click(object sender, RoutedEventArgs e)
    {
        var apps = ScanStartMenu();
        var icons = LoadIcons(apps);
        List.ItemsSource = icons;
    }

    // GOOD - only the final assignment on UI thread
    private async void Button_Click(object sender, RoutedEventArgs e)
    {
        var apps = await Task.Run(() => ScanStartMenu());
        var icons = await Task.Run(() => LoadIcons(apps));
        List.ItemsSource = icons;
    }

Never await inside a lock. Use SemaphoreSlim instead. Never block on Task.Result or Task.Wait().

## 7 - PropertyChanged Discipline

Raise PropertyChanged only when the value actually changed.

    // BAD
    public string TimeString
    {
        get => _timeString;
        set { _timeString = value; OnPropertyChanged(); }
    }

    // GOOD
    public string TimeString
    {
        get => _timeString;
        set
        {
            if (_timeString == value) return;
            _timeString = value;
            OnPropertyChanged();
        }
    }

For timer-driven updates, diff the formatted string:

    var formatted = FormatSpeed(metrics.DownloadBytesPerSec);
    if (formatted == _lastFormattedDownload) return;
    _lastFormattedDownload = formatted;
    DownloadSpeedText = formatted;

## 8 - No Forced Memory Management

Banned:
- GC.Collect() - any overload, any generation, any mode.
- EmptyWorkingSet() - pages working set to disk and forces soft faults on restore.
- SetProcessWorkingSetSize with small values.
- Any Win32 call that trims process memory.

Allowed:
- GC.GetTotalMemory(forceFullCollection: false) for diagnostics, off the UI thread.
- GC.CollectionCount(n) for diagnostics.
- .csproj flags: ServerGarbageCollection=false, GarbageCollectionAdaptationMode=1, TieredPGO=true.

If you think you need to force a collection, you have a leak. Find it with dotnet-gcdump.

## 9 - Diff Before Dispatch

Any code path that posts to the UI thread must first check whether the value actually changed.

    // BAD - marshals every tick, even when nothing changed
    Application.Current.Dispatcher.InvokeAsync(() => Throughput = metrics);

    // GOOD - diff on the caller thread, marshal only on change
    var prev = Throughput;
    if (prev == null ||
        Math.Abs(metrics.DownloadBytesPerSec - prev.DownloadBytesPerSec) > 0.5 ||
        metrics.DownloadFormatted != prev.DownloadFormatted)
    {
        Application.Current.Dispatcher.InvokeAsync(() => Throughput = metrics);
    }

Applies to timer callbacks (network, media, clock, throughput), event handlers from high-frequency publishers, and any InvokeAsync or Invoke call site.

## 10 - Lifecycle: Pause and Resume

Every widget, service, or control that runs background work must implement Pause() and Resume(). Pause() is called from MainWindow.HideScreen() and when a widget is scrolled off-screen. Resume() is called from MainWindow.ShowScreen() and when a widget comes back into view.

Pause() must:
- Stop all timers.
- Cancel any CancellationTokenSource and null it out.
- Unsubscribe from external events (NetworkChange, singleton INotifyPropertyChanged).
- Release rebuildable caches (decoded bitmaps, collections), but not the disk cache.

Resume() must:
- Re-subscribe to external events.
- Recreate the CancellationTokenSource.
- Restart timers.
- Re-warm caches synchronously if warming takes less than 50ms, or before the window becomes visible if warming takes longer.

If your widget does no background work, Pause() and Resume() can be no-ops, but implement them anyway.

## 11 - Verification Before Merge

Every new feature must pass this checklist before it ships.

Build: dotnet build -c Release produces 0 errors, 0 warnings.

Trace (30 seconds idle after first launch):

    dotnet-trace collect --process-id <PID> --duration 00:00:30 -o trace.nettrace

Targets:
- AutomationPeer.UpdateSubtree calls: 0
- Visual.RenderRecursive calls: less than 100
- Unexpected timers firing: 0

Counters (20 seconds idle):

    dotnet-counters collect --process-id <PID> --format json --duration 00:00:20 -o counters.json

Targets:
- CPU (user + system) avg: less than 0.5%
- CPU peak: less than 2%
- Allocation rate: less than 0.5 MB/s
- Gen0 collections: less than 1 per 5 seconds
- Gen2 collections: 0
- Working set: stable, no upward trend

Lifecycle: minimize and restore 10 times. Working set and GDI handles must be flat. Leave idle for 10 minutes. No growth in any counter.

If any target fails, the feature is not done. Fix the root cause. Do not add GC.Collect.

## 12 - Anti-Patterns Reference

Never use these. If you see them in code review, reject.

- EmptyWorkingSet: pages working set to disk, refaults on restore. Use nothing, let Windows manage it.
- GC.Collect(2, ...): forces full blocking GC on all managed threads. Remove, find the allocation source.
- DispatcherTimer at 1-4 Hz for idle work: UI thread wake every tick. Use System.Threading.Timer.
- model.PropertyChanged += (s, e) => ...: lambda captures subscriber, leaks. Use named handler + unsubscribe in Dispose.
- new SolidColorBrush(...) in a loop: allocation plus no freeze means event subscription. Cache frozen brushes by color.
- new BitmapImage(new Uri(path)): decodes at native resolution. Use BitmapImage with DecodePixelWidth.
- NetworkInterface.GetAllNetworkInterfaces() every second: kernel syscall plus allocation. Cache, invalidate on NetworkChange.
- Unbounded Dictionary: grows forever. Use LRU or fixed capacity.
- string.GetHashCode() for cache filenames: randomized per process, duplicate files. Use SHA-256 deterministic hash.
- Task.Run inside a lock: deadlock risk. Use SemaphoreSlim.WaitAsync.
- .Result or .Wait() on the UI thread: deadlock. Use async void handler or ConfigureAwait(false).
- Raising PropertyChanged for unchanged values: forces binding re-evaluation and layout. Diff before raise.

## 13 - Code Review Checklist

Before approving any PR that adds a widget, feature, service, or control:

- No DispatcherTimer for polling or idle work
- No anonymous lambda event subscriptions to long-lived publishers
- Every Freezable frozen on creation
- Every BitmapSource decoded at display size and frozen
- Every cache bounded, versioned on disk, hashed deterministically
- All UI-thread PropertyChanged raises diffed
- All timers stoppable, gated on Pause and Resume
- No GC.Collect, no EmptyWorkingSet
- dotnet-counters and dotnet-trace targets met (attach results to PR)
- 10-cycle minimize and restore shows flat working set and GDI

## 14 - When Something Is Slow

Follow this order. Do not skip steps. Do not guess.

1. Reproduce with a measurement. dotnet-counters for CPU and allocation, dotnet-trace for call stacks.
2. Identify the top consumer. It will be obvious. It's almost always a timer, an allocation, or a UI-thread marshal.
3. Read the call stack. The method that appears most often in the trace is the problem.
4. Fix the root cause. Don't cache over it, don't add GC.Collect, don't add Task.Delay.
5. Re-measure. The number must move. If it doesn't, the fix didn't work.
6. Only if the fix works, ship it. Otherwise revert and try again.

A fix without a before and after measurement is not a fix. It's a hope.

## Agent Instructions

When implementing any new feature, widget, service, or control for MetroHub, you MUST follow PERFORMANCE_STANDARDS.md. Specifically:

1. No DispatcherTimer for background, polling, or idle work. Use System.Threading.Timer, PeriodicTimer, or event-driven async loops.
2. Every IDisposable must unsubscribe every event it subscribes to. Use named handlers, never lambdas, for anything subscribed to a long-lived publisher.
3. Every Freezable (Brush, Geometry, Transform, Pen) must be Freeze()d immediately after construction.
4. Every cache must be bounded (LRU or fixed-size). No unbounded Dictionary or ConcurrentDictionary growth.
5. Every BitmapSource must be created with DecodePixelWidth or DecodePixelHeight at display resolution, and Freeze()d.
6. No work on the UI thread that can be done on a threadpool. Marshal only the final result.
7. Before posting any PropertyChanged, check if the value actually changed. Don't raise for no-op updates.
8. No GC.Collect calls, ever. No EmptyWorkingSet. No forced memory trimming.
9. Any timer-driven update must diff its output before writing to the UI. If the formatted string is identical, return early.
10. On minimize or hide: pause timers, release rebuildable caches, and stop polling. On show or restore: resume timers, re-warm caches before the window becomes visible if warming is over 50ms.

Before declaring a feature complete, run dotnet-counters for 20 seconds while the feature is idle and confirm CPU under 0.5%, allocation under 0.5 MB/s, and Gen0 under 1 per second. Report the numbers.

## Quick Reference Card

    TIMERS       -> System.Threading.Timer, never DispatcherTimer for background
    EVENTS       -> Named handlers, always unsubscribed
    FREEZABLES   -> Freeze on creation, cache frozen instances
    BITMAPS      -> DecodePixelWidth at display size, Freeze
    CACHES       -> Bounded, LRU, deterministic hashes
    THREADS      -> Task.Run for work, marshal only the result
    PROPERTIES   -> Diff before raise
    MEMORY       -> No GC.Collect, no EmptyWorkingSet, ever
    LIFECYCLE    -> Pause timers on hide, Resume on show
    VERIFY       -> dotnet-counters + dotnet-trace before merge

Last updated: 2026-09-17
Applies to: All code in src/MetroHub/
Enforcement: Build must pass, trace must show targets, review must check the list above.