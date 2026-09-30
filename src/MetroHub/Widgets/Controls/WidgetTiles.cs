using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MetroHub.Presentation.Themes;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets;

/// <summary>
/// Modular top tile strip for widgets with continuous Fluent border reveal and hardware-accelerated sliding active indicator.
/// Inherits from <see cref="Selector"/> for seamless two-way MVVM binding (SelectedValue, SelectedItem, SelectedIndex).
/// </summary>
public class WidgetTiles : Selector
{
    public static readonly DependencyProperty IndicatorHeightProperty =
        DependencyProperty.Register(
            nameof(IndicatorHeight),
            typeof(double),
            typeof(WidgetTiles),
            new PropertyMetadata(2.0));

    public static readonly DependencyProperty IndicatorCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(IndicatorCornerRadius),
            typeof(CornerRadius),
            typeof(WidgetTiles),
            new PropertyMetadata(new CornerRadius(0)));

    public static readonly DependencyProperty AnimationDurationMsProperty =
        DependencyProperty.Register(
            nameof(AnimationDurationMs),
            typeof(int),
            typeof(WidgetTiles),
            new PropertyMetadata(200));

    public double IndicatorHeight
    {
        get => (double)GetValue(IndicatorHeightProperty);
        set => SetValue(IndicatorHeightProperty, value);
    }

    public CornerRadius IndicatorCornerRadius
    {
        get => (CornerRadius)GetValue(IndicatorCornerRadiusProperty);
        set => SetValue(IndicatorCornerRadiusProperty, value);
    }

    public int AnimationDurationMs
    {
        get => (int)GetValue(AnimationDurationMsProperty);
        set => SetValue(AnimationDurationMsProperty, value);
    }

    private FrameworkElement? _hostGrid;
    private Border? _bottomRevealBorder;
    private RadialGradientBrush? _bottomRevealBrush;
    private Border? _slidingIndicator;
    private TranslateTransform? _indicatorTransform;
    private DropShadowEffect? _indicatorShadow;
    private bool _isInternalSync = false;

    static WidgetTiles()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WidgetTiles),
            new FrameworkPropertyMetadata(typeof(WidgetTiles)));

        SelectedValuePathProperty.OverrideMetadata(
            typeof(WidgetTiles),
            new FrameworkPropertyMetadata("Value"));
    }

    public WidgetTiles()
    {
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        MouseMove += OnTilesMouseMove;
        MouseEnter += OnTilesMouseEnter;
        MouseLeave += OnTilesMouseLeave;

        var dpd = DependencyPropertyDescriptor.FromProperty(SelectedValueProperty, typeof(WidgetTiles));
        dpd?.AddValueChanged(this, (s, e) =>
        {
            if (!_isInternalSync)
            {
                SyncSelectionFromValue(SelectedValue);
            }
        });
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _hostGrid = GetTemplateChild("PART_HostGrid") as FrameworkElement;
        _bottomRevealBorder = GetTemplateChild("PART_BottomRevealBorder") as Border;
        _bottomRevealBrush = GetTemplateChild("PART_BottomRevealBrush") as RadialGradientBrush;
        _slidingIndicator = GetTemplateChild("PART_SlidingIndicator") as Border;
        _indicatorTransform = GetTemplateChild("PART_IndicatorTransform") as TranslateTransform;
        _indicatorShadow = GetTemplateChild("PART_IndicatorShadow") as DropShadowEffect;

        UpdateIndicator(animate: false);
    }

    protected override bool IsItemItsOwnContainerOverride(object item)
    {
        return item is WidgetTile;
    }

    protected override DependencyObject GetContainerForItemOverride()
    {
        return new WidgetTile();
    }

    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (!_isInternalSync && SelectedValue != null)
        {
            SyncSelectionFromValue(SelectedValue);
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);

        if (!_isInternalSync)
        {
            int idx = SelectedIndex;
            if (idx >= 0 && idx < Items.Count && Items[idx] is WidgetTile tile)
            {
                _isInternalSync = true;
                try
                {
                    SelectedValue = tile.Value;
                    for (int i = 0; i < Items.Count; i++)
                    {
                        if (Items[i] is WidgetTile t)
                        {
                            t.IsSelected = (i == idx);
                        }
                    }
                }
                finally
                {
                    _isInternalSync = false;
                }
            }
        }

        UpdateIndicator(animate: IsLoaded);
    }

    internal void NotifyTileClicked(WidgetTile tile)
    {
        int index = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i] == tile)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            _isInternalSync = true;
            try
            {
                SelectedIndex = index;
                SelectedItem = tile;
                SelectedValue = tile.Value;

                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i] is WidgetTile t)
                    {
                        t.IsSelected = (i == index);
                    }
                }
            }
            finally
            {
                _isInternalSync = false;
            }

            UpdateIndicator(animate: true);
        }
    }

    private void SyncSelectionFromValue(object? val)
    {
        if (Items.Count == 0) return;

        if (val == null)
        {
            _isInternalSync = true;
            try
            {
                SelectedIndex = -1;
                SelectedItem = null;
                for (int j = 0; j < Items.Count; j++)
                {
                    if (Items[j] is WidgetTile t)
                    {
                        t.IsSelected = false;
                    }
                }
            }
            finally
            {
                _isInternalSync = false;
            }

            UpdateIndicator(animate: IsLoaded);
            return;
        }

        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i] is WidgetTile tile)
            {
                bool isMatch = Equals(tile.Value, val);
                if (isMatch)
                {
                    _isInternalSync = true;
                    try
                    {
                        SelectedIndex = i;
                        SelectedItem = tile;
                        for (int j = 0; j < Items.Count; j++)
                        {
                            if (Items[j] is WidgetTile t)
                            {
                                t.IsSelected = (j == i);
                            }
                        }
                    }
                    finally
                    {
                        _isInternalSync = false;
                    }

                    UpdateIndicator(animate: IsLoaded);
                    return;
                }
            }
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIndicator(animate: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncSelectionFromValue(SelectedValue);
        Dispatcher.InvokeAsync(() => UpdateIndicator(animate: false), DispatcherPriority.Loaded);
    }

    private void OnTilesMouseMove(object sender, MouseEventArgs e)
    {
        if (_bottomRevealBrush != null)
        {
            Point pos = e.GetPosition(this);
            _bottomRevealBrush.Center = pos;
            _bottomRevealBrush.GradientOrigin = pos;
        }
    }

    private void OnTilesMouseEnter(object sender, MouseEventArgs e)
    {
        if (_bottomRevealBorder != null)
        {
            var anim = new DoubleAnimation(1.0, MotionTokens.DurationFast);
            _bottomRevealBorder.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    private void OnTilesMouseLeave(object sender, MouseEventArgs e)
    {
        if (_bottomRevealBorder != null)
        {
            var anim = new DoubleAnimation(0.0, MotionTokens.DurationNormal);
            _bottomRevealBorder.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    private static readonly Color DefaultIndicatorColor = ThemeTokens.StatusSuccessColor;
    private static readonly SolidColorBrush DefaultIndicatorBrush = ThemeTokens.StatusSuccessBrush;

    private void UpdateIndicator(bool animate = true)
    {
        if (_slidingIndicator == null || _indicatorTransform == null || _hostGrid == null) return;

        int index = SelectedIndex;
        if (index < 0 || index >= Items.Count)
        {
            // Fade out indicator when no item is selected
            if (_slidingIndicator.Opacity > 0.0)
            {
                var duration = !MotionTokens.AnimationsEnabled ? TimeSpan.Zero : TimeSpan.FromMilliseconds(AnimationDurationMs);
                var fadeOut = new DoubleAnimation(0.0, duration)
                {
                    EasingFunction = MotionTokens.Decelerate
                };
                _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            return;
        }

        // Measure item bounds relative to container canvas
        double targetX = 0;
        double targetWidth = 0;

        if (ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            if (container.ActualWidth > 0)
            {
                var point = container.TranslatePoint(new Point(0, 0), _hostGrid);
                targetX = point.X;
                targetWidth = container.ActualWidth;
            }
            else
            {
                Dispatcher.InvokeAsync(() => UpdateIndicator(false), DispatcherPriority.Loaded);
                return;
            }
        }

        // Determine active tile's indicator color
        var activeTile = Items[index] as WidgetTile;
        Brush targetBrush = activeTile?.IndicatorBrush ?? DefaultIndicatorBrush;
        Color targetColor = (targetBrush as SolidColorBrush)?.Color ?? DefaultIndicatorColor;

        // Keep width static to eliminate layout passes during translation
        if (Math.Abs(_slidingIndicator.Width - targetWidth) > 0.5)
        {
            _slidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            _slidingIndicator.Width = targetWidth;
        }

        if (_slidingIndicator.Opacity < 1.0)
        {
            _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            _slidingIndicator.Opacity = 1.0;
        }

        if (animate)
        {
            var duration = !MotionTokens.AnimationsEnabled ? TimeSpan.Zero : TimeSpan.FromMilliseconds(AnimationDurationMs);
            var xAnim = new DoubleAnimation(targetX, duration)
            {
                EasingFunction = MotionTokens.Decelerate
            };
            Timeline.SetDesiredFrameRate(xAnim, 120);

            _indicatorTransform.BeginAnimation(TranslateTransform.XProperty, xAnim, HandoffBehavior.SnapshotAndReplace);

            // Smooth color transition
            _slidingIndicator.Background = targetBrush;
            if (_indicatorShadow != null)
            {
                _indicatorShadow.Color = targetColor;
            }
        }
        else
        {
            _indicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _indicatorTransform.X = targetX;
            _slidingIndicator.Background = targetBrush;
            if (_indicatorShadow != null)
            {
                _indicatorShadow.Color = targetColor;
            }
        }
    }

    internal void UpdateIndicatorBrush(Brush? brush)
    {
        if (_slidingIndicator == null || brush == null) return;
        _slidingIndicator.Background = brush;
        if (_indicatorShadow != null)
        {
            Color targetColor = (brush as SolidColorBrush)?.Color ?? ThemeTokens.StatusSuccessColor;
            _indicatorShadow.Color = targetColor;
        }
        _slidingIndicator.InvalidateVisual();
    }
}

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
                ThemeTokens.StatusSuccessBrush,
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
