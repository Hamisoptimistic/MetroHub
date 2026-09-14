using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Widgets;

/// <summary>
/// An individual selectable segment item within a <see cref="WidgetSegmentedControl"/>.
/// Supports clean borderless typography with Fluent glass hover reveal and selection state.
/// </summary>
public class WidgetSegmentedItem : ListBoxItem
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(object),
            typeof(WidgetSegmentedItem),
            new PropertyMetadata(null));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static readonly DependencyPropertyKey IsPressedPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsPressed),
            typeof(bool),
            typeof(WidgetSegmentedItem),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsPressedProperty =
        IsPressedPropertyKey.DependencyProperty;

    public bool IsPressed => (bool)GetValue(IsPressedProperty);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        SetValue(IsPressedPropertyKey, true);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        SetValue(IsPressedPropertyKey, false);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Point pt = e.GetPosition(this);
        if (pt.X >= 0 && pt.X <= ActualWidth && pt.Y >= 0 && pt.Y <= ActualHeight)
        {
            FindParentControl()?.NotifyItemClicked(this);
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        SetValue(IsPressedPropertyKey, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            FindParentControl()?.NotifyItemClicked(this);
            e.Handled = true;
        }
    }

    private WidgetSegmentedControl? FindParentControl()
    {
        var parent = ItemsControl.ItemsControlFromItemContainer(this) as WidgetSegmentedControl;
        if (parent != null) return parent;

        DependencyObject? cur = this;
        while (cur != null)
        {
            if (cur is WidgetSegmentedControl wsc) return wsc;
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    static WidgetSegmentedItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WidgetSegmentedItem),
            new FrameworkPropertyMetadata(typeof(WidgetSegmentedItem)));
    }
}
