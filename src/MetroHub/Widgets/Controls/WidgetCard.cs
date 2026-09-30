using System.Windows;
using System.Windows.Controls;

namespace MetroHub.Widgets;

/// <summary>
/// Reusable base card shell for MetroHub widgets (Phase 0.2).
/// Provides consistent dark background, hairline border, uniform corner radius, and internal padding.
/// Widget authors only supply interior content inside this shell.
/// </summary>
public class WidgetCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(WidgetCard),
            new FrameworkPropertyMetadata(new CornerRadius(2)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static WidgetCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WidgetCard),
            new FrameworkPropertyMetadata(typeof(WidgetCard)));
    }
}
