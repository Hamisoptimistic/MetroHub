using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace MetroHub.Widgets.Behaviors;

/// <summary>
/// Reusable attached behavior (via Microsoft.Xaml.Behaviors.Wpf) for widget controls (Phase 0.6).
/// Marks PreviewMouseLeftButtonDown as handled to safeguard interactive controls
/// (buttons, sliders, text inputs, combo boxes) from triggering canvas tile dragging or selection.
/// </summary>
public class InputSafeguardBehavior : Behavior<UIElement>
{
    public static readonly DependencyProperty MarkHandledProperty = DependencyProperty.Register(
        nameof(MarkHandled),
        typeof(bool),
        typeof(InputSafeguardBehavior),
        new PropertyMetadata(true));

    public bool MarkHandled
    {
        get => (bool)GetValue(MarkHandledProperty);
        set => SetValue(MarkHandledProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
        base.OnDetaching();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MarkHandled)
        {
            e.Handled = true;
        }
    }
}

/// <summary>
/// Attached property helper enabling concise XAML usage:
/// behaviors:InputSafeguard.SuppressTileDrag="True"
/// </summary>
public static class InputSafeguard
{
    public static readonly DependencyProperty SuppressTileDragProperty =
        DependencyProperty.RegisterAttached(
            "SuppressTileDrag",
            typeof(bool),
            typeof(InputSafeguard),
            new PropertyMetadata(false, OnSuppressTileDragChanged));

    public static bool GetSuppressTileDrag(DependencyObject obj) => (bool)obj.GetValue(SuppressTileDragProperty);
    public static void SetSuppressTileDrag(DependencyObject obj, bool value) => obj.SetValue(SuppressTileDragProperty, value);

    private static void OnSuppressTileDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            if ((bool)e.NewValue)
            {
                element.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            }
            else
            {
                element.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            }
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
