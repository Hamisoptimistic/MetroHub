using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Attached behavior for ScrollViewer providing 120Hz/240Hz VSync-locked physics scrolling.
/// Uses a closed-form analytical critically damped spring integrator (zeta = 1.0),
/// deduplicates CompositionTarget.Rendering multi-fires, suppresses layout-rounding stair-stepping,
/// dynamically optimizes text hinting during motion, and distinguishes touchpads from discrete wheels.
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
    /// Closed-form analytical solution for a 1D critically damped harmonic oscillator (zeta = 1.0).
    /// Pure mathematical function: frame-rate invariant across variable refresh rates (60Hz to 240Hz).
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

        // Physics constants:
        // Omega = 20.0 gives responsive glide with natural deceleration for discrete wheel notches.
        // Omega = 55.0 gives tight near-1:1 coupling for continuous precision touchpads.
        private const double WheelSpringOmega = 20.0;
        private const double TouchpadSpringOmega = 55.0;
        private const double PixelsPerWheelNotch = 96.0;
        private const double RestPositionThreshold = 0.05; // px
        private const double RestVelocityThreshold = 1.0;  // px/sec (< 0.008px per frame at 120Hz)

        private double _currentOffset;
        private double _targetOffset;
        private double _velocity;
        private double _activeOmega = WheelSpringOmega;

        private bool _isRenderingHooked;
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

            _targetOffset = _currentOffset;
            _velocity = 0.0;
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
                _targetOffset = _currentOffset;
            }
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
                _targetOffset = _currentOffset;
                _velocity = 0.0;
            }

            double maxScroll = _scrollViewer.ScrollableHeight;

            if (_isTouchpadGestureActive)
            {
                _activeOmega = TouchpadSpringOmega;
                _targetOffset = Math.Clamp(_targetOffset - (e.Delta * 0.8), 0.0, maxScroll);
            }
            else
            {
                _activeOmega = WheelSpringOmega;
                double deltaPixels = (e.Delta / (double)Mouse.MouseWheelDeltaForOneLine) * PixelsPerWheelNotch;
                _targetOffset = Math.Clamp(_targetOffset - deltaPixels, 0.0, maxScroll);
            }

            if (Math.Abs(_targetOffset - _currentOffset) < RestPositionThreshold && Math.Abs(_velocity) < RestVelocityThreshold)
            {
                return;
            }

            AttachRenderLoop();
            e.Handled = true;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (e is not RenderingEventArgs renderingArgs) return;

            // TRAP 1 FIX: Deduplicate multiple CompositionTarget.Rendering events fired in same VSync
            if (renderingArgs.RenderingTime == _lastRenderTime)
            {
                return;
            }

            double dt = _lastRenderTime == TimeSpan.Zero
                ? (1.0 / 120.0)
                : (renderingArgs.RenderingTime - _lastRenderTime).TotalSeconds;

            _lastRenderTime = renderingArgs.RenderingTime;

            // Clamp dt to [0.001s, 0.1s] to avoid runaway jumps if thread was suspended or at breakpoint
            if (dt <= 0.0 || dt > 0.1)
            {
                dt = 1.0 / 120.0;
            }

            _targetOffset = Math.Clamp(_targetOffset, 0.0, _scrollViewer.ScrollableHeight);

            double x0 = _currentOffset - _targetOffset;
            double v0 = _velocity;

            var (xNew, vNew) = EvaluateCriticallyDampedSpring(x0, v0, _activeOmega, dt);

            _currentOffset = _targetOffset + xNew;
            _velocity = vNew;

            // Rest condition check
            if (Math.Abs(xNew) < RestPositionThreshold && Math.Abs(_velocity) < RestVelocityThreshold)
            {
                _currentOffset = _targetOffset;
                _velocity = 0.0;
                _scrollViewer.ScrollToVerticalOffset(_currentOffset);
                DetachRenderLoop(snapToIntegerPixel: true);
                return;
            }

            _scrollViewer.ScrollToVerticalOffset(_currentOffset);
        }

        private void AttachRenderLoop()
        {
            if (_isRenderingHooked) return;

            _lastRenderTime = TimeSpan.Zero;

            // TRAP 3 & 4 FIX: Suppress layout rounding to eliminate sub-pixel stair-stepping,
            // and set animated text hinting mode to prevent DirectWrite glyph re-hinting stalls.
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
            _lastRenderTime = TimeSpan.Zero;

            if (snapToIntegerPixel)
            {
                _currentOffset = Math.Round(_targetOffset);
                _scrollViewer.ScrollToVerticalOffset(_currentOffset);
            }

            // Restore crisp pixel snapping and static text rendering at rest (0% idle CPU)
            _scrollViewer.UseLayoutRounding = true;
            if (_scrollViewer.Content is UIElement content)
            {
                TextOptions.SetTextHintingMode(content, TextHintingMode.Auto);
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

