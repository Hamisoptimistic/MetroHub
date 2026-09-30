using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MetroHub.Presentation.Controls
{
    /// <summary>
    /// High-performance smooth inertia scrolling using analytical exponential velocity decay
    /// synchronized with WPF's CompositionTarget.Rendering pass.
    /// </summary>
    public static class SmoothScrollBehavior
    {
        // ── Physics Tuning ────────────────────────────────────────────────────────
        // Impulse multiplier per mouse-wheel notch. Higher = longer travel per notch.
        private const double VelocityMultiplier = 8.5;

        // Exponential decay coefficient (1/sec). Higher = stops faster.
        private const double Friction = 8.5;

        // Velocity floor (px/sec) to terminate the animation loop.
        private const double StopVelocity = 2.0;

        // Caps maximum speed from rapid free-spinning wheels.
        private const double MaxVelocity = 3500.0;

        // ── Public Attached Property ──────────────────────────────────────────────
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        // ── Per-ScrollViewer State ────────────────────────────────────────────────
        private sealed class ScrollState
        {
            public double Velocity;
            public double Current;
            public UIElement? Content;
            public DispatcherTimer? HoverTimer;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly List<ScrollViewer> _active = new();
        private static bool _hooked;
        private static TimeSpan _lastTime = TimeSpan.Zero;

        // ── Lifecycle & Attachment ────────────────────────────────────────────────
        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;

            if ((bool)e.NewValue)
            {
                sv.PreviewMouseWheel += OnWheel;
                sv.Loaded += OnLoaded;
                sv.Unloaded += OnUnloaded;

                if (sv.IsLoaded)
                {
                    ConfigureScrollViewer(sv);
                }
            }
            else
            {
                sv.PreviewMouseWheel -= OnWheel;
                sv.Loaded -= OnLoaded;
                sv.Unloaded -= OnUnloaded;

                if (_states.TryGetValue(sv, out var s))
                {
                    s.HoverTimer?.Stop();
                    if (s.Content != null)
                    {
                        s.Content.IsHitTestVisible = true;
                    }
                }

                _states.Remove(sv);
                _active.Remove(sv);

                if (_active.Count == 0 && _hooked)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    _hooked = false;
                }
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                ConfigureScrollViewer(sv);
            }
        }

        private static void ConfigureScrollViewer(ScrollViewer sv)
        {
            var s = GetOrCreate(sv);
            s.Content = sv.Content as UIElement;
            s.Current = sv.VerticalOffset;

            // Force pixel-accurate scrolling and pre-virtualization cache on parent ItemsControl
            if (sv.TemplatedParent is ItemsControl itemsControl)
            {
                VirtualizingPanel.SetScrollUnit(itemsControl, ScrollUnit.Pixel);
                VirtualizingPanel.SetCacheLengthUnit(itemsControl, VirtualizationCacheLengthUnit.Page);
                VirtualizingPanel.SetCacheLength(itemsControl, new VirtualizationCacheLength(1.0, 1.0));
            }
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            if (_states.TryGetValue(sv, out var s))
            {
                s.HoverTimer?.Stop();
            }

            _states.Remove(sv);
            _active.Remove(sv);

            if (_active.Count == 0 && _hooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }

        private static ScrollState GetOrCreate(ScrollViewer sv)
        {
            if (!_states.TryGetValue(sv, out var s))
            {
                s = new ScrollState { Current = sv.VerticalOffset };
                _states[sv] = s;
            }
            return s;
        }

        // ── Input Handling ────────────────────────────────────────────────────────
        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            e.Handled = true;

            var s = GetOrCreate(sv);

            // Re-sync position if thumb was dragged or page keys were pressed
            if (Math.Abs(sv.VerticalOffset - s.Current) > 1.0 && Math.Abs(s.Velocity) < StopVelocity)
            {
                s.Current = sv.VerticalOffset;
                s.Velocity = 0;
            }

            // Delta > 0 is scroll up (decreases vertical offset)
            double impulse = -(e.Delta / 120.0) * (120.0 * VelocityMultiplier);

            // Accumulate velocity if scrolling in the same direction, else reset direction instantly
            if (Math.Sign(impulse) == Math.Sign(s.Velocity))
            {
                s.Velocity = Math.Clamp(s.Velocity + impulse, -MaxVelocity, MaxVelocity);
            }
            else
            {
                s.Velocity = impulse;
            }

            SuppressHover(sv, s);

            if (!_active.Contains(sv))
            {
                _active.Add(sv);
            }

            if (!_hooked)
            {
                _lastTime = TimeSpan.Zero;
                CompositionTarget.Rendering += OnRendering;
                _hooked = true;
            }
        }

        // ── Render Loop ───────────────────────────────────────────────────────────
        private static void OnRendering(object? sender, EventArgs e)
        {
            var args = (RenderingEventArgs)e;
            if (args.RenderingTime == _lastTime) return;

            double dt = _lastTime == TimeSpan.Zero ? 0.016 : (args.RenderingTime - _lastTime).TotalSeconds;
            _lastTime = args.RenderingTime;

            // Clamp delta time to avoid large jumps on first frame or system pauses
            dt = Math.Clamp(dt, 0.001, 0.04);

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var sv = _active[i];
                if (!_states.TryGetValue(sv, out var s))
                {
                    _active.RemoveAt(i);
                    continue;
                }

                // Stop condition: velocity fell below threshold
                if (Math.Abs(s.Velocity) < StopVelocity)
                {
                    s.Velocity = 0;
                    _active.RemoveAt(i);

                    if (s.Content != null)
                    {
                        s.Content.IsHitTestVisible = true;
                    }
                    s.HoverTimer?.Stop();
                    continue;
                }

                // Analytical integration: Δx = v0 * (1 - e^(-f * dt)) / f
                double decay = Math.Exp(-Friction * dt);
                double deltaOffset = s.Velocity * (1.0 - decay) / Friction;
                s.Velocity *= decay;

                double nextOffset = Math.Clamp(s.Current + deltaOffset, 0, sv.ScrollableHeight);

                // Stop momentum immediately when hitting scroll boundaries
                if ((nextOffset <= 0 && s.Velocity < 0) || (nextOffset >= sv.ScrollableHeight && s.Velocity > 0))
                {
                    s.Velocity = 0;
                }

                s.Current = nextOffset;

                // Sub-pixel offsets applied directly without rounding
                sv.ScrollToVerticalOffset(s.Current);
            }

            if (_active.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }

        // ── Hover Suppression ─────────────────────────────────────────────────────
        private static void SuppressHover(ScrollViewer sv, ScrollState s)
        {
            s.Content ??= sv.Content as UIElement;
            if (s.Content == null) return;

            s.Content.IsHitTestVisible = false;

            if (s.HoverTimer == null)
            {
                s.HoverTimer = new DispatcherTimer(DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                var content = s.Content;
                s.HoverTimer.Tick += (_, _) =>
                {
                    s.HoverTimer!.Stop();
                    content.IsHitTestVisible = true;
                };
            }

            s.HoverTimer.Stop();
            s.HoverTimer.Start();
        }
    }
}