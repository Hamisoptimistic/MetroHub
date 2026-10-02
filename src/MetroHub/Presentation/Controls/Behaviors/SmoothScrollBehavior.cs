using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Presentation.Controls
{
    /// <summary>
    /// Frame-rate-independent continuous exponential smooth scrolling behavior.
    /// Provides silky, natural inertia matching modern Chromium and macOS physics,
    /// with integer pixel-snapping to preserve DirectWrite text texture caching and strict nested container containment.
    /// </summary>
    public static class SmoothScrollBehavior
    {
        // ── Physics Tuning ────────────────────────────────────────────────────────
        // Pixels traveled per standard 120-unit wheel notch in pixel-scrolling mode (matches 1 grid tile unit).
        private const double ScrollStep = 72.0;

        // Items traveled per wheel notch in item-based (logical) scrolling mode.
        private const double ItemStep = 1.0;

        // ── Public Attached Property ──────────────────────────────────────────────
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        // ── Hooked Viewer Storage (for container elements like ListView/ListBox) ──
        private static readonly DependencyProperty HookedViewerProperty =
            DependencyProperty.RegisterAttached(
                "HookedViewer",
                typeof(ScrollViewer),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(null));

        // ── Per-ScrollViewer State ────────────────────────────────────────────────
        private sealed class ScrollState
        {
            public double Target;
            public double Current;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly List<ScrollViewer> _active = new();
        private static bool _hooked;
        private static TimeSpan _lastTime = TimeSpan.Zero;

        // ── Lifecycle & Attachment ────────────────────────────────────────────────
        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            bool isEnabled = (bool)e.NewValue;

            if (d is ScrollViewer sv)
            {
                ApplyToScrollViewer(sv, isEnabled);
            }
            else if (d is FrameworkElement fe)
            {
                if (isEnabled)
                {
                    fe.PreviewMouseWheel -= OnParentPreviewMouseWheel;
                    fe.PreviewMouseWheel += OnParentPreviewMouseWheel;

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
                    fe.PreviewMouseWheel -= OnParentPreviewMouseWheel;

                    if (fe.GetValue(HookedViewerProperty) is ScrollViewer hooked)
                    {
                        ApplyToScrollViewer(hooked, false);
                        fe.ClearValue(HookedViewerProperty);
                    }
                    else
                    {
                        var childSv = FindChild<ScrollViewer>(fe);
                        if (childSv != null)
                        {
                            ApplyToScrollViewer(childSv, false);
                        }
                    }
                }
            }
        }

        private static void OnParentPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var sv = fe.GetValue(HookedViewerProperty) as ScrollViewer ?? FindChild<ScrollViewer>(fe);
            if (sv != null)
            {
                if (fe.GetValue(HookedViewerProperty) == null)
                {
                    fe.SetValue(HookedViewerProperty, sv);
                    ApplyToScrollViewer(sv, true);
                }

                if (!e.Handled)
                {
                    OnWheel(sv, e);
                }
            }
        }

        private static void AttachToChild(FrameworkElement parent)
        {
            var sv = FindChild<ScrollViewer>(parent);
            if (sv != null)
            {
                parent.SetValue(HookedViewerProperty, sv);
                ApplyToScrollViewer(sv, true);
            }
            else
            {
                parent.Dispatcher.InvokeAsync(() =>
                {
                    var delayedSv = FindChild<ScrollViewer>(parent);
                    if (delayedSv != null)
                    {
                        parent.SetValue(HookedViewerProperty, delayedSv);
                        ApplyToScrollViewer(delayedSv, true);
                    }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private static void ApplyToScrollViewer(ScrollViewer sv, bool enable)
        {
            if (enable)
            {
                sv.PreviewMouseWheel -= OnWheel;
                sv.PreviewMouseWheel += OnWheel;
                sv.Loaded -= OnLoaded;
                sv.Loaded += OnLoaded;
                sv.Unloaded -= OnUnloaded;
                sv.Unloaded += OnUnloaded;
                sv.ScrollChanged -= OnScrollChanged;
                sv.ScrollChanged += OnScrollChanged;

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
                sv.ScrollChanged -= OnScrollChanged;

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
            s.Current = sv.VerticalOffset;
            s.Target = sv.VerticalOffset;

            if (sv.TemplatedParent is ItemsControl itemsControl && !sv.CanContentScroll)
            {
                VirtualizingPanel.SetScrollUnit(itemsControl, ScrollUnit.Pixel);
            }
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            _states.Remove(sv);
            _active.Remove(sv);

            if (_active.Count == 0 && _hooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            if (_states.TryGetValue(sv, out var s))
            {
                // If user dragged the thumb or track clicked while animating, snap immediately
                if (Math.Abs(sv.VerticalOffset - s.Current) > 2.0)
                {
                    s.Current = sv.VerticalOffset;
                    s.Target = sv.VerticalOffset;

                    if (_active.Contains(sv))
                    {
                        _active.Remove(sv);
                    }
                }
            }
        }

        private static ScrollState GetOrCreate(ScrollViewer sv)
        {
            if (!_states.TryGetValue(sv, out var s))
            {
                s = new ScrollState
                {
                    Current = sv.VerticalOffset,
                    Target = sv.VerticalOffset
                };
                _states[sv] = s;
            }
            return s;
        }

        // ── Input Handling & Containment ──────────────────────────────────────────
        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            // Check if user is hovering over an interactive child control (e.g. Volume slider, or a nested ScrollViewer/ListView)
            if (IsInteractiveChild(e.OriginalSource as DependencyObject, sv))
            {
                return;
            }

            // Strict containment: mark handled so wheel never bubbles up to ancestor containers
            e.Handled = true;

            // Immediately mute tile reveal effects so moving tiles under the cursor don't spawn animations
            TileControl.SuppressRevealForScrolling();

            var s = GetOrCreate(sv);

            // Re-sync if external offset change occurred while idle
            if (Math.Abs(sv.VerticalOffset - s.Current) > 2.0 && !_active.Contains(sv))
            {
                s.Current = sv.VerticalOffset;
                s.Target = sv.VerticalOffset;
            }

            double step = sv.CanContentScroll ? ItemStep : ScrollStep;
            double delta = -(e.Delta / 120.0) * step;

            // Continuous target accumulation: successive wheel ticks seamlessly build momentum
            // without resetting velocity, jumping, or causing frame hitches.
            // Clamped with lead bound to prevent runaway speed on high-frequency wheel ticks
            double maxLead = sv.CanContentScroll ? 10.0 : 360.0;
            double rawTarget = s.Target + delta;
            s.Target = Math.Clamp(rawTarget, Math.Max(0, s.Current - maxLead), Math.Min(sv.ScrollableHeight, s.Current + maxLead));

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

        private static bool IsInteractiveChild(DependencyObject? source, ScrollViewer owner)
        {
            var current = source;
            while (current != null && current != owner)
            {
                if (current is RangeBase) return true; // Slider, ScrollBar thumb/track
                if (current is WidgetVolumeSlider) return true; // Volume, Brightness, Night Light, Sleep sliders
                if (current is TextBoxBase) return true;
                if (current is PasswordBox) return true;
                if (current is ScrollViewer nestedSv && nestedSv != owner) return true; // Nested ScrollViewer handles its own scrolling
                if (current is ListBox) return true; // ListView, ListBox, etc.
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        // ── Render Loop ───────────────────────────────────────────────────────────
        private static void OnRendering(object? sender, EventArgs e)
        {
            var args = (RenderingEventArgs)e;
            if (args.RenderingTime == _lastTime) return;

            double dt = _lastTime == TimeSpan.Zero ? 0.016 : (args.RenderingTime - _lastTime).TotalSeconds;
            _lastTime = args.RenderingTime;

            // Guard against large frame hitches (e.g. window move or system pause)
            dt = Math.Clamp(dt, 0.001, 0.05);

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var sv = _active[i];
                if (!_states.TryGetValue(sv, out var s))
                {
                    _active.RemoveAt(i);
                    continue;
                }

                double diff = s.Target - s.Current;
                if (Math.Abs(diff) < 0.25)
                {
                    s.Current = s.Target;
                    double finalOffset = sv.CanContentScroll ? s.Current : Math.Round(s.Current);
                    if (Math.Abs(sv.VerticalOffset - finalOffset) >= 0.5)
                    {
                        sv.ScrollToVerticalOffset(finalOffset);
                    }
                    _active.RemoveAt(i);
                    continue;
                }

                // Continuous frame-rate independent exponential smoothing: factor = 1 - e^(-lambda * dt)
                // Yields identical physical glide velocity on 60Hz, 120Hz, and 144Hz monitors,
                // zero velocity discontinuity, and silky organic deceleration.
                double lambda = sv.CanContentScroll ? 26.0 : 16.0;
                double factor = 1.0 - Math.Exp(-lambda * dt);

                s.Current += diff * factor;

                // Physical integer pixel-snapping preserves DirectWrite ClearType text cache
                double renderOffset = sv.CanContentScroll ? s.Current : Math.Round(s.Current);
                if (Math.Abs(sv.VerticalOffset - renderOffset) >= 0.5)
                {
                    sv.ScrollToVerticalOffset(renderOffset);
                }
            }

            // Unhook render loop when idle - exactly 0% idle CPU overhead
            if (_active.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }

        // ── Visual Tree Helper ────────────────────────────────────────────────────
        internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                {
                    return typed;
                }

                var found = FindChild<T>(child);
                if (found != null) return found;
            }

            return null;
        }
    }
}