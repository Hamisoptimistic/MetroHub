using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MetroHub.Core.Services;
using MetroHub.Presentation.Themes;

namespace MetroHub.Presentation.Controls.Behaviors;

public enum FluentTransitionMode
{
    None,
    VerticalDrillIn,   // Flow B: Y: +14 -> 0 (180ms)
    HorizontalSlide    // Flow A: X: +20 -> 0 (Drill In, 180ms) or In-Place Fade (Back to Home, 140ms)
}

/// <summary>
/// Production-grade Fluent entrance transition behavior for ContentControl.
/// Eliminates start layout stalls, child-Loaded frame drops, and end-of-animation pixel snapping.
/// </summary>
public static class FluentTransitionBehavior
{
    // CubicEase (EaseOut) matches WinUI 3 entrance curves and eliminates
    // the extended subpixel crawl of high-exponent easing functions.
    private static readonly IEasingFunction FluentTransformEasing = CreateTransformEasing();
    private static readonly IEasingFunction FluentOpacityEasing = CreateOpacityEasing();
    private static int _cachedRefreshRate;

    private static IEasingFunction CreateTransformEasing()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static IEasingFunction CreateOpacityEasing()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static int GetTargetFrameRate()
    {
        if (_cachedRefreshRate <= 0)
        {
            int rate = NativeMethods.GetScreenRefreshRate();
            _cachedRefreshRate = rate > 0 ? rate : 120;
        }
        return _cachedRefreshRate;
    }

