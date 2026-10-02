using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace MetroHub.Core.Services;

/// <summary>
/// Diagnostic snapshot containing high-precision rendering, layout, and scroll performance metrics.
/// </summary>
public sealed class ScrollDiagnosticsSnapshot
{
    public double CurrentFps { get; init; }
    public double AverageFps { get; init; }
    public double MinFps { get; init; }
    public double MaxFrameTimeMs { get; init; }
    public int RenderTier { get; init; }
    public string RenderTierDescription { get; init; } = string.Empty;
    public int TotalHitches { get; init; }
    public long TotalFrames { get; init; }
    public double LayoutPassesPerSec { get; init; }
    public double LayoutPassesPerFrame { get; init; }
    public double DispatcherLatencyMs { get; init; }
    public string ActiveScrollTarget { get; init; } = string.Empty;
    public double ActiveScrollTravelPx { get; init; }
    public bool IsScrolling { get; init; }
    public string LastCompletedSummary { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

/// <summary>
/// Real-time diagnostics engine attached to MainWindow and AllAppsDrawer to measure and pinpoint
/// frame rate drops, rendering tier fallbacks, layout thrashing, and scroll stuttering.
/// Emits structured telemetry to Serilog, a dedicated scroll_diagnostics.log file, and the live HUD overlay.
/// </summary>
public static class ScrollDiagnosticsLogger
{
    private static readonly ILogger Logger = Serilog.Log.ForContext("Source", "ScrollDiagnostics");
    private static readonly object FileLock = new();
    private static readonly string LogFilePath = AppPaths.ScrollDiagnosticsLogPath;

    public static bool IsEnabled { get; set; } = true;
    public static event Action<ScrollDiagnosticsSnapshot>? SnapshotUpdated;

    // ── Render Telemetry ───────────────────────────────────────────────────────
    private static bool _isHooked;
    private static TimeSpan _lastRenderTime = TimeSpan.Zero;
    private static readonly Queue<double> _recentFrameIntervalsMs = new(120);
    private static long _totalFramesRendered;
    private static int _totalHitchesCount;
    private static double _maxFrameTimeSeenMs;

    // ── Layout Telemetry ───────────────────────────────────────────────────────
    private static long _totalLayoutPasses;
    private static long _layoutPassesSinceLastTick;
    private static readonly List<WeakReference<FrameworkElement>> _monitoredElements = new();

    // ── Scroll Session Telemetry ───────────────────────────────────────────────
    private sealed class ScrollSession
    {
        public string TargetName = string.Empty;
        public double StartOffset;
        public double CurrentOffset;
        public double LastOffset;
        public long StartTimestamp;
        public long LastActivityTimestamp;
        public int FrameCount;
        public int HitchCount;
        public double MinFps = 999.0;
        public double MaxFrameTimeMs;
        public long LayoutPasses;
        public int Gen0Start;
        public int Gen1Start;
        public int Gen2Start;
    }

    private static ScrollSession? _activeSession;
    private static readonly DispatcherTimer _scrollSettleTimer;
    private static string _lastCompletedSummary = "Ready - Scroll canvas or drawer to record diagnostics.";

    // ── Dispatcher Heartbeat Latency ───────────────────────────────────────────
    private static readonly DispatcherTimer _heartbeatTimer;
    private static double _lastDispatcherLatencyMs;

    // ── Hardware Render Tier ───────────────────────────────────────────────────
    private static int _cachedRenderTier = -1;
    private static string _renderTierDescription = "Unknown";

    static ScrollDiagnosticsLogger()
    {
        try
        {
            AppPaths.EnsureDirectory(LogFilePath);
        }
        catch { }

        EvaluateRenderTier();
        RenderCapability.TierChanged += (_, _) => EvaluateRenderTier();

        // Timer to detect when a scroll motion has settled (no movement for 160ms)
        _scrollSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _scrollSettleTimer.Tick += OnScrollSettleTick;

        // Periodic heartbeat to test UI thread dispatcher latency and emit HUD snapshot
        _heartbeatTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _heartbeatTimer.Tick += OnHeartbeatTick;
        _heartbeatTimer.Start();

        WriteEntry($"=== SCROLL DIAGNOSTICS LOGGER INITIALIZED | Render Tier: {_cachedRenderTier} ({_renderTierDescription}) ===");
    }

    // ── Attachment & Monitoring ────────────────────────────────────────────────
    public static void AttachMainWindow(Window window, ScrollViewer? contentScrollViewer)
    {
        if (window == null) return;

        MonitorElementLayout(window, "MainWindow");

        if (contentScrollViewer != null)
        {
            MonitorScrollViewer(contentScrollViewer, "MainWindow (ContentScrollViewer)");
        }

        HookRenderingLoop();
        WriteEntry($"[ATTACH] Monitored MainWindow and ContentScrollViewer. Render Tier: {_cachedRenderTier}");
    }

    public static void AttachAllAppsDrawer(FrameworkElement drawerControl, ScrollViewer? groupedScrollViewer, ScrollViewer? searchResultsScrollViewer)
    {
        if (drawerControl == null) return;

        MonitorElementLayout(drawerControl, "AllAppsDrawer");

        if (groupedScrollViewer != null)
        {
            MonitorScrollViewer(groupedScrollViewer, "AllAppsDrawer (GroupedScrollViewer)");
        }

        if (searchResultsScrollViewer != null)
        {
            MonitorScrollViewer(searchResultsScrollViewer, "AllAppsDrawer (SearchResultsScrollViewer)");
        }

        HookRenderingLoop();
        WriteEntry($"[ATTACH] Monitored AllAppsDrawer and child ScrollViewers.");
    }

    public static void MonitorScrollViewer(ScrollViewer sv, string name)
    {
        if (sv == null) return;

        sv.ScrollChanged -= OnScrollViewerScrollChanged;
        sv.ScrollChanged += OnScrollViewerScrollChanged;
        sv.Tag = name;
    }

    private static bool _isLayoutHooked;

    public static void MonitorElementLayout(FrameworkElement fe, string name)
    {
        if (fe == null) return;

        if (!_isLayoutHooked)
        {
            fe.LayoutUpdated += OnElementLayoutUpdated;
            _isLayoutHooked = true;
            _monitoredElements.Add(new WeakReference<FrameworkElement>(fe));
        }
    }

    private static void HookRenderingLoop()
    {
        if (_isHooked) return;
        _isHooked = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    // ── Telemetry Event Handlers ───────────────────────────────────────────────
    private static void OnElementLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        _totalLayoutPasses++;
        _layoutPassesSinceLastTick++;

        if (_activeSession != null)
        {
            _activeSession.LayoutPasses++;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

        var args = (RenderingEventArgs)e;
        if (args.RenderingTime == _lastRenderTime) return;

        double dtMs = _lastRenderTime == TimeSpan.Zero ? 16.67 : (args.RenderingTime - _lastRenderTime).TotalMilliseconds;
        _lastRenderTime = args.RenderingTime;

        // Ignore large jumps caused by window minimization or modal dialogs
        if (dtMs <= 0 || dtMs > 500) return;

        _totalFramesRendered++;
        if (dtMs > _maxFrameTimeSeenMs) _maxFrameTimeSeenMs = dtMs;

        lock (_recentFrameIntervalsMs)
        {
            _recentFrameIntervalsMs.Enqueue(dtMs);
            while (_recentFrameIntervalsMs.Count > 60)
            {
                _recentFrameIntervalsMs.Dequeue();
            }
        }

        double fps = 1000.0 / dtMs;
        bool isHitch = dtMs > 25.0; // Less than 40 FPS

        if (isHitch)
        {
            _totalHitchesCount++;
        }

        if (_activeSession != null)
        {
            _activeSession.FrameCount++;
            if (fps < _activeSession.MinFps) _activeSession.MinFps = fps;
            if (dtMs > _activeSession.MaxFrameTimeMs) _activeSession.MaxFrameTimeMs = dtMs;

            if (isHitch)
            {
                _activeSession.HitchCount++;
                string severity = dtMs > 66.0 ? "CRITICAL FREEZE" : (dtMs > 40.0 ? "SEVERE HITCH" : "MINOR HITCH");
                WriteEntry($"[HITCH] {severity} in {_activeSession.TargetName}: dt={dtMs:0.0}ms ({fps:0.0} FPS) | Offset={_activeSession.CurrentOffset:0} | LayoutCountInSession={_activeSession.LayoutPasses} | UI Delay={_lastDispatcherLatencyMs:0.0}ms");
            }
        }
    }

    private static void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsEnabled) return;
        if (Math.Abs(e.VerticalChange) < 0.1 && Math.Abs(e.HorizontalChange) < 0.1) return;

        if (sender is not ScrollViewer sv) return;
        string name = sv.Tag as string ?? sv.Name ?? "ScrollViewer";

        long now = Stopwatch.GetTimestamp();

        if (_activeSession == null || _activeSession.TargetName != name)
        {
            // Close previous session if target changed
            if (_activeSession != null)
            {
                FinishScrollSession();
            }

            // Start new scroll session
            _activeSession = new ScrollSession
            {
                TargetName = name,
                StartOffset = sv.VerticalOffset,
                CurrentOffset = sv.VerticalOffset,
                LastOffset = sv.VerticalOffset,
                StartTimestamp = now,
                LastActivityTimestamp = now,
                Gen0Start = GC.CollectionCount(0),
                Gen1Start = GC.CollectionCount(1),
                Gen2Start = GC.CollectionCount(2)
            };
        }
        else
        {
            _activeSession.LastOffset = _activeSession.CurrentOffset;
            _activeSession.CurrentOffset = sv.VerticalOffset;
            _activeSession.LastActivityTimestamp = now;
        }

        // Restart settle timer
        _scrollSettleTimer.Stop();
        _scrollSettleTimer.Start();
    }

    private static void OnScrollSettleTick(object? sender, EventArgs e)
    {
        _scrollSettleTimer.Stop();
        FinishScrollSession();
    }

    private static void FinishScrollSession()
    {
        if (_activeSession == null) return;

        var s = _activeSession;
        _activeSession = null;

        double durationMs = (Stopwatch.GetTimestamp() - s.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
        double travelPx = Math.Abs(s.CurrentOffset - s.StartOffset);

        if (s.FrameCount == 0 || durationMs < 30) return;

        double avgFps = (s.FrameCount / durationMs) * 1000.0;
        double minFps = s.MinFps > 200 ? avgFps : s.MinFps;
        double layoutsPerFrame = s.FrameCount > 0 ? (double)s.LayoutPasses / s.FrameCount : 0.0;

        int gen0 = GC.CollectionCount(0) - s.Gen0Start;
        int gen1 = GC.CollectionCount(1) - s.Gen1Start;
        int gen2 = GC.CollectionCount(2) - s.Gen2Start;
        double wsMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);

        string summary = $"[SCROLL SUMMARY] '{s.TargetName}' | Travel: {travelPx:0}px in {durationMs:0}ms | Avg: {avgFps:0.0} FPS | Min: {minFps:0.0} FPS (Worst Hitch: {s.MaxFrameTimeMs:0.0}ms) | Hitches (>25ms): {s.HitchCount} | Frames: {s.FrameCount} | Layouts: {s.LayoutPasses} ({layoutsPerFrame:0.0}/f) | UI Delay: {_lastDispatcherLatencyMs:0.0}ms | Tier: {_cachedRenderTier} | Mem: {wsMb:0.0}MB (GC {gen0}/{gen1}/{gen2})";

        _lastCompletedSummary = summary;
        WriteEntry(summary);
    }

    private static void OnHeartbeatTick(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

        // Measure UI thread queue latency
        long queueTime = Stopwatch.GetTimestamp();
        Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            double delayMs = (Stopwatch.GetTimestamp() - queueTime) * 1000.0 / Stopwatch.Frequency;
            _lastDispatcherLatencyMs = delayMs;
        }));

        // Calculate rolling stats
        double avgFps = 0.0;
        double minFps = 0.0;
        double maxDt = 0.0;

        lock (_recentFrameIntervalsMs)
        {
            if (_recentFrameIntervalsMs.Count > 0)
            {
                double sum = 0.0;
                double max = 0.0;
                foreach (var dt in _recentFrameIntervalsMs)
                {
                    sum += dt;
                    if (dt > max) max = dt;
                }
                double avgDt = sum / _recentFrameIntervalsMs.Count;
                avgFps = avgDt > 0 ? 1000.0 / avgDt : 0.0;
                maxDt = max;
                minFps = max > 0 ? 1000.0 / max : 0.0;
            }
        }

        double layoutsPerSec = _layoutPassesSinceLastTick / 0.15;
        _layoutPassesSinceLastTick = 0;

        double layoutsPerFrame = avgFps > 0 ? (layoutsPerSec / avgFps) : 0;

        var snapshot = new ScrollDiagnosticsSnapshot
        {
            CurrentFps = _recentFrameIntervalsMs.Count > 0 ? (1000.0 / _recentFrameIntervalsMs.Peek()) : avgFps,
            AverageFps = avgFps,
            MinFps = minFps,
            MaxFrameTimeMs = maxDt,
            RenderTier = _cachedRenderTier,
            RenderTierDescription = _renderTierDescription,
            TotalHitches = _totalHitchesCount,
            TotalFrames = _totalFramesRendered,
            LayoutPassesPerSec = layoutsPerSec,
            LayoutPassesPerFrame = layoutsPerFrame,
            DispatcherLatencyMs = _lastDispatcherLatencyMs,
            ActiveScrollTarget = _activeSession?.TargetName ?? string.Empty,
            ActiveScrollTravelPx = _activeSession != null ? Math.Abs(_activeSession.CurrentOffset - _activeSession.StartOffset) : 0,
            IsScrolling = _activeSession != null,
            LastCompletedSummary = _lastCompletedSummary
        };

        SnapshotUpdated?.Invoke(snapshot);
    }

    private static void EvaluateRenderTier()
    {
        _cachedRenderTier = RenderCapability.Tier >> 16;
        _renderTierDescription = _cachedRenderTier switch
        {
            2 => "Tier 2: Full GPU Direct3D hardware acceleration",
            1 => "Tier 1: Partial GPU acceleration (Legacy)",
            0 => "Tier 0: SOFTWARE RENDERING ONLY (CPU-bound - severe stutter expected)",
            _ => $"Tier {_cachedRenderTier}"
        };
    }

    private static void WriteEntry(string line)
    {
        try
        {
            Logger.Information("{Message}", line);
            Debug.WriteLine($"[ScrollDiagnostics] {line}");

            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
            lock (FileLock)
            {
                File.AppendAllText(LogFilePath, entry);
            }
        }
        catch { }
    }
}
