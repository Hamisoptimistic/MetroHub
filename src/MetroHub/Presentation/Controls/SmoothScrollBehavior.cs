using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controls
{
    /// <summary>
    /// Smooth inertia scrolling using CompositionTarget.Rendering for per-frame updates
    /// synchronized with WPF's GPU render pass — no animation system overhead.
    ///
    /// Algorithm: frame-rate-independent exponential lerp.
    ///   factor = 1 - e^(-SpringK × Δt)
    /// Same feel at 30 / 60 / 144 Hz. Natural deceleration, no abrupt stops.
    ///
    /// Usage: controls:SmoothScrollBehavior.IsEnabled="True"
    /// </summary>
    public static class SmoothScrollBehavior
    {
        // ── Tuning ────────────────────────────────────────────────────────────────
        /// <summary>
        /// Spring stiffness. Higher = snappier.
        /// k=10 → reaches ~86% of target in 1/10 s (similar to Windows 11 feel).
        /// </summary>
        private const double SpringK = 10.0;

        /// <summary>Pixels per mouse-wheel notch (Windows default = 3 lines × ~40px).</summary>
        private const double ScrollStep = 120.0;

        /// <summary>Stop animating when remaining distance is below this (px).</summary>
        private const double StopThreshold = 0.4;

        // ── Public attached property ──────────────────────────────────────────────
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        // ── Per-ScrollViewer state ────────────────────────────────────────────────
        private sealed class ScrollState
        {
            public double Current;
            public double Target;
            public UIElement? Content;
            public DispatcherTimer? HoverTimer;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states  = new();
        private static readonly List<ScrollViewer>                     _active  = new();
        private static bool     _hooked;
        private static TimeSpan _lastTime = TimeSpan.Zero;

        // ── Wiring ────────────────────────────────────────────────────────────────
        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;
            if ((bool)e.NewValue)
            {
                sv.PreviewMouseWheel += OnWheel;
                sv.Loaded   += OnLoaded;
                sv.Unloaded += OnUnloaded;
            }
            else
            {
                sv.PreviewMouseWheel -= OnWheel;
                sv.Loaded   -= OnLoaded;
                sv.Unloaded -= OnUnloaded;
                _states.Remove(sv);
                _active.Remove(sv);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            var s = GetOrCreate(sv);
            s.Content = sv.Content as UIElement;
            s.Current = sv.VerticalOffset;
            s.Target  = sv.VerticalOffset;
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            _states.Remove(sv);
            _active.Remove(sv);
        }

        private static ScrollState GetOrCreate(ScrollViewer sv)
        {
            if (!_states.TryGetValue(sv, out var s))
            {
                s = new ScrollState { Current = sv.VerticalOffset, Target = sv.VerticalOffset };
                _states[sv] = s;
            }
            return s;
        }

        // ── Wheel handler ─────────────────────────────────────────────────────────
        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            e.Handled = true;

            var s = GetOrCreate(sv);

            // Drag-sync: user dragged scrollbar → re-sync our tracked position
            if (Math.Abs(sv.VerticalOffset - s.Current) > 2.0)
            {
                s.Current = sv.VerticalOffset;
                s.Target  = sv.VerticalOffset;
            }

            // Accumulate target offset
            s.Target = Math.Clamp(
                s.Target + (-e.Delta / 120.0 * ScrollStep),
                0, sv.ScrollableHeight);

            // Suppress item hover highlights during scroll
            SuppressHover(sv, s);

            // Register this viewer for the render loop
            if (!_active.Contains(sv)) _active.Add(sv);

            // Hook into WPF's render pass (fires once per frame, before GPU present)
            if (!_hooked)
            {
                _lastTime = TimeSpan.Zero;
                CompositionTarget.Rendering += OnRendering;
                _hooked = true;
            }
        }

        // ── Per-frame spring update (the actual smooth scroll) ────────────────────
        private static void OnRendering(object? sender, EventArgs e)
        {
            var args = (RenderingEventArgs)e;

            // Guard: CompositionTarget.Rendering can fire multiple times with the same
            // timestamp in the same render cycle — skip duplicate calls
            if (args.RenderingTime == _lastTime) return;

            double dt = (args.RenderingTime - _lastTime).TotalSeconds;
            _lastTime = args.RenderingTime;

            // Clamp dt to avoid huge jumps on first frame or after tab/window switch
            dt = Math.Clamp(dt, 0.001, 0.05);

            // Frame-rate-independent exponential lerp factor
            // At 60hz  (dt=0.0167): factor ≈ 0.154
            // At 144hz (dt=0.0069): factor ≈ 0.068
            // Feel is identical regardless of refresh rate
            double factor = 1.0 - Math.Exp(-SpringK * dt);

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var sv = _active[i];
                if (!_states.TryGetValue(sv, out var s)) { _active.RemoveAt(i); continue; }

                double remaining = s.Target - s.Current;

                if (Math.Abs(remaining) < StopThreshold)
                {
                    // Snap to final target — animation complete
                    s.Current = s.Target;
                    sv.ScrollToVerticalOffset(s.Current);
                    _active.RemoveAt(i);

                    // Restore hover now that list has stopped moving
                    if (s.Content != null)
                        s.Content.IsHitTestVisible = true;
                    s.HoverTimer?.Stop();
                }
                else
                {
                    // Exponential approach: smooth, natural, no abrupt stops
                    s.Current += remaining * factor;
                    sv.ScrollToVerticalOffset(s.Current);
                }
            }

            // Unhook when nothing is animating — zero idle CPU cost
            if (_active.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }

        // ── Hover suppression ─────────────────────────────────────────────────────
        private static void SuppressHover(ScrollViewer sv, ScrollState s)
        {
            s.Content ??= sv.Content as UIElement;
            if (s.Content == null) return;

            s.Content.IsHitTestVisible = false;

            // Fallback timer: re-enables hover if the render loop settles faster
            // than the timer fires, or if something interrupts the scroll
            if (s.HoverTimer == null)
            {
                s.HoverTimer = new DispatcherTimer(DispatcherPriority.Input)
                    { Interval = TimeSpan.FromMilliseconds(500) };
                var content = s.Content;
                s.HoverTimer.Tick += (_, _) =>
                {
                    s.HoverTimer!.Stop();
                    content.IsHitTestVisible = true;
                };
            }

            // Reset timer on every wheel tick — keeps hover suppressed during rapid spinning
            s.HoverTimer.Stop();
            s.HoverTimer.Start();
        }
    }
}