    #region Attached Properties

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(FluentTransitionMode),
            typeof(FluentTransitionBehavior),
            new PropertyMetadata(FluentTransitionMode.None, OnModeChanged));

    public static void SetMode(DependencyObject element, FluentTransitionMode value) =>
        element.SetValue(ModeProperty, value);

    public static FluentTransitionMode GetMode(DependencyObject element) =>
        (FluentTransitionMode)element.GetValue(ModeProperty);

    private static readonly DependencyProperty HasRenderedInitialViewProperty =
        DependencyProperty.RegisterAttached(
            "HasRenderedInitialView",
            typeof(bool),
            typeof(FluentTransitionBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty AnimationVersionProperty =
        DependencyProperty.RegisterAttached(
            "AnimationVersion",
            typeof(int),
            typeof(FluentTransitionBehavior),
            new PropertyMetadata(0));

    #endregion

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl contentControl) return;

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            ContentControl.ContentProperty, typeof(ContentControl));

        descriptor?.RemoveValueChanged(contentControl, OnContentChanged);
        contentControl.Loaded -= OnControlLoaded;
        contentControl.Unloaded -= OnControlUnloaded;

        if ((FluentTransitionMode)e.NewValue != FluentTransitionMode.None)
        {
            EnsurePersistentTransform(contentControl);
            descriptor?.AddValueChanged(contentControl, OnContentChanged);
            contentControl.Loaded += OnControlLoaded;
            contentControl.Unloaded += OnControlUnloaded;
        }
    }

    private static void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl control && GetMode(control) != FluentTransitionMode.None)
        {
            EnsurePersistentTransform(control);
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                ContentControl.ContentProperty, typeof(ContentControl));
            descriptor?.RemoveValueChanged(control, OnContentChanged);
            descriptor?.AddValueChanged(control, OnContentChanged);
        }
    }

    private static void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl control)
        {
            control.SetValue(HasRenderedInitialViewProperty, false);

            var descriptor = DependencyPropertyDescriptor.FromProperty(
                ContentControl.ContentProperty, typeof(ContentControl));
            descriptor?.RemoveValueChanged(control, OnContentChanged);
        }
    }

    private static TranslateTransform EnsurePersistentTransform(UIElement target)
    {
        if (target.RenderTransform is TranslateTransform existing && !existing.IsFrozen)
        {
            return existing;
        }

        var transform = new TranslateTransform(0.0, 0.0);
        target.RenderTransform = transform;
        return transform;
    }

    private static void OnContentChanged(object? sender, EventArgs e)
    {
        if (sender is not ContentControl control || control.Content == null) return;

        var mode = GetMode(control);
        if (mode == FluentTransitionMode.None) return;

        var transform = EnsurePersistentTransform(control);

        if (!MotionTokens.AnimationsEnabled)
        {
            ResetToRestingState(control, transform);
            return;
        }

        // Suppress animation on initial mount when the window or detail shell first opens
        bool hasRenderedInitial = (bool)control.GetValue(HasRenderedInitialViewProperty);
        if (!hasRenderedInitial)
        {
            control.SetValue(HasRenderedInitialViewProperty, true);
            ResetToRestingState(control, transform);
            return;
        }

        int currentVersion = (int)control.GetValue(AnimationVersionProperty) + 1;
        control.SetValue(AnimationVersionProperty, currentVersion);

        double offsetX = 0.0;
        double offsetY = 0.0;
        int durationMs = 180;

        if (mode == FluentTransitionMode.HorizontalSlide)
        {
            bool isReturningHome = IsHomeState(control.Content);
            // Drill-In slides in from +20px. Returning to Home uses in-place fade at 0px
            // to avoid exposing an unrendered 20px gap on the right window edge.
            offsetX = isReturningHome ? 0.0 : 20.0;
            durationMs = isReturningHome ? 140 : 180;
        }
        else if (mode == FluentTransitionMode.VerticalDrillIn)
        {
            offsetY = 14.0;
            durationMs = 180;
        }

        // 1. Synchronously clear active clocks and pre-position at the starting offset
        //    BEFORE the layout/render pass so Frame 0 is already at (offsetX, offsetY).
        control.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);

        control.Opacity = 0.0;
        transform.X = offsetX;
        transform.Y = offsetY;

        // 2. Schedule at DispatcherPriority.Background (priority 4) so that:
        //    - DataTemplate inflation & Measure/Arrange (Render, priority 7) finish first
        //    - All child control Loaded events (Loaded, priority 6) finish second
        //    - The UI thread is 100% idle when the animation clock starts at T=0.
        control.Dispatcher.InvokeAsync(() =>
        {
            PlayEntrance(control, transform, mode, offsetX, offsetY, durationMs, currentVersion);
        }, DispatcherPriority.Background);
    }

    private static bool IsHomeState(object content)
    {
        string typeName = content.GetType().Name;
        return typeName.Contains("Home", StringComparison.OrdinalIgnoreCase);
    }

    private static void PlayEntrance(
        UIElement target,
        TranslateTransform transform,
        FluentTransitionMode mode,
        double offsetX,
        double offsetY,
        int durationMs,
        int expectedVersion)
    {
        if ((int)target.GetValue(AnimationVersionProperty) != expectedVersion) return;

        if (target is FrameworkElement { IsLoaded: false })
        {
            ResetToRestingState(target, transform);
            return;
        }

        int refreshRate = GetTargetFrameRate();
        var translateDuration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var opacityDuration = new Duration(TimeSpan.FromMilliseconds(Math.Min(140, durationMs)));

        // Set resting base values before applying HoldEnd/Stop clocks
        target.Opacity = 1.0;
        transform.X = 0.0;
        transform.Y = 0.0;

        var animOpacity = new DoubleAnimation(0.0, 1.0, opacityDuration)
        {
            EasingFunction = FluentOpacityEasing,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animOpacity, refreshRate);

        double startOffset = mode == FluentTransitionMode.HorizontalSlide ? offsetX : offsetY;

        DependencyProperty translateProp = mode == FluentTransitionMode.HorizontalSlide
            ? TranslateTransform.XProperty
            : TranslateTransform.YProperty;

        if (Math.Abs(startOffset) > 0.001)
        {
            var animTranslate = new DoubleAnimation(startOffset, 0.0, translateDuration)
            {
                EasingFunction = FluentTransformEasing,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animTranslate, refreshRate);

            animTranslate.Completed += (_, _) =>
            {
                if ((int)target.GetValue(AnimationVersionProperty) != expectedVersion) return;

                // Because FillBehavior.Stop automatically reverts to the base values (Opacity=1, X=0, Y=0)
                // and we keep the persistent identity TranslateTransform attached, zero layout or visual
                // tree mutations occur here—eliminating the end-of-animation snap.
                target.BeginAnimation(UIElement.OpacityProperty, null);
                transform.BeginAnimation(translateProp, null);
            };

            target.BeginAnimation(UIElement.OpacityProperty, animOpacity);
            transform.BeginAnimation(translateProp, animTranslate);
        }
        else
        {
            animOpacity.Completed += (_, _) =>
            {
                if ((int)target.GetValue(AnimationVersionProperty) != expectedVersion) return;
                target.BeginAnimation(UIElement.OpacityProperty, null);
            };

            target.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        }
    }

    private static void ResetToRestingState(UIElement target, TranslateTransform transform)
    {
        target.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        target.Opacity = 1.0;
        transform.X = 0.0;
        transform.Y = 0.0;
    }
}
