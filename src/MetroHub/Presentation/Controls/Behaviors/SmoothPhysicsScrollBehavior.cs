using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Resolution-, DPI-, and refresh-rate-invariant WinUI 3 smooth scroll behavior.
/// Uses an impulse-assisted critically damped spring in device-independent pixels (DIPs)
/// paired with per-monitor physical pixel snapping and GPU sub-pixel interpolation.
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
    /// Default is 15.8 (balanced between 12.5 heavy and 19.5 fast).
    /// </summary>
    public static readonly DependencyProperty SmoothnessProperty =
        DependencyProperty.RegisterAttached(
            "Smoothness",
            typeof(double),
            typeof(SmoothPhysicsScrollBehavior),
            new PropertyMetadata(15.8));

    public static double GetSmoothness(DependencyObject obj) => (double)obj.GetValue(SmoothnessProperty);
    public static void SetSmoothness(DependencyObject obj, double value) => obj.SetValue(SmoothnessProperty, value);

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
            });
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

        while (current != null && current != parent)
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
        private readonly TranslateTransform _subPixelAndOverscrollTransform = new();

        // Impulse-assisted spring constants (all distances in resolution-independent DIPs):
        private const double InitialVelocityKickRatio = 0.35; // Eliminates start-up lag on Frame 1
        private const double BoundarySpringOmega = 14.0;      // Edge bounce-back stiffness (rad/s)
        private const double MaxOverscrollDistance = 15.0;    // Max edge stretch in DIPs
        private const double RestPositionThreshold = 0.03;    // Sub-pixel convergence threshold (DIPs)
        private const double RestVelocityThreshold = 1.2;     // Velocity convergence threshold (DIPs/sec)

        private double _currentOffset;
        private double _targetOffset;
        private double _velocity;
        private double _lastSnappedScrollOffset = double.NaN;

        private bool _isRenderingHooked;
        private bool _isInternalScrollUpdate;
        private TimeSpan _lastRenderArgsTime = TimeSpan.Zero;
        private long _lastFrameTimestamp;
        private long _lastWheelTimestamp;
        private bool _isTouchpadGestureActive;

        public PhysicsScrollController(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;

            if (ScrollViewer.GetCanContentScroll(_scrollViewer))
            {
                ScrollViewer.SetCanContentScroll(_scrollViewer, false);
            }

            _scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            _scrollViewer.MouseWheel += OnMouseWheel;
            _scrollViewer.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            _scrollViewer.ScrollChanged += OnExternalScrollChanged;
            _scrollViewer.Unloaded += OnUnloaded;
        }

        public void CancelMomentumAndFreeze()
        {
            if (!_isRenderingHooked) return;

            _velocity = 0.0;
            _currentOffset = Math.Clamp(_currentOffset, 0.0, _scrollViewer.ScrollableHeight);
            _targetOffset = _currentOffset;
            ApplyViewportOffset(_currentOffset, _scrollViewer.ScrollableHeight);
            DetachRenderLoop(snapToDevicePixel: false);
        }

        public void ResetOffset(double offset)
        {
            DetachRenderLoop(snapToDevicePixel: false);
            _velocity = 0.0;
            double maxScroll = _scrollViewer.ScrollableHeight;
            double clamped = maxScroll > 0 ? Math.Clamp(offset, 0.0, maxScroll) : Math.Max(0.0, offset);
            _currentOffset = clamped;
            _targetOffset = clamped;
            _lastSnappedScrollOffset = clamped;

            _isInternalScrollUpdate = true;
            try
            {
                _scrollViewer.ScrollToVerticalOffset(clamped);
            }
            finally
            {
                _isInternalScrollUpdate = false;
            }

            if (_scrollViewer.Content is UIElement content && content.RenderTransform == _subPixelAndOverscrollTransform)
            {
                _subPixelAndOverscrollTransform.Y = 0.0;
                content.RenderTransform = null;
            }
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isRenderingHooked)
            {
                CancelMomentumAndFreeze();
            }
        }

        private void OnExternalScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isInternalScrollUpdate) return;

            if (!_isRenderingHooked && e.VerticalChange != 0)
            {
                _currentOffset = _scrollViewer.VerticalOffset;
                _targetOffset = _currentOffset;
                _lastSnappedScrollOffset = _currentOffset;
            }
            else if (_isRenderingHooked && e.ExtentHeightChange != 0)
            {
                double maxScroll = _scrollViewer.ScrollableHeight;
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
                _targetOffset = _currentOffset;
                _velocity = 0.0;
                _lastSnappedScrollOffset = _currentOffset;
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
                _targetOffset = Math.Clamp(_currentOffset, 0.0, maxScroll);
                _velocity = 0.0;

                ApplyViewportOffset(_currentOffset, maxScroll);

                if (_currentOffset < 0.0 || _currentOffset > maxScroll)
                {
                    AttachRenderLoop();
                }

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

            double pendingRemaining = _targetOffset - _currentOffset;
            if (Math.Sign(deltaOffset) != 0 && Math.Sign(pendingRemaining) != 0 && Math.Sign(deltaOffset) != Math.Sign(pendingRemaining))
            {
                // Instant direction reversal
                _targetOffset = Math.Clamp(_currentOffset, 0.0, maxScroll);
                _velocity = 0.0;
            }

            double omega = Math.Clamp(GetSmoothness(_scrollViewer), 8.0, 30.0);
            double proposedTarget = _targetOffset + deltaOffset;

            if (proposedTarget < 0.0 || proposedTarget > maxScroll)
            {
                double clampedTarget = Math.Clamp(proposedTarget, 0.0, maxScroll);
                double overflow = proposedTarget - clampedTarget;
                double inBoundsTravel = clampedTarget - _targetOffset;
                _targetOffset = clampedTarget;

                // Apply initial kick for the in-bounds portion
                _velocity += inBoundsTravel * omega * InitialVelocityKickRatio;

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

                double effectiveDelta = newTarget - _targetOffset;
                _targetOffset = newTarget;

                // Immediate velocity kick eliminates the 80ms spring ramp-up delay
                double maxInBoundsVelocity = maxLead * omega;
                _velocity = Math.Clamp(
                    _velocity + (effectiveDelta * omega * InitialVelocityKickRatio),
                    -maxInBoundsVelocity,
                    maxInBoundsVelocity);
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
            if (e is not RenderingEventArgs renderingArgs) return;

            if (renderingArgs.RenderingTime == _lastRenderArgsTime) return;
            _lastRenderArgsTime = renderingArgs.RenderingTime;

            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
            _lastFrameTimestamp = now;

            if (dt <= 0.0001 || dt > 0.05)
            {
                dt = 1.0 / 120.0;
            }

            double maxScroll = _scrollViewer.ScrollableHeight;
            _targetOffset = Math.Clamp(_targetOffset, 0.0, maxScroll);

            if (_isTouchpadGestureActive)
            {
                double msSinceWheel = (now - _lastWheelTimestamp) * 1000.0 / Stopwatch.Frequency;
                if (msSinceWheel < 45.0)
                {
                    return;
                }
                _isTouchpadGestureActive = false;
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

            if (Math.Abs(xNew) < RestPositionThreshold && Math.Abs(_velocity) < RestVelocityThreshold)
            {
                _currentOffset = _targetOffset;
                _velocity = 0.0;
                ApplyViewportOffset(_currentOffset, maxScroll);
                DetachRenderLoop(snapToDevicePixel: true);
                return;
            }

            ApplyViewportOffset(_currentOffset, maxScroll);
        }

        private void ApplyViewportOffset(double offset, double maxScroll)
        {
            double inBounds = Math.Clamp(offset, 0.0, maxScroll);
            double overscroll = offset - inBounds;

            // Per-monitor DPI scale (e.g. 1.0 at 100%, 1.25 at 125%, 1.5 at 150%, 2.0 at 200%)
            double dpiY = VisualTreeHelper.GetDpi(_scrollViewer).DpiScaleY;
            if (dpiY <= 0.0) dpiY = 1.0;

            // Snap ScrollViewer layout offset to physical monitor pixels,
            // and let the GPU TranslateTransform render the sub-pixel fraction.
            double snappedInBounds = Math.Round(inBounds * dpiY) / dpiY;
            double subPixelRemainder = inBounds - snappedInBounds;

            if (double.IsNaN(_lastSnappedScrollOffset) || Math.Abs(snappedInBounds - _lastSnappedScrollOffset) > 1e-4)
            {
                _isInternalScrollUpdate = true;
                try
                {
                    _scrollViewer.ScrollToVerticalOffset(snappedInBounds);
                    _lastSnappedScrollOffset = snappedInBounds;
                }
                finally
                {
                    _isInternalScrollUpdate = false;
                }
            }

            double totalVisualShift = subPixelRemainder + overscroll;

            if (_scrollViewer.Content is UIElement content)
            {
                if (Math.Abs(totalVisualShift) > 0.005)
                {
                    if (content.RenderTransform != _subPixelAndOverscrollTransform)
                    {
                        content.RenderTransform = _subPixelAndOverscrollTransform;
                    }
                    _subPixelAndOverscrollTransform.Y = -totalVisualShift;
                }
                else if (content.RenderTransform == _subPixelAndOverscrollTransform)
                {
                    _subPixelAndOverscrollTransform.Y = 0.0;
                    content.RenderTransform = null;
                }
            }
        }

        private void AttachRenderLoop()
        {
            if (_isRenderingHooked) return;

            _lastRenderArgsTime = TimeSpan.Zero;
            _lastFrameTimestamp = Stopwatch.GetTimestamp();

            CompositionTarget.Rendering += OnRendering;
            _isRenderingHooked = true;
        }

        private void DetachRenderLoop(bool snapToDevicePixel)
        {
            if (!_isRenderingHooked) return;

            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
            _lastRenderArgsTime = TimeSpan.Zero;

            if (_scrollViewer.Content is UIElement content && content.RenderTransform == _subPixelAndOverscrollTransform)
            {
                _subPixelAndOverscrollTransform.Y = 0.0;
                content.RenderTransform = null;
            }

            if (snapToDevicePixel)
            {
                double dpiY = VisualTreeHelper.GetDpi(_scrollViewer).DpiScaleY;
                if (dpiY <= 0.0) dpiY = 1.0;

                double maxScroll = _scrollViewer.ScrollableHeight;
                _currentOffset = Math.Round(Math.Clamp(_currentOffset, 0.0, maxScroll) * dpiY) / dpiY;
                _targetOffset = _currentOffset;

                _isInternalScrollUpdate = true;
                try
                {
                    _scrollViewer.ScrollToVerticalOffset(_currentOffset);
                    _lastSnappedScrollOffset = _currentOffset;
                }
                finally
                {
                    _isInternalScrollUpdate = false;
                }
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            DetachRenderLoop(snapToDevicePixel: false);
            _scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            _scrollViewer.MouseWheel -= OnMouseWheel;
            _scrollViewer.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            _scrollViewer.ScrollChanged -= OnExternalScrollChanged;
            _scrollViewer.Unloaded -= OnUnloaded;
        }
    }
}