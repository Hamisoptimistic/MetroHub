using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Resolution-, DPI-, and refresh-rate-invariant WinUI 3 smooth scroll behavior.
/// Uses an impulse-assisted critically damped spring in device-independent pixels (DIPs)
/// paired with per-monitor DPI scaling and GPU sub-pixel interpolation.
/// </summary>
public static class SmoothPhysicsScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothPhysicsScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// Distance in DIPs (1/96 inch) per standard 3-line mouse wheel notch.
    /// Default is 76.0 DIPs (independent of window size, resolution, or DPI scale).
    /// </summary>
    public static readonly DependencyProperty StepSizeProperty =
        DependencyProperty.RegisterAttached(
            "StepSize",
            typeof(double),
            typeof(SmoothPhysicsScrollBehavior),
            new PropertyMetadata(76.0));

    public static double GetStepSize(DependencyObject obj) => (double)obj.GetValue(StepSizeProperty);
    public static void SetStepSize(DependencyObject obj, double value) => obj.SetValue(StepSizeProperty, value);

    /// <summary>
    /// Critically damped spring natural frequency (omega in rad/s).
    /// Default is 19.0 (balanced natural acceleration without artificial kick).
    /// </summary>
    public static readonly DependencyProperty SmoothnessProperty =
        DependencyProperty.RegisterAttached(
            "Smoothness",
            typeof(double),
            typeof(SmoothPhysicsScrollBehavior),
            new PropertyMetadata(19.0));

    public static double GetSmoothness(DependencyObject obj) => (double)obj.GetValue(SmoothnessProperty);
    public static void SetSmoothness(DependencyObject obj, double value) => obj.SetValue(SmoothnessProperty, value);

    /// <summary>
    /// Global flag indicating whether smooth scrolling motion is currently active.
    /// Used by child controls (e.g. AtmosphericAuraControl) to pause expensive animations.
    /// </summary>
    public static bool IsScrolling { get; private set; }

    private static readonly DependencyProperty ControllerProperty =
        DependencyProperty.RegisterAttached(
            "Controller",
            typeof(PhysicsScrollController),
            typeof(SmoothPhysicsScrollBehavior),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        bool enable = (bool)e.NewValue;

        if (d is ScrollViewer sv)
        {
            ApplyToScrollViewer(sv, enable);
        }
        else if (d is FrameworkElement fe)
        {
            if (enable)
            {
                if (fe.IsLoaded)
                {
                    AttachToChild(fe);
                }
                else
                {
                    RoutedEventHandler? loaded = null;
                    loaded = (s, args) =>
                    {
                        fe.Loaded -= loaded;
                        AttachToChild(fe);
                    };
                    fe.Loaded += loaded;
                }
            }
            else
            {
                var childSv = AutoHideScrollBehavior.FindChild<ScrollViewer>(fe);
                if (childSv != null)
                {
                    ApplyToScrollViewer(childSv, false);
                }
            }
        }
    }

    private static void AttachToChild(FrameworkElement parent)
    {
        parent.ApplyTemplate();
        var sv = AutoHideScrollBehavior.FindChild<ScrollViewer>(parent);
        if (sv != null)
        {
            InheritTuningProperties(parent, sv);
            ApplyToScrollViewer(sv, true);
        }
        else
        {
            parent.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var deferredSv = AutoHideScrollBehavior.FindChild<ScrollViewer>(parent);
                if (deferredSv != null)
                {
                    InheritTuningProperties(parent, deferredSv);
                    ApplyToScrollViewer(deferredSv, true);
                }
            }).FireAndForget("SmoothPhysicsScrollBehavior.Attach");
        }
    }

    private static void InheritTuningProperties(DependencyObject parent, ScrollViewer childSv)
    {
        if (parent.ReadLocalValue(StepSizeProperty) != DependencyProperty.UnsetValue)
        {
            SetStepSize(childSv, GetStepSize(parent));
        }
        if (parent.ReadLocalValue(SmoothnessProperty) != DependencyProperty.UnsetValue)
        {
            SetSmoothness(childSv, GetSmoothness(parent));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "PhysicsScrollController lifetime is managed by ScrollViewer via ControllerProperty.")]
    private static void ApplyToScrollViewer(ScrollViewer sv, bool isEnabled)
    {
        if (isEnabled)
        {
            if (sv.GetValue(ControllerProperty) is null)
            {
                var controller = new PhysicsScrollController(sv);
                sv.SetValue(ControllerProperty, controller);
            }
        }
        else
        {
            if (sv.GetValue(ControllerProperty) is PhysicsScrollController controller)
            {
                controller.Dispose();
                sv.ClearValue(ControllerProperty);
            }
        }
    }

    public static void CancelMomentum(ScrollViewer sv)
    {
        if (sv.GetValue(ControllerProperty) is PhysicsScrollController controller)
        {
            controller.CancelMomentumAndFreeze();
        }
    }

    public static void ResetOffset(ScrollViewer sv, double offset)
    {
        if (sv.GetValue(ControllerProperty) is PhysicsScrollController controller)
        {
            controller.ResetOffset(offset);
        }
        else
        {
            sv.ScrollToVerticalOffset(offset);
        }
    }

    internal static bool IsOverNestedScrollContainer(DependencyObject? source, ScrollViewer parent)
    {
        DependencyObject? current = source;

        while (current != null && !ReferenceEquals(current, parent))
        {
            // 1. ScrollViewer (Radio, Network, and standard lists)
            if (current is ScrollViewer nestedSv && nestedSv.Visibility == Visibility.Visible)
            {
                if (nestedSv.ScrollableHeight > 0.5 || nestedSv.ScrollableWidth > 0.5)
                {
                    return true;
                }
            }
            // 2. TextBoxBase (Notepad RichTextBox and multiline text editors)
            else if (current is TextBoxBase tb && tb.Visibility == Visibility.Visible)
            {
                if (tb.ExtentHeight > tb.ViewportHeight + 0.5 ||
                    tb.ExtentWidth > tb.ViewportWidth + 0.5 ||
                    tb.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                {
                    return true;
                }
            }

            DependencyObject? next = null;
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                next = VisualTreeHelper.GetParent(current);
            }

            if (next == null)
            {
                if (current is FrameworkContentElement fce)
                {
                    next = fce.Parent;
                }
                else if (current is FrameworkElement fe)
                {
                    next = fe.Parent ?? LogicalTreeHelper.GetParent(current);
                }
                else
                {
                    next = LogicalTreeHelper.GetParent(current);
                }
            }

            current = next;
        }

        return false;
    }

    public static (double xNew, double vNew) EvaluateCriticallyDampedSpring(double x0, double v0, double omega, double dt)
    {
        if (dt <= 0.0) return (x0, v0);
        double exp = Math.Exp(-omega * dt);
        double temp = (v0 + omega * x0) * dt;
        double xNew = (x0 + temp) * exp;
        double vNew = (v0 - omega * temp) * exp;
        return (xNew, vNew);
    }

    private sealed class PhysicsScrollController : IDisposable
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly TranslateTransform _overscrollTransform = new();

        // Impulse-assisted spring constants (all distances in resolution-independent DIPs):
        private const double InitialVelocityKickRatio = 0.0;  // Zero artificial kick for smooth natural acceleration
        private const double BoundarySpringOmega = 14.0;      // Edge bounce-back stiffness (rad/s)
        private const double MaxOverscrollDistance = 15.0;    // Max edge stretch in DIPs
        private const double RestPositionThreshold = 0.75;    // Cut off asymptotic crawl (<1 physical px)
        private const double RestVelocityThreshold = 12.0;    // Velocity cutoff (<0.08 DIPs/frame at 144Hz)

        private double _currentOffset;
        private double _rawTargetOffset;
        private double _targetOffset;
        private double _velocity;

        private bool _isRenderingHooked;
        private bool _isInternalScrollUpdate;
        private bool _isSurfaceCacheHeld;
        private TimeSpan _lastRenderArgsTime = TimeSpan.Zero;
        private long _lastWheelTimestamp;
        private bool _isTouchpadGestureActive;
        private System.Windows.Threading.DispatcherTimer? _cacheSettleTimer;

        // TEMPORARY scroll frame-pacing probe. Remove once scroll jank is diagnosed.
        private const int PacingCapacity = 4096;
        private readonly double[] _pacingFrameMs = new double[PacingCapacity];
        private int _pacingCount;
        private double _pacingWorstFrameMs;
        private double _lastTickWallMs;
        private double _pacingWorstDispatchMs;
        private int _pacingUiStalls;
        private double _pacingHandlerSumMs;
        private double _pacingHandlerWorstMs;

        public PhysicsScrollController(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;

            if (ScrollViewer.GetCanContentScroll(_scrollViewer))
            {
                ScrollViewer.SetCanContentScroll(_scrollViewer, false);
            }

            _currentOffset = _scrollViewer.VerticalOffset;
            _rawTargetOffset = _currentOffset;
            _targetOffset = _currentOffset;

            _scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            _scrollViewer.MouseWheel += OnMouseWheel;
            _scrollViewer.PreviewMouseDown += OnPreviewMouseDown;
            _scrollViewer.ScrollChanged += OnExternalScrollChanged;
            _scrollViewer.Unloaded += OnUnloaded;

            // Scrollbar thumb dragging bypasses the spring entirely (native WPF pixel scrolling).
            // Keep the surface cache hot for the duration of the drag so it re-composites one
            // texture instead of re-rasterizing every tile on each pointer move.
            _scrollViewer.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted), true);
            _scrollViewer.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted), true);
            _scrollViewer.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture), true);

            // TEMPORARY: proves the diagnostic sink is live even before the first scroll burst.
            ScrollTrace.Write(FormattableString.Invariant($"[ScrollProbe] attached extent={_scrollViewer.ExtentHeight:F0} viewport={_scrollViewer.ViewportHeight:F0} scrollable={_scrollViewer.ScrollableHeight:F0} trace={ScrollTrace.FilePath}"));
        }

        public void CancelMomentumAndFreeze()
        {
            _cacheSettleTimer?.Stop();
            if (!_isRenderingHooked) return;

            _velocity = 0.0;
            double maxScroll = _scrollViewer.ScrollableHeight;
            _currentOffset = Math.Clamp(_currentOffset, 0.0, maxScroll);
            _rawTargetOffset = _currentOffset;
            _targetOffset = _currentOffset;
            ApplyViewportOffset(_currentOffset, maxScroll);
            DetachRenderLoop();
        }

        public void ResetOffset(double offset)
        {
            _cacheSettleTimer?.Stop();
            DetachRenderLoop();
            _velocity = 0.0;
            double maxScroll = _scrollViewer.ScrollableHeight;
            double clamped = maxScroll > 0 ? Math.Clamp(offset, 0.0, maxScroll) : Math.Max(0.0, offset);
            _currentOffset = clamped;
            _rawTargetOffset = clamped;
            _targetOffset = clamped;

            _isInternalScrollUpdate = true;
            try
            {
                _scrollViewer.ScrollToVerticalOffset(clamped);
            }
            finally
            {
                _isInternalScrollUpdate = false;
            }

            if (_scrollViewer.Content is UIElement content)
            {
                content.CacheMode = null;
                if (content.RenderTransform == _overscrollTransform)
                {
                    _overscrollTransform.Y = 0.0;
                    content.RenderTransform = null;
                }
            }
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isRenderingHooked)
            {
                CancelMomentumAndFreeze();
            }
        }

        private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
        {
            if (_isRenderingHooked) return;
            if (!IsOwnScrollBarThumb(e.OriginalSource)) return;

            AttachSurfaceCache();
        }

        private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_isRenderingHooked) return;
            if (!IsOwnScrollBarThumb(e.OriginalSource)) return;

            ReleaseSurfaceCache();
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isRenderingHooked || !_isSurfaceCacheHeld) return;

            ReleaseSurfaceCache();
        }

        /// <summary>
        /// True when the routed <see cref="Thumb"/> belongs to this ScrollViewer's own ScrollBar
        /// template, not to a Slider or nested ScrollBar living inside the canvas content.
        /// </summary>
        private bool IsOwnScrollBarThumb(object? source)
        {
            if (source is not DependencyObject current) return false;

            DependencyObject? content = _scrollViewer.Content as DependencyObject;
            bool sawThumb = false;

            while (current != null && !ReferenceEquals(current, _scrollViewer))
            {
                if (ReferenceEquals(current, content)) return false;

                if (current is Thumb)
                {
                    sawThumb = true;
                }
                else if (current is ScrollBar)
                {
                    return sawThumb;
                }

                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : (current is FrameworkElement fe
                        ? (fe.Parent ?? LogicalTreeHelper.GetParent(current))
                        : LogicalTreeHelper.GetParent(current));
            }

            return false;
        }

        private void OnExternalScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isInternalScrollUpdate) return;

            if (!_isRenderingHooked && e.VerticalChange != 0)
            {
                _currentOffset = _scrollViewer.VerticalOffset;
                _rawTargetOffset = _currentOffset;
                _targetOffset = _currentOffset;
            }
            else if (_isRenderingHooked && e.ExtentHeightChange != 0)
            {
                double maxScroll = _scrollViewer.ScrollableHeight;
                _rawTargetOffset = Math.Clamp(_rawTargetOffset, 0.0, maxScroll);
                _targetOffset = Math.Clamp(_targetOffset, 0.0, maxScroll);
            }
        }

        private double CalculateNotchStepInDips()
        {
            // Fixed physical DIPs per wheel notch (1 DIP = 1/96 inch).
            // Automatically scales with OS DPI (100%-250%) and user's Windows WheelScrollLines setting,
            // and never changes when the window or monitor resolution changes.
            int sysLines = SystemParameters.WheelScrollLines;
            double lineFactor = sysLines > 0 ? (sysLines / 3.0) : 1.0;
            double configuredStep = Math.Max(16.0, GetStepSize(_scrollViewer));
            return configuredStep * lineFactor;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double maxScroll = _scrollViewer.ScrollableHeight;
            if (e.Handled || maxScroll <= 0) return;

            // Do not intercept if Ctrl is pressed (e.g. zoom in Notepad / Photos)
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) return;

            // Do not intercept if cursor is over a nested scroll container (e.g. Radio, Notepad, Network)
            if (IsOverNestedScrollContainer(e.OriginalSource as DependencyObject, _scrollViewer))
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsedMs = _lastWheelTimestamp == 0
                ? 1000.0
                : (now - _lastWheelTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastWheelTimestamp = now;

            // Precision Touchpad sends non-120 multiples
            bool isSubDetent = (Math.Abs(e.Delta) % Mouse.MouseWheelDeltaForOneLine) != 0;
            if (isSubDetent)
            {
                _isTouchpadGestureActive = true;
            }
            else if (elapsedMs > 80.0)
            {
                _isTouchpadGestureActive = false;
            }

            if (!_isRenderingHooked)
            {
                _currentOffset = _scrollViewer.VerticalOffset;
                _rawTargetOffset = _currentOffset;
                _targetOffset = _currentOffset;
                _velocity = 0.0;
            }

            if (_isTouchpadGestureActive)
            {
                // 1:1 direct DIP tracking for Precision Touchpad
                double step = -e.Delta * 0.80;
                bool pushingTop = _currentOffset <= 0.0 && step < 0.0;
                bool pushingBottom = _currentOffset >= maxScroll && step > 0.0;

                if (pushingTop || pushingBottom)
                {
                    double stretch = pushingTop ? Math.Abs(_currentOffset) : Math.Abs(_currentOffset - maxScroll);
                    double resistance = Math.Clamp(1.0 - (stretch / MaxOverscrollDistance), 0.05, 0.45);
                    step *= resistance;
                }

                _currentOffset = Math.Clamp(_currentOffset + step, -MaxOverscrollDistance, maxScroll + MaxOverscrollDistance);
                _rawTargetOffset = Math.Clamp(_currentOffset, 0.0, maxScroll);
                _targetOffset = _rawTargetOffset;
                _velocity = 0.0;

                ApplyViewportOffset(_currentOffset, maxScroll);

                AttachRenderLoop();

                e.Handled = true;
                return;
            }

            // Mouse Wheel: Cadence boost up to 1.30x on fast flicks (< 50ms between notches)
            double cadenceMultiplier = 1.0;
            if (elapsedMs < 50.0)
            {
                cadenceMultiplier = Math.Min(1.30, 1.0 + (((50.0 - elapsedMs) / 50.0) * 0.30));
            }

            double direction = -Math.Sign(e.Delta);
            double notchCount = Math.Abs(e.Delta) / (double)Mouse.MouseWheelDeltaForOneLine;
            double deltaOffset = direction * CalculateNotchStepInDips() * notchCount * cadenceMultiplier;

            double pendingRemaining = _rawTargetOffset - _currentOffset;
            if (Math.Sign(deltaOffset) != 0 && Math.Sign(pendingRemaining) != 0 && Math.Sign(deltaOffset) != Math.Sign(pendingRemaining))
            {
                // Instant direction reversal
                _rawTargetOffset = Math.Clamp(_currentOffset, 0.0, maxScroll);
                _targetOffset = _rawTargetOffset;
                _velocity = 0.0;
            }

            double proposedTarget = _rawTargetOffset + deltaOffset;

            if (proposedTarget < 0.0 || proposedTarget > maxScroll)
            {
                double clampedTarget = Math.Clamp(proposedTarget, 0.0, maxScroll);
                double overflow = proposedTarget - clampedTarget;
                _rawTargetOffset = clampedTarget;

                // Convert boundary overflow into an elastic overscroll kick
                double currentStretch = _currentOffset < 0.0
                    ? Math.Abs(_currentOffset)
                    : (_currentOffset > maxScroll ? Math.Abs(_currentOffset - maxScroll) : 0.0);

                double resistance = Math.Clamp(1.0 - (currentStretch / MaxOverscrollDistance), 0.05, 0.38);
                double desiredStretchIncrement = Math.Clamp(overflow * 0.10 * resistance, -5.0, 5.0);
                double velocityKick = desiredStretchIncrement * BoundarySpringOmega * Math.E;

                double maxBoundaryVelocity = MaxOverscrollDistance * BoundarySpringOmega * Math.E;
                _velocity = Math.Clamp(_velocity + velocityKick, -maxBoundaryVelocity, maxBoundaryVelocity);
            }
            else
            {
                // Cap maximum pending lead at 8x step size (~600 DIPs) regardless of viewport height
                double maxLead = CalculateNotchStepInDips() * 8.0;
                double newTarget = Math.Clamp(proposedTarget, _currentOffset - maxLead, _currentOffset + maxLead);
                newTarget = Math.Clamp(newTarget, 0.0, maxScroll);

                _rawTargetOffset = newTarget;
            }

            AttachRenderLoop();
            e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // If the mouse is over any scrollable widget, swallow the bubbled event
            // so the canvas never scrolls even when the inner scrollbar reaches the bottom or top.
            if (IsOverNestedScrollContainer(e.OriginalSource as DependencyObject, _scrollViewer))
            {
                e.Handled = true;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            // TEMPORARY scroll frame-pacing probe. Remove once scroll jank is diagnosed.
            long handlerStart = Stopwatch.GetTimestamp();
            try
            {
                RenderTick(e);
            }
            finally
            {
                RecordHandlerCost(handlerStart);
            }
        }

        private void RenderTick(EventArgs e)
        {
            if (e is not RenderingEventArgs renderingArgs) return;

            if (renderingArgs.RenderingTime == _lastRenderArgsTime) return;

            double dt;
            bool hasPreviousFrame = _lastRenderArgsTime != TimeSpan.Zero;
            if (!hasPreviousFrame)
            {
                dt = 1.0 / 120.0;
            }
            else
            {
                dt = (renderingArgs.RenderingTime - _lastRenderArgsTime).TotalSeconds;
            }
            _lastRenderArgsTime = renderingArgs.RenderingTime;

            // TEMPORARY scroll frame-pacing probe. Remove once scroll jank is diagnosed.
            RecordPacingSample(hasPreviousFrame, dt);

            dt = Math.Clamp(dt, 0.001, 0.05);

            double maxScroll = _scrollViewer.ScrollableHeight;
            _rawTargetOffset = Math.Clamp(_rawTargetOffset, 0.0, maxScroll);

            // Stage 1: Smoothly chase raw wheel target to absorb 8ms-16ms USB polling jitter
            if (!_isTouchpadGestureActive)
            {
                double filterFactor = 1.0 - Math.Exp(-35.0 * dt);
                _targetOffset += (_rawTargetOffset - _targetOffset) * filterFactor;
                if (Math.Abs(_rawTargetOffset - _targetOffset) < 0.05)
                {
                    _targetOffset = _rawTargetOffset;
                }
            }
            else
            {
                _targetOffset = _rawTargetOffset;
            }

            bool isOverscrolled = _currentOffset < -0.01 || _currentOffset > maxScroll + 0.01;
            double wheelOmega = Math.Clamp(GetSmoothness(_scrollViewer), 8.0, 30.0);
            double omega = isOverscrolled ? BoundarySpringOmega : wheelOmega;

            double displacement = _currentOffset - _targetOffset;
            var (xNew, vNew) = EvaluateCriticallyDampedSpring(displacement, _velocity, omega, dt);

            _currentOffset = _targetOffset + xNew;
            _velocity = vNew;

            if (_currentOffset < -MaxOverscrollDistance)
            {
                _currentOffset = -MaxOverscrollDistance;
                if (_velocity < 0.0) _velocity = 0.0;
            }
            else if (_currentOffset > maxScroll + MaxOverscrollDistance)
            {
                _currentOffset = maxScroll + MaxOverscrollDistance;
                if (_velocity > 0.0) _velocity = 0.0;
            }

            if (Math.Abs(_rawTargetOffset - _currentOffset) < RestPositionThreshold && Math.Abs(_velocity) < RestVelocityThreshold)
            {
                _currentOffset = _rawTargetOffset;
                _targetOffset = _rawTargetOffset;
                _velocity = 0.0;
                ApplyViewportOffset(_currentOffset, maxScroll);
                DetachRenderLoop();
                return;
            }

            ApplyViewportOffset(_currentOffset, maxScroll);
        }

        private void ApplyViewportOffset(double offset, double maxScroll)
        {
            double inBounds = Math.Clamp(offset, 0.0, maxScroll);
            double overscroll = offset - inBounds;

            _isInternalScrollUpdate = true;
            try
            {
                _scrollViewer.ScrollToVerticalOffset(inBounds);
            }
            finally
            {
                _isInternalScrollUpdate = false;
            }

            if (_scrollViewer.Content is UIElement content)
            {
                if (Math.Abs(overscroll) > 0.05)
                {
                    if (content.RenderTransform != _overscrollTransform)
                    {
                        content.RenderTransform = _overscrollTransform;
                    }
                    _overscrollTransform.Y = -overscroll;
                }
                else if (content.RenderTransform == _overscrollTransform)
                {
                    _overscrollTransform.Y = 0.0;
                    content.RenderTransform = null;
                }
            }
        }

        private void AttachRenderLoop()
        {
            AttachSurfaceCache();

            if (_isRenderingHooked) return;

            IsScrolling = true;
            TileControl.SuppressRevealForScrolling();

            _lastRenderArgsTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
            _isRenderingHooked = true;
        }

        /// <summary>
        /// Holds a single-quad surface cache over the scroll content. Independent of the physics
        /// render loop so scrollbar thumb drags can reuse it without the spring fighting the thumb.
        /// </summary>
        private void AttachSurfaceCache()
        {
            _cacheSettleTimer?.Stop();

            if (_isSurfaceCacheHeld) return;

            if (_scrollViewer.Content is UIElement content)
            {
                content.IsHitTestVisible = false;
                if (content.CacheMode == null)
                {
                    content.CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = false, EnableClearType = false };
                }
                _isSurfaceCacheHeld = true;
            }
        }

        private void ReleaseSurfaceCache()
        {
            if (!_isSurfaceCacheHeld) return;

            if (_scrollViewer.Content is UIElement content)
            {
                content.IsHitTestVisible = true;
                if (content.RenderTransform == _overscrollTransform)
                {
                    _overscrollTransform.Y = 0.0;
                    content.RenderTransform = null;
                }
            }
            else
            {
                _isSurfaceCacheHeld = false;
                return;
            }

            // Debounce BitmapCache teardown by 500ms (industry standard) to keep cache hot across trailing wheel detents
            if (_cacheSettleTimer == null)
            {
                _cacheSettleTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _cacheSettleTimer.Tick += OnCacheSettleTimerTick;
            }
            _cacheSettleTimer.Stop();
            _cacheSettleTimer.Start();
        }

        private void DetachRenderLoop()
        {
            if (!_isRenderingHooked) return;

            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
            _lastRenderArgsTime = TimeSpan.Zero;
            IsScrolling = false;

            FlushPacingProbe();
            ReleaseSurfaceCache();
        }

        // ---------------------------------------------------------------------------------
        // TEMPORARY scroll frame-pacing probe. Remove once scroll jank is diagnosed.
        // Logs one line per scroll burst:
        //   frame  = CompositionTarget.Rendering RenderingTime delta (render thread pace)
        //   dispatch = wall-clock delta between our callbacks (UI thread pace)
        // If frame p95 is high but dispatch is clean -> render thread (too much to draw).
        // If dispatch is much higher than frame   -> UI thread is blocked between frames.
        // ---------------------------------------------------------------------------------
        private void RecordPacingSample(bool hasPreviousFrame, double dtSeconds)
        {
            if (!hasPreviousFrame) return;

            if (_pacingCount < _pacingFrameMs.Length)
            {
                double ms = dtSeconds * 1000.0;
                _pacingFrameMs[_pacingCount++] = ms;
                if (ms > _pacingWorstFrameMs) _pacingWorstFrameMs = ms;
            }

            double wallMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (_lastTickWallMs > 0.0)
            {
                double dispatchMs = wallMs - _lastTickWallMs;
                if (dispatchMs > _pacingWorstDispatchMs) _pacingWorstDispatchMs = dispatchMs;
                if (dispatchMs > (dtSeconds * 1000.0) + 8.0) _pacingUiStalls++;
            }
            _lastTickWallMs = wallMs;
        }

        private void FlushPacingProbe()
        {
            int count = _pacingCount;
            _pacingCount = 0;
            _lastTickWallMs = 0.0;
            double worstFrame = _pacingWorstFrameMs;
            _pacingWorstFrameMs = 0.0;
            double worstDispatch = _pacingWorstDispatchMs;
            _pacingWorstDispatchMs = 0.0;
            int uiStalls = _pacingUiStalls;
            _pacingUiStalls = 0;
            double handlerSum = _pacingHandlerSumMs;
            _pacingHandlerSumMs = 0.0;
            double handlerWorst = _pacingHandlerWorstMs;
            _pacingHandlerWorstMs = 0.0;

            if (count < 8) return;

            var samples = _pacingFrameMs.AsSpan(0, count);
            samples.Sort();
            double p50 = samples[count / 2];
            double p95 = samples[Math.Min(count - 1, (int)(count * 0.95))];
            double p99 = samples[Math.Min(count - 1, (int)(count * 0.99))];

            int over17 = 0, over25 = 0, over50 = 0;
            for (int i = 0; i < count; i++)
            {
                double s = samples[i];
                if (s > 16.7) over17++;
                if (s > 25.0) over25++;
                if (s > 50.0) over50++;
            }

            int tier = RenderCapability.Tier >> 16;
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(_scrollViewer).DpiScaleX; }
            catch (Exception ex) { Safe.Ignore(ex, "SmoothPhysicsScrollBehavior.PacingDpi"); }

            bool cached = _scrollViewer.Content is UIElement content && content.CacheMode != null;

            string message = FormattableString.Invariant($"[ScrollPacing] frames={count} p50={p50:F1}ms p95={p95:F1}ms p99={p99:F1}ms worst={worstFrame:F1}ms over17={over17} over25={over25} over50={over50} worstDispatch={worstDispatch:F1}ms uiStalls={uiStalls} handlerAvg={(count > 0 ? handlerSum / count : 0.0):F2}ms handlerMax={handlerWorst:F2}ms tier={tier} dpi={dpi:F2} viewport={_scrollViewer.ViewportHeight:F0} extent={_scrollViewer.ExtentHeight:F0} scrollable={_scrollViewer.ScrollableHeight:F0} cached={cached} wpCached={MetroHub.Presentation.Controllers.BackdropManager.WallpaperLayerCached}");

            ScrollTrace.Write(message);
            Serilog.Log.Information("{PacingLine}", message);
        }

        // TEMPORARY scroll frame-pacing probe. Remove once scroll jank is diagnosed.
        private void RecordHandlerCost(long startTimestamp)
        {
            double ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            _pacingHandlerSumMs += ms;
            if (ms > _pacingHandlerWorstMs) _pacingHandlerWorstMs = ms;
        }

        private void OnCacheSettleTimerTick(object? sender, EventArgs e)
        {
            _cacheSettleTimer?.Stop();
            if (!_isRenderingHooked && _scrollViewer.Content is UIElement content)
            {
                content.CacheMode = null;
            }
            if (!_isRenderingHooked)
            {
                _isSurfaceCacheHeld = false;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            DetachRenderLoop();
            _cacheSettleTimer?.Stop();
            _cacheSettleTimer = null;

            _scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            _scrollViewer.MouseWheel -= OnMouseWheel;
            _scrollViewer.PreviewMouseDown -= OnPreviewMouseDown;
            _scrollViewer.ScrollChanged -= OnExternalScrollChanged;
            _scrollViewer.Unloaded -= OnUnloaded;
            _scrollViewer.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted));
            _scrollViewer.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted));
            _scrollViewer.RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnLostMouseCapture));

            _isSurfaceCacheHeld = false;

            IsScrolling = false;
            if (_scrollViewer.Content is UIElement content)
            {
                content.CacheMode = null;
                content.IsHitTestVisible = true;
                if (content.RenderTransform == _overscrollTransform)
                {
                    _overscrollTransform.Y = 0.0;
                    content.RenderTransform = null;
                }
            }
        }
    }
}