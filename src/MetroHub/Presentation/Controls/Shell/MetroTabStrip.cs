using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MetroHub.Core.Services;
using MetroHub.Presentation.Themes;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Reusable Metro tab strip control with clean text tabs and a smooth, animated active indicator with glow effect.
/// </summary>
[TemplatePart(Name = PartHostGrid, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartSlidingIndicator, Type = typeof(Border))]
[TemplatePart(Name = PartIndicatorTransform, Type = typeof(TranslateTransform))]
[TemplatePart(Name = PartIndicatorShadow, Type = typeof(DropShadowEffect))]
public class MetroTabStrip : ListBox
{
    private const string PartHostGrid = "PART_HostGrid";
    private const string PartSlidingIndicator = "PART_SlidingIndicator";
    private const string PartIndicatorTransform = "PART_IndicatorTransform";
    private const string PartIndicatorShadow = "PART_IndicatorShadow";

    public static readonly DependencyProperty IndicatorHeightProperty =
        DependencyProperty.Register(
            nameof(IndicatorHeight),
            typeof(double),
            typeof(MetroTabStrip),
            new PropertyMetadata(2.0));

    public static readonly DependencyProperty IndicatorCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(IndicatorCornerRadius),
            typeof(CornerRadius),
            typeof(MetroTabStrip),
            new PropertyMetadata(new CornerRadius(1)));

    public static readonly DependencyProperty AnimationDurationMsProperty =
        DependencyProperty.Register(
            nameof(AnimationDurationMs),
            typeof(int),
            typeof(MetroTabStrip),
            new PropertyMetadata((int)MotionTokens.DurationNormal.TotalMilliseconds));

    public static readonly DependencyProperty IndicatorBrushProperty =
        DependencyProperty.Register(
            nameof(IndicatorBrush),
            typeof(Brush),
            typeof(MetroTabStrip),
            new PropertyMetadata(null, (d, _) => ((MetroTabStrip)d).UpdateIndicatorBrush()));

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

    public Brush? IndicatorBrush
    {
        get => (Brush?)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    private FrameworkElement? _hostGrid;
    private Border? _slidingIndicator;
    private TranslateTransform? _indicatorTransform;
    private DropShadowEffect? _indicatorShadow;

    private bool _isTransitioning;
    private bool _pendingLayoutRefresh;
    private long _updateGeneration;

    static MetroTabStrip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MetroTabStrip),
            new FrameworkPropertyMetadata(typeof(MetroTabStrip)));
    }

    public MetroTabStrip()
    {
        Loaded += (s, e) => UpdateIndicator(false);
        Unloaded += (s, e) =>
        {
            _isTransitioning = false;
            _pendingLayoutRefresh = false;
            _updateGeneration++;
        };
        SizeChanged += OnSizeChanged;
        ItemContainerGenerator.StatusChanged += (s, e) =>
        {
            if (ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated && !_isTransitioning)
            {
                UpdateIndicator(false);
            }
        };
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isTransitioning)
        {
            _pendingLayoutRefresh = true;
            return;
        }

        UpdateIndicator(false);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _hostGrid = GetTemplateChild(PartHostGrid) as FrameworkElement;
        _slidingIndicator = GetTemplateChild(PartSlidingIndicator) as Border;
        _indicatorTransform = GetTemplateChild(PartIndicatorTransform) as TranslateTransform
                              ?? _slidingIndicator?.RenderTransform as TranslateTransform;
        _indicatorShadow = GetTemplateChild(PartIndicatorShadow) as DropShadowEffect
                           ?? _slidingIndicator?.Effect as DropShadowEffect;

        if (_slidingIndicator != null && (_indicatorTransform == null || _indicatorTransform.IsFrozen))
        {
            _indicatorTransform = new TranslateTransform();
            _slidingIndicator.RenderTransform = _indicatorTransform;
        }

        UpdateIndicator(false);
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UpdateIndicator(true);
    }

    private void UpdateIndicator(bool animate = true)
    {
        if (_slidingIndicator == null || _indicatorTransform == null || _hostGrid == null)
        {
            return;
        }

        long currentGen = ++_updateGeneration;
        int index = SelectedIndex;

        if (index < 0 || index >= Items.Count)
        {
            if (_slidingIndicator.Opacity <= 0.0)
            {
                return;
            }

            // Do not fade out immediately: tab switches (e.g. TwoWay bindings where old tab is unselected
            // before new tab is selected) momentarily drop SelectedIndex to -1.
            // Defer the fade-out check to allow the new selection to arrive in the same dispatch cycle.
            Dispatcher.InvokeAsync(() =>
            {
                if (currentGen != _updateGeneration) return;
                if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
                {
                    PerformFadeOut(animate);
                }
            }, DispatcherPriority.Input).FireAndForget("MetroTabStrip.PerformFadeOut");
            return;
        }

        if (ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
            container.ActualWidth <= 0 ||
            !container.IsDescendantOf(_hostGrid))
        {
            // Container not ready or not measured yet. Defer update until layout completes, preserving animation mode.
            Dispatcher.InvokeAsync(() => UpdateIndicator(animate), DispatcherPriority.Loaded)
                      .FireAndForget("MetroTabStrip.UpdateIndicator");
            return;
        }

        var point = container.TranslatePoint(new Point(0, 0), _hostGrid);
        double targetX = point.X;
        double targetWidth = container.ActualWidth;

        UpdateIndicatorBrush();

        // Ensure indicator is visible when a valid tab is selected
        if (_slidingIndicator.Opacity < 1.0)
        {
            if (animate && MotionTokens.AnimationsEnabled)
            {
                var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(AnimationDurationMs))
                {
                    EasingFunction = MotionTokens.Decelerate
                };
                long fadeInGen = currentGen;
                fadeIn.Completed += (_, _) =>
                {
                    if (fadeInGen != _updateGeneration) return;
                    _slidingIndicator.Opacity = 1.0;
                    _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                };
                _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
            else
            {
                _slidingIndicator.Opacity = 1.0;
                _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            }
        }

        TimeSpan duration = (!MotionTokens.AnimationsEnabled || !animate)
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(AnimationDurationMs);

        if (animate && MotionTokens.AnimationsEnabled && duration > TimeSpan.Zero)
        {
            _isTransitioning = true;

            var xAnim = new DoubleAnimation(targetX, duration)
            {
                EasingFunction = MotionTokens.Decelerate
            };
            var widthAnim = new DoubleAnimation(targetWidth, duration)
            {
                EasingFunction = MotionTokens.Decelerate
            };

            long animGen = currentGen;
            xAnim.Completed += (_, _) =>
            {
                if (animGen != _updateGeneration) return;

                _isTransitioning = false;
                _indicatorTransform.X = targetX;
                _slidingIndicator.Width = targetWidth;
                _indicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _slidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);

                if (_pendingLayoutRefresh)
                {
                    _pendingLayoutRefresh = false;
                    UpdateIndicator(false);
                }
            };

            _indicatorTransform.BeginAnimation(TranslateTransform.XProperty, xAnim, HandoffBehavior.SnapshotAndReplace);
            _slidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            _isTransitioning = false;
            _indicatorTransform.X = targetX;
            _slidingIndicator.Width = targetWidth;
            _indicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _slidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
        }
    }

    private void PerformFadeOut(bool animate)
    {
        if (_slidingIndicator == null) return;

        _isTransitioning = false;
        if (animate && MotionTokens.AnimationsEnabled)
        {
            var fadeDuration = TimeSpan.FromMilliseconds(AnimationDurationMs);
            var fadeOut = new DoubleAnimation(0.0, fadeDuration)
            {
                EasingFunction = MotionTokens.Decelerate
            };
            long fadeGen = _updateGeneration;
            fadeOut.Completed += (_, _) =>
            {
                if (fadeGen != _updateGeneration) return;
                if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
                {
                    _slidingIndicator.Opacity = 0.0;
                    _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                }
            };
            _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        else
        {
            _slidingIndicator.Opacity = 0.0;
            _slidingIndicator.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }

    private void UpdateIndicatorBrush()
    {
        if (_slidingIndicator == null) return;
        Brush resolvedBrush = IndicatorBrush ?? (TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White);
        _slidingIndicator.Background = resolvedBrush;

        if (_indicatorShadow != null)
        {
            Color shadowColor = (resolvedBrush as SolidColorBrush)?.Color ?? Colors.White;
            _indicatorShadow.Color = shadowColor;
        }
    }
}
