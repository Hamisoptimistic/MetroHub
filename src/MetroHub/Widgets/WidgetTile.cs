using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Widgets;

/// <summary>
/// An individual selectable tile item within a <see cref="WidgetTiles"/> strip.
/// Displays an icon, header label, and provides its own dynamic <see cref="IndicatorBrush"/>.
/// </summary>
public class WidgetTile : ListBoxItem
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(object),
            typeof(WidgetTile),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(object),
            typeof(WidgetTile),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(WidgetTile),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IndicatorBrushProperty =
        DependencyProperty.Register(
            nameof(IndicatorBrush),
            typeof(Brush),
            typeof(WidgetTile),
            new PropertyMetadata(
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E676")),
                OnIndicatorBrushChanged));

    private static void OnIndicatorBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetTile tile)
        {
            WidgetTiles? parent = ItemsControl.ItemsControlFromItemContainer(tile) as WidgetTiles
                ?? tile.Parent as WidgetTiles;
            if (parent == null)
            {
                DependencyObject current = tile;
                while (current != null && parent == null)
                {
                    current = VisualTreeHelper.GetParent(current);
                    parent = current as WidgetTiles;
                }
            }

            if (parent != null)
            {
                bool isSelected = tile.IsSelected ||
                    (parent.SelectedIndex >= 0 && parent.SelectedIndex < parent.Items.Count && parent.Items[parent.SelectedIndex] == tile) ||
                    (parent.SelectedValue != null && Equals(parent.SelectedValue, tile.Value));

                if (isSelected)
                {
                    parent.UpdateIndicatorBrush(e.NewValue as Brush);
                }
            }
        }
    }

    private static readonly DependencyPropertyKey IsPressedPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsPressed),
            typeof(bool),
            typeof(WidgetTile),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsPressedProperty =
        IsPressedPropertyKey.DependencyProperty;

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public Brush IndicatorBrush
    {
        get => (Brush)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

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
            FindParentControl()?.NotifyTileClicked(this);
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
            FindParentControl()?.NotifyTileClicked(this);
            e.Handled = true;
        }
    }

    private WidgetTiles? FindParentControl()
    {
        var parent = ItemsControl.ItemsControlFromItemContainer(this) as WidgetTiles;
        if (parent != null) return parent;

        DependencyObject? cur = this;
        while (cur != null)
        {
            if (cur is WidgetTiles wt) return wt;
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    static WidgetTile()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WidgetTile),
            new FrameworkPropertyMetadata(typeof(WidgetTile)));
    }
}
