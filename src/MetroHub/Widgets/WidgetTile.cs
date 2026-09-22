using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets;

/// <summary>
/// An individual selectable tile item within a <see cref="WidgetTiles"/> strip.
/// Displays an icon, header label, and provides its own dynamic <see cref="IndicatorBrush"/>.
/// Standardized on the 24px Fluent System Icons grid.
/// </summary>
public class WidgetTile : ListBoxItem
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(object),
            typeof(WidgetTile),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(
            nameof(Symbol),
            typeof(SymbolRegular?),
            typeof(WidgetTile),
            new PropertyMetadata(null, OnSymbolChanged));

    private static void OnSymbolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetTile tile)
        {
            tile.SetValue(HasSymbolPropertyKey, e.NewValue != null);
        }
    }

    private static readonly DependencyPropertyKey HasSymbolPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasSymbol),
            typeof(bool),
            typeof(WidgetTile),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasSymbolProperty =
        HasSymbolPropertyKey.DependencyProperty;

    public bool HasSymbol => (bool)GetValue(HasSymbolProperty);

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(WidgetTile),
            new PropertyMetadata(24.0));

    public SymbolRegular? Symbol
    {
        get => (SymbolRegular?)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

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
                DependencyObject? current = tile;
                while (current != null && parent == null)
                {
                    current = GetParent(current);
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

    public static readonly RoutedEvent ClickEvent =
        EventManager.RegisterRoutedEvent(
            nameof(Click),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(WidgetTile));

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
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
            RaiseEvent(new RoutedEventArgs(ClickEvent, this));
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        SetValue(IsPressedPropertyKey, false);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        SetValue(IsPressedPropertyKey, false);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            FindParentControl()?.NotifyTileClicked(this);
            RaiseEvent(new RoutedEventArgs(ClickEvent, this));
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
            cur = GetParent(cur);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject? node)
    {
        if (node == null) return null;
        if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
        {
            var parent = VisualTreeHelper.GetParent(node);
            if (parent != null) return parent;
        }
        if (node is FrameworkElement fe)
            return fe.Parent ?? fe.TemplatedParent ?? LogicalTreeHelper.GetParent(node);
        if (node is FrameworkContentElement fce)
            return fce.Parent ?? fce.TemplatedParent ?? LogicalTreeHelper.GetParent(node);
        return LogicalTreeHelper.GetParent(node);
    }

    static WidgetTile()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WidgetTile),
            new FrameworkPropertyMetadata(typeof(WidgetTile)));
    }
}
