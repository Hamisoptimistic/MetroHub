using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MetroHub.Widgets;

/// <summary>
/// Modular segmented toggle / tab bar with built-in Fluent sliding active indicator.
/// Inherits from <see cref="Selector"/> for seamless two-way MVVM binding (SelectedIndex, SelectedItem, SelectedValue).
/// Encapsulates all animation math and layout tracking so widget authors write 0 lines of code-behind.
/// </summary>
public class WidgetSegmentedControl : Selector
{
    public static readonly DependencyProperty IndicatorBrushProperty =
        DependencyProperty.Register(
            nameof(IndicatorBrush),
            typeof(Brush),
            typeof(WidgetSegmentedControl),
            new PropertyMetadata(new SolidColorBrush(Colors.White)));

    public static readonly DependencyProperty IndicatorHeightProperty =
        DependencyProperty.Register(
            nameof(IndicatorHeight),
            typeof(double),
            typeof(WidgetSegmentedControl),
            new PropertyMetadata(2.0));

    public static readonly DependencyProperty IndicatorCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(IndicatorCornerRadius),
            typeof(CornerRadius),
            typeof(WidgetSegmentedControl),
            new PropertyMetadata(new CornerRadius(1)));

    public static readonly DependencyProperty IndicatorShadowColorProperty =
        DependencyProperty.Register(
            nameof(IndicatorShadowColor),
            typeof(Color),
            typeof(WidgetSegmentedControl),
            new PropertyMetadata(Colors.White));

    public static readonly DependencyProperty IndicatorShadowBlurRadiusProperty =
        DependencyProperty.Register(
            nameof(IndicatorShadowBlurRadius),
            typeof(double),
            typeof(WidgetSegmentedControl),
            new PropertyMetadata(6.0));

    public static readonly DependencyProperty AnimationDurationMsProperty =
        DependencyProperty.Register(
            nameof(AnimationDurationMs),
            typeof(int),
            typeof(WidgetSegmentedControl),
            new PropertyMetadata(300));

    public Brush IndicatorBrush
    {
        get => (Brush)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

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

    public Color IndicatorShadowColor
    {
        get => (Color)GetValue(IndicatorShadowColorProperty);
        set => SetValue(IndicatorShadowColorProperty, value);
    }

    public double IndicatorShadowBlurRadius
    {
        get => (double)GetValue(IndicatorShadowBlurRadiusProperty);
        set => SetValue(IndicatorShadowBlurRadiusProperty, value);
    }

    public int AnimationDurationMs
    {
        get => (int)GetValue(AnimationDurationMsProperty);
        set => SetValue(AnimationDurationMsProperty, value);
    }

    private FrameworkElement? _hostGrid;
    private Border? _slidingIndicator;
    private TranslateTransform? _indicatorTransform;
    private bool _isInternalSync = false;

    static WidgetSegmentedControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WidgetSegmentedControl),
            new FrameworkPropertyMetadata(typeof(WidgetSegmentedControl)));

        SelectedValuePathProperty.OverrideMetadata(
            typeof(WidgetSegmentedControl),
            new FrameworkPropertyMetadata("Value"));
    }

    public WidgetSegmentedControl()
    {
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;

        var dpd = DependencyPropertyDescriptor.FromProperty(SelectedValueProperty, typeof(WidgetSegmentedControl));
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
        _slidingIndicator = GetTemplateChild("PART_SlidingIndicator") as Border;
        _indicatorTransform = GetTemplateChild("PART_IndicatorTransform") as TranslateTransform;

        UpdateIndicator(animate: false);
    }

    protected override bool IsItemItsOwnContainerOverride(object item)
    {
        return item is WidgetSegmentedItem;
    }

    protected override DependencyObject GetContainerForItemOverride()
    {
        return new WidgetSegmentedItem();
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
            if (idx >= 0 && idx < Items.Count && Items[idx] is WidgetSegmentedItem seg)
            {
                _isInternalSync = true;
                try
                {
                    SelectedValue = seg.Value;
                    for (int i = 0; i < Items.Count; i++)
                    {
                        if (Items[i] is WidgetSegmentedItem s)
                        {
                            s.IsSelected = (i == idx);
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

    internal void NotifyItemClicked(WidgetSegmentedItem item)
    {
        int index = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i] == item)
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
                SelectedItem = item;
                SelectedValue = item.Value;

                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i] is WidgetSegmentedItem seg)
                    {
                        seg.IsSelected = (i == index);
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
            if (Items[i] is WidgetSegmentedItem seg)
            {
                bool isMatch = Equals(seg.Value, val);
                if (isMatch)
                {
                    _isInternalSync = true;
                    try
                    {
                        SelectedIndex = i;
                        SelectedItem = seg;
                        for (int j = 0; j < Items.Count; j++)
                        {
                            if (Items[j] is WidgetSegmentedItem s)
                            {
                                s.IsSelected = (j == i);
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

    private void UpdateIndicator(bool animate)
    {
        if (_slidingIndicator == null || _indicatorTransform == null || _hostGrid == null) return;

        int index = SelectedIndex;
        if (index < 0)
        {
            _slidingIndicator.Opacity = 0;
            return;
        }

        int count = Items.Count;
        double hostWidth = _hostGrid.ActualWidth;

        double slotWidth = (count > 0 && hostWidth > 0) ? (hostWidth / count) : 0;
        double slotCenterX = (index + 0.5) * slotWidth;

        var container = ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (container == null && index >= 0 && index < Items.Count && Items[index] is FrameworkElement fe)
        {
            container = fe;
        }

        if (slotWidth <= 0 && container != null && container.ActualWidth > 0)
        {
            try
            {
                Point p = container.TranslatePoint(new Point(0, 0), _hostGrid);
                slotCenterX = p.X + (container.ActualWidth / 2.0);
            }
            catch
            {
                return;
            }
        }

        double textWidth = 0;
        if (container != null)
        {
            var cp = FindVisualChild<ContentPresenter>(container);
            if (cp != null && cp.ActualWidth > 0)
            {
                textWidth = cp.ActualWidth;
            }
        }

        // Fallback calculation using FormattedText if layout is pending
        if (textWidth <= 0 && index >= 0 && index < Items.Count)
        {
            string? text = null;
            if (Items[index] is HeaderedContentControl hcc && hcc.Content != null)
            {
                text = hcc.Content.ToString();
            }
            else if (Items[index] is WidgetSegmentedItem segItem && segItem.Content != null)
            {
                text = segItem.Content.ToString();
            }
            else if (Items[index] != null)
            {
                text = Items[index].ToString();
            }

            if (!string.IsNullOrEmpty(text))
            {
                var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var formatted = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    12.0,
                    Brushes.White,
                    VisualTreeHelper.GetDpi(_hostGrid).PixelsPerDip);
                textWidth = formatted.Width;
            }
        }

        if (textWidth <= 0)
        {
            textWidth = slotWidth > 0 ? (slotWidth * 0.45) : 24.0;
        }

        double targetWidth = Math.Round(textWidth);
        double targetX = Math.Round(slotCenterX - (targetWidth / 2.0));

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

            var wAnim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(AnimationDurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(wAnim, 120);
            _slidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, wAnim, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            _indicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _indicatorTransform.X = targetX;
            _slidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            _slidingIndicator.Width = targetWidth;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }
}
