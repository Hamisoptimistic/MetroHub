using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Attached behavior for ScrollViewer providing true WinUI 3 kinetic friction scrolling.
/// Features aerodynamic kinetic friction deceleration, cadence-based flick acceleration,
/// elastic boundary rubber-band bounce, sub-pixel layout rounding suppression,
/// and automated precision trackpad detection.
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
            ApplyToScrollViewer(sv, true);
        }
        else
        {
            parent.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var deferredSv = AutoHideScrollBehavior.FindChild<ScrollViewer>(parent);
                if (deferredSv != null)
                {
                    ApplyToScrollViewer(deferredSv, true);
                }
            });
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

    /// <summary>
    /// Immediately halts any in-progress smooth scroll momentum on the given ScrollViewer.
    /// Freezes the viewport at the current visual pixel without snapping.
    /// </summary>
    public static void CancelMomentum(ScrollViewer sv)
    {
        if (sv.GetValue(ControllerProperty) is PhysicsScrollController controller)
        {
            controller.CancelMomentumAndFreeze();
        }
    }

    /// <summary>
    /// Frame-rate invariant exponential friction decay (WinUI 3 kinetic model).
    /// </summary>
    public static double EvaluateKineticFriction(double velocity, double frictionBase, double dt)
    {
        if (dt <= 0.0) return velocity;
        return velocity * Math.Pow(frictionBase, dt * 60.0);
    }

    /// <summary>
    /// Closed-form analytical solution for a 1D critically damped harmonic oscillator (zeta = 1.0).
    /// Used for the elastic boundary overscroll rubber-band return.
    /// </summary>
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

        // WinUI 3 Kinetic Tuning Constants:
        private const double InBoundsFriction = 0.88;      // Aerodynamic kinetic friction decay per 16.6ms
        private const double BoundaryOmega = 10.0;        // Soft damped spring frequency for edge rubber-band (rad/s)
        private const double MaxOverscrollDistance = 16.0; // Subtle boundary stretch clamp (px)
        private const double RestVelocityThreshold = 2.0;  // px/sec (< 0.017px per frame at 120Hz)

        private double _currentOffset;
        private double _velocity;

        private bool _isRenderingHooked;
        private bool _wasOverscrolled;
        private TimeSpan _lastRenderTime = TimeSpan.Zero;
        private long _lastWheelTimestamp;
        private bool _isTouchpadGestureActive;

        public PhysicsScrollController(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;

            _scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            _scrollViewer.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            _scrollViewer.ScrollChanged += OnExternalScrollChanged;
            _scrollViewer.Unloaded += OnUnloaded;
        }

        public void CancelMomentumAndFreeze()
        {
            if (!_isRenderingHooked) return;

            _velocity = 0.0;
            _wasOverscrolled = false;
            _currentOffset = Math.Clamp(_currentOffset, 0.0, _scrollViewer.ScrollableHeight);
            DetachRenderLoop(snapToIntegerPixel: false);
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
            if (!_isRenderingHooked && e.VerticalChange != 0)
            {
                _currentOffset = _scrollViewer.VerticalOffset;
            }
        }

        private double CalculateBaseNotchImpulse()
        {
            double lines = Math.Max(1.0, SystemParameters.WheelScrollLines);
            double viewport = _scrollViewer.ViewportHeight;
            if (viewport <= 100.0)
            {
                viewport = _scrollViewer.ActualHeight > 100.0 ? _scrollViewer.ActualHeight : 800.0;
            }

            // Target travel distance per standard 3-line notch: ~10% of viewport,
            // clamped between 60 DIPs (1 small tile) and 130 DIPs (1 medium tile + gap).
            double targetDistance = Math.Clamp(viewport * 0.10, 60.0, 130.0) * (lines / 3.0);

            // Under kinetic friction base 0.88, decay constant lambda = -60 * ln(0.88) ~= 7.6704 s^-1
            // Total travel distance D = impulse / lambda => impulse = D * lambda
            const double decayRate = 7.6704;
            return targetDistance * decayRate;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || _scrollViewer.ScrollableHeight <= 0) return;

            long now = Stopwatch.GetTimestamp();
            double elapsedMs = _lastWheelTimestamp == 0
                ? 1000.0
                : (now - _lastWheelTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastWheelTimestamp = now;

            // Precision Touchpad detection:
            // Touchpads send fractional deltas (not multiples of 120) or continuous rapid events (< 20ms apart).
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
                _velocity = 0.0;
            }

            if (_isTouchpadGestureActive)
            {
                // Touchpad: 1:1 direct tracking with boundary resistance
                double step = e.Delta * 0.85;
                if ((_currentOffset <= 0.0 && step > 0.0) || (_currentOffset >= _scrollViewer.ScrollableHeight && step < 0.0))
                {
                    double currentStretch = _currentOffset <= 0.0
                        ? Math.Abs(_currentOffset)
                        : Math.Abs(_currentOffset - _scrollViewer.ScrollableHeight);
                    double resistance = Math.Clamp(1.0 - (currentStretch / MaxOverscrollDistance), 0.1, 0.5);
                    step *= resistance;
                }
                _currentOffset -= step;
                _velocity = 0.0;
            }
            else
            {
                // Mouse Wheel: Viewport-Adaptive Kinetic Impulse with Tamed Flick Acceleration
                double cadenceMultiplier = 1.0;
                if (elapsedMs < 50.0)
                {
                    cadenceMultiplier = Math.Min(1.35, 1.0 + (((50.0 - elapsedMs) / 50.0) * 0.35));
                }

                double direction = -Math.Sign(e.Delta);
                double notchCount = Math.Abs(e.Delta) / (double)Mouse.MouseWheelDeltaForOneLine;
                double baseImpulse = CalculateBaseNotchImpulse();
                double impulse = direction * baseImpulse * notchCount * cadenceMultiplier;

                // Dynamic max velocity scaled to viewport to avoid runaway scrolling on any display
                double viewport = _scrollViewer.ViewportHeight > 100.0 ? _scrollViewer.ViewportHeight : 800.0;
                double maxVelocity = Math.Max(1800.0, viewport * 2.5);

                // High boundary resistance when pushing further into an already overscrolled edge
                bool scrollingIntoTopWall = _currentOffset <= 0.0 && direction < 0.0;
                bool scrollingIntoBottomWall = _currentOffset >= _scrollViewer.ScrollableHeight && direction > 0.0;
                if (scrollingIntoTopWall || scrollingIntoBottomWall)
                {
                    double currentStretch = scrollingIntoTopWall
                        ? Math.Abs(_currentOffset)
                        : Math.Abs(_currentOffset - _scrollViewer.ScrollableHeight);
                    double resistance = Math.Clamp(1.0 - (currentStretch / MaxOverscrollDistance), 0.05, 0.35);
                    impulse *= resistance;
                }

                // Accumulate velocity with dynamic speed cap
                _velocity = Math.Clamp(_velocity + impulse, -maxVelocity, maxVelocity);
            }

            AttachRenderLoop();
            e.Handled = true;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (e is not RenderingEventArgs renderingArgs) return;

            // Deduplicate multiple CompositionTarget.Rendering events fired in the same VSync
            if (renderingArgs.RenderingTime == _lastRenderTime)
            {
                return;
            }

            double dt = _lastRenderTime == TimeSpan.Zero
                ? (1.0 / 120.0)
                : (renderingArgs.RenderingTime - _lastRenderTime).TotalSeconds;

            _lastRenderTime = renderingArgs.RenderingTime;

            // Clamp dt to [0.001s, 0.1s] to avoid runaway jumps if thread was suspended
            if (dt <= 0.0 || dt > 0.1)
            {
                dt = 1.0 / 120.0;
            }

            double maxScroll = _scrollViewer.ScrollableHeight;

            // 1. Check whether in-bounds or overscrolled
            bool isOverscrolledTop = _currentOffset < 0.0;
            bool isOverscrolledBottom = _currentOffset > maxScroll;
            bool isOverscrolled = isOverscrolledTop || isOverscrolledBottom;

            if (isOverscrolled)
            {
                // Soft foam bumper: absorb 70% of kinetic energy on initial boundary contact
                if (!_wasOverscrolled)
                {
                    _velocity *= 0.30;
                }
                _wasOverscrolled = true;

                // 2. Over-bounds: Apply critically damped spring pulling back to boundary
                double boundary = isOverscrolledTop ? 0.0 : maxScroll;
                double x0 = _currentOffset - boundary;

                var (xNew, vNew) = EvaluateCriticallyDampedSpring(x0, _velocity, BoundaryOmega, dt);
                _currentOffset = boundary + xNew;
                _velocity = vNew;

                // Extra boundary damping to prevent oscillation
                _velocity *= Math.Pow(0.80, dt * 60.0);

                // Soft clamp max overscroll stretch
                if (Math.Abs(xNew) > MaxOverscrollDistance)
                {
                    _currentOffset = boundary + (Math.Sign(xNew) * MaxOverscrollDistance);
                    _velocity = 0.0;
                }

                // Rest condition at boundary
                if (Math.Abs(xNew) < 0.05 && Math.Abs(_velocity) < RestVelocityThreshold)
                {
                    _currentOffset = boundary;
                    _velocity = 0.0;
                    _wasOverscrolled = false;
                    ApplyViewportOffset(_currentOffset, maxScroll);
                    DetachRenderLoop(snapToIntegerPixel: true);
                    return;
                }
            }
            else
            {
                _wasOverscrolled = false;

                // 3. In-bounds: Apply aerodynamic kinetic friction decay
                _velocity = EvaluateKineticFriction(_velocity, InBoundsFriction, dt);
                _currentOffset += _velocity * dt;

                // Rest condition in-bounds
                if (Math.Abs(_velocity) < RestVelocityThreshold)
                {
                    _velocity = 0.0;
                    ApplyViewportOffset(_currentOffset, maxScroll);
                    DetachRenderLoop(snapToIntegerPixel: true);
                    return;
                }
            }

            ApplyViewportOffset(_currentOffset, maxScroll);
        }

        private void ApplyViewportOffset(double offset, double maxScroll)
        {
            double inBounds = Math.Clamp(offset, 0.0, maxScroll);
            double overscroll = offset - inBounds;

            _scrollViewer.ScrollToVerticalOffset(inBounds);

            // Elastic rubber-band visual displacement on overscroll
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
                    content.RenderTransform = null;
                }
            }
        }

        private void AttachRenderLoop()
        {
            if (_isRenderingHooked) return;

            _lastRenderTime = TimeSpan.Zero;

            // Suppress layout rounding to eliminate sub-pixel stair-stepping,
            // and set animated text hinting mode to prevent DirectWrite glyph re-hint stalls.
            _scrollViewer.UseLayoutRounding = false;
            if (_scrollViewer.Content is UIElement content)
            {
                TextOptions.SetTextHintingMode(content, TextHintingMode.Animated);
            }

            CompositionTarget.Rendering += OnRendering;
            _isRenderingHooked = true;
        }

        private void DetachRenderLoop(bool snapToIntegerPixel)
        {
            if (!_isRenderingHooked) return;

            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
            _wasOverscrolled = false;
            _lastRenderTime = TimeSpan.Zero;

            if (_scrollViewer.Content is UIElement content && content.RenderTransform == _overscrollTransform)
            {
                content.RenderTransform = null;
            }

            if (snapToIntegerPixel)
            {
                _currentOffset = Math.Round(Math.Clamp(_currentOffset, 0.0, _scrollViewer.ScrollableHeight));
                _scrollViewer.ScrollToVerticalOffset(_currentOffset);
            }

            // Restore crisp pixel snapping and static text rendering at rest (0% idle CPU)
            _scrollViewer.UseLayoutRounding = true;
            if (_scrollViewer.Content is UIElement c)
            {
                TextOptions.SetTextHintingMode(c, TextHintingMode.Auto);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            DetachRenderLoop(snapToIntegerPixel: false);
            _scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            _scrollViewer.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            _scrollViewer.ScrollChanged -= OnExternalScrollChanged;
            _scrollViewer.Unloaded -= OnUnloaded;
        }
    }
}
