using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Attached behavior for ScrollViewer (or controls hosting a ScrollViewer) that translates
/// standard mouse-wheel rotation into horizontal scrolling.
/// <para>
/// Can be attached to the ScrollViewer directly, or to a container element — in which case the
/// first ScrollViewer in its visual tree is hooked (and the container's <see cref="ScrollFactorProperty"/>
/// value is handed down to it, so the factor still applies when declared on the container).
/// </para>
/// </summary>
public static class HorizontalScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(HorizontalScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static readonly DependencyProperty ScrollFactorProperty =
        DependencyProperty.RegisterAttached(
            "ScrollFactor",
            typeof(double),
            typeof(HorizontalScrollBehavior),
            new PropertyMetadata(1.0));

    public static double GetScrollFactor(DependencyObject obj) => (double)obj.GetValue(ScrollFactorProperty);
    public static void SetScrollFactor(DependencyObject obj, double value) => obj.SetValue(ScrollFactorProperty, value);

    /// <summary>
    /// The ScrollViewer actually carrying the handler on behalf of a container element. Kept so
    /// disabling the behavior can unhook the viewer it hooked — without this the handler could
    /// never be removed once a child viewer had been resolved.
    /// </summary>
    private static readonly DependencyProperty HookedViewerProperty =
        DependencyProperty.RegisterAttached(
            "HookedViewer",
            typeof(ScrollViewer),
            typeof(HorizontalScrollBehavior),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        bool enable = (bool)e.NewValue;

        if (d is ScrollViewer sv)
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            if (enable)
            {
                sv.PreviewMouseWheel += OnPreviewMouseWheel;
            }
            return;
        }

        if (d is not FrameworkElement fe)
        {
            return;
        }

        if (enable)
        {
            // Already-realized elements never raise Loaded again: attach immediately instead of
            // waiting for an event that will not come (the sibling AutoHideScrollBehavior does the same).
            if (fe.IsLoaded)
            {
                AttachToChild(fe);
            }
            else
            {
                fe.Loaded -= OnElementLoaded;
                fe.Loaded += OnElementLoaded;
            }
            return;
        }

        fe.Loaded -= OnElementLoaded;
        fe.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (fe.GetValue(HookedViewerProperty) is ScrollViewer hooked)
        {
            hooked.PreviewMouseWheel -= OnPreviewMouseWheel;
            fe.ClearValue(HookedViewerProperty);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.Loaded -= OnElementLoaded;
            AttachToChild(fe);
        }
    }

    private static void AttachToChild(FrameworkElement fe)
    {
        var sv = FindVisualChild<ScrollViewer>(fe);
        if (sv != null)
        {
            // Carry a factor declared on the container down to the viewer the handler will read it from.
            if (fe.ReadLocalValue(ScrollFactorProperty) is double declared && declared > 0)
            {
                sv.SetValue(ScrollFactorProperty, declared);
            }

            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            fe.SetValue(HookedViewerProperty, sv);
        }
        else
        {
            fe.PreviewMouseWheel -= OnPreviewMouseWheel;
            fe.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;

        ScrollViewer? sv = sender as ScrollViewer;
        if (sv == null && sender is DependencyObject d)
        {
            sv = FindVisualChild<ScrollViewer>(d);
        }

        if (sv == null || sv.ScrollableWidth <= 0) return;

        double factor = GetScrollFactor(sv);
        if (factor <= 0) factor = 1.0;

        // Scroll horizontally based on delta
        double step = e.Delta * factor;
        double targetOffset = Math.Max(0, Math.Min(sv.ScrollableWidth, sv.HorizontalOffset - step));

        // Already at the end the wheel is pointing at: leave the event unhandled so the gesture
        // reaches whatever scrolls behind the strip instead of dying in a dead zone.
        if (Math.Abs(targetOffset - sv.HorizontalOffset) < 0.5)
        {
            return;
        }

        sv.ScrollToHorizontalOffset(targetOffset);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
