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
            new PropertyMetadata(150));

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
        if (val == null || Items.Count == 0) return;

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
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(100));
            _bottomRevealBorder.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    private void OnTilesMouseLeave(object sender, MouseEventArgs e)
    {
        if (_bottomRevealBorder != null)
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200));
            _bottomRevealBorder.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    private static readonly Color DefaultIndicatorColor = Color.FromRgb(0x00, 0xE6, 0x76);
    private static readonly SolidColorBrush DefaultIndicatorBrush = CreateFrozenBrush(DefaultIndicatorColor);

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private void UpdateIndicator(bool animate = true)
    {
        if (_slidingIndicator == null || _indicatorTransform == null || _hostGrid == null) return;

        int index = SelectedIndex;
        if (index < 0 || index >= Items.Count)
        {
            // Fade out indicator when no item is selected
            if (_slidingIndicator.Opacity > 0.0)
            {
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(AnimationDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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
            var xAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(AnimationDurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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
            Color targetColor = (brush as SolidColorBrush)?.Color ?? (Color)ColorConverter.ConvertFromString("#00E676");
            _indicatorShadow.Color = targetColor;
        }
        _slidingIndicator.InvalidateVisual();
    }
}
