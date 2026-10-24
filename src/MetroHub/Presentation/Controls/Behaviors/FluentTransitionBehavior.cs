using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MetroHub.Presentation.Themes;

namespace MetroHub.Presentation.Controls.Behaviors;

/// <summary>
/// Stationary micro-fade transition (70ms, from opacity 0.65 to 1.0).
/// Zero pixel displacement: text stays strictly locked to device pixels with zero UI-thread overhead.
/// </summary>
public static class FluentTransitionBehavior
{
    private static readonly DoubleAnimation FadeAnim = CreateFadeAnimation();

    private static DoubleAnimation CreateFadeAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = 0.65,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(70),
            FillBehavior = FillBehavior.Stop
        };
        anim.Freeze();
        return anim;
    }

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(FluentTransitionBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static readonly DependencyProperty HasRenderedInitialViewProperty =
        DependencyProperty.RegisterAttached(
            "HasRenderedInitialView",
            typeof(bool),
            typeof(FluentTransitionBehavior),
            new PropertyMetadata(false));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl control) return;

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            ContentControl.ContentProperty, typeof(ContentControl));

        descriptor?.RemoveValueChanged(control, OnContentChanged);
        control.Loaded -= OnControlLoaded;
        control.Unloaded -= OnControlUnloaded;

        if ((bool)e.NewValue)
        {
            descriptor?.AddValueChanged(control, OnContentChanged);
            control.Loaded += OnControlLoaded;
            control.Unloaded += OnControlUnloaded;
        }
        else
        {
            control.BeginAnimation(UIElement.OpacityProperty, null);
            control.Opacity = 1.0;
        }
    }

    private static void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl control && GetIsEnabled(control))
        {
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
            control.BeginAnimation(UIElement.OpacityProperty, null);
            control.Opacity = 1.0;
        }
    }

    private static void OnContentChanged(object? sender, EventArgs e)
    {
        if (sender is not ContentControl control || control.Content == null) return;

        // Skip animation on initial mount so window opens instantly without delay
        bool hasRenderedInitial = (bool)control.GetValue(HasRenderedInitialViewProperty);
        if (!hasRenderedInitial)
        {
            control.SetValue(HasRenderedInitialViewProperty, true);
            control.Opacity = 1.0;
            return;
        }

        if (!MotionTokens.AnimationsEnabled)
        {
            control.Opacity = 1.0;
            return;
        }

        control.Opacity = 1.0;
        control.BeginAnimation(UIElement.OpacityProperty, FadeAnim, HandoffBehavior.SnapshotAndReplace);
    }
}
