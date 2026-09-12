using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Presentation.Controls;

public partial class FluentVolumeSlider : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(FluentVolumeSlider),
            new FrameworkPropertyMetadata(50.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum),
            typeof(double),
            typeof(FluentVolumeSlider),
            new PropertyMetadata(0.0, OnMinMaxChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(FluentVolumeSlider),
            new PropertyMetadata(100.0, OnMinMaxChanged));

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(
            nameof(IsMuted),
            typeof(bool),
            typeof(FluentVolumeSlider),
            new PropertyMetadata(false, OnIsMutedChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    private bool _isDragging;

    public FluentVolumeSlider()
    {
        InitializeComponent();
        Loaded += (s, e) => UpdateVisuals();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FluentVolumeSlider slider)
        {
            slider.UpdateVisuals();
        }
    }

    private static void OnMinMaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FluentVolumeSlider slider)
        {
            slider.UpdateVisuals();
        }
    }

    private static void OnIsMutedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FluentVolumeSlider slider)
        {
            slider.UpdateMutedVisuals();
        }
    }

    private void UpdateMutedVisuals()
    {
        ProgressFill.Opacity = IsMuted ? 0.35 : 1.0;
        SliderThumb.Opacity = IsMuted ? 0.45 : (_isDragging || IsMouseOver ? 1.0 : 0.0);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (RootContainer == null || ProgressFill == null || SliderThumb == null) return;

        double width = RootContainer.ActualWidth;
        if (width <= 0) return;

        double range = Maximum - Minimum;
        if (range <= 0) range = 1.0;

        double ratio = Math.Clamp((Value - Minimum) / range, 0.0, 1.0);

        ProgressFill.Width = ratio * width;

        double thumbWidth = SliderThumb.ActualWidth > 0 ? SliderThumb.ActualWidth : 5;
        double maxThumbLeft = Math.Max(0, width - thumbWidth);
        double thumbLeft = ratio * maxThumbLeft;

        SliderThumb.Margin = new Thickness(thumbLeft, 0, 0, 0);

        if (FloatingTooltip != null && TooltipText != null && TooltipTranslate != null)
        {
            TooltipText.Text = $"{Math.Round(Value)}%";
            double tooltipWidth = FloatingTooltip.ActualWidth > 0 ? FloatingTooltip.ActualWidth : 36.0;
            double thumbCenter = thumbLeft + (thumbWidth / 2.0);
            double targetX = Math.Clamp(thumbCenter - (tooltipWidth / 2.0), 0.0, Math.Max(0, width - tooltipWidth));
            TooltipTranslate.X = targetX;
        }
    }

    private void UpdateValueFromPosition(Point pos)
    {
        double width = RootContainer.ActualWidth;
        if (width <= 0) return;

        double ratio = Math.Clamp(pos.X / width, 0.0, 1.0);
        double range = Maximum - Minimum;
        double newVal = Minimum + (ratio * range);

        Value = Math.Round(newVal, 1);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Intercept tunneling mouse down event to completely prevent parent tile activation/drag
        e.Handled = true;

        _isDragging = true;
        RootContainer.CaptureMouse();
        AnimateHoverState(true, isDragging: true);

        Point pt = e.GetPosition(RootContainer);
        UpdateValueFromPosition(pt);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            e.Handled = true;
            Point pt = e.GetPosition(RootContainer);
            UpdateValueFromPosition(pt);
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            e.Handled = true;
            _isDragging = false;
            RootContainer.ReleaseMouseCapture();

            bool isMouseStillOver = RootContainer.IsMouseOver;
            AnimateHoverState(isMouseStillOver, isDragging: false);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double step = (Maximum - Minimum) * 0.02; // 2% step
        if (step <= 0) step = 1.0;

        double delta = e.Delta > 0 ? step : -step;
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            AnimateHoverState(true, isDragging: false);
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            AnimateHoverState(false, isDragging: false);
        }
    }

    private void AnimateHoverState(bool isHovered, bool isDragging)
    {
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 1. Thumb Opacity: 0 when resting, 1.0 when hovered or scrubbing
        double targetOpacity = (isHovered || isDragging) ? (IsMuted ? 0.45 : 1.0) : 0.0;
        var thumbOpacityAnim = new DoubleAnimation(targetOpacity, duration) { EasingFunction = easing };
        SliderThumb.BeginAnimation(OpacityProperty, thumbOpacityAnim);

        // 2. Vertical Pill Thumb Scale: 0.6x0.7 resting -> 1.0x1.0 hover -> 1.2x1.1 dragging
        double targetScaleX = isDragging ? 1.2 : (isHovered ? 1.0 : 0.6);
        double targetScaleY = isDragging ? 1.1 : (isHovered ? 1.0 : 0.7);
        var scaleXAnim = new DoubleAnimation(targetScaleX, duration) { EasingFunction = easing };
        var scaleYAnim = new DoubleAnimation(targetScaleY, duration) { EasingFunction = easing };
        ThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        ThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

        // 3. Track Height: 3px resting -> 4.5px on hover/drag
        double targetHeight = (isHovered || isDragging) ? 4.5 : 3.0;
        var trackHeightAnim = new DoubleAnimation(targetHeight, duration) { EasingFunction = easing };
        TrackTrough?.BeginAnimation(HeightProperty, trackHeightAnim);
        TrackBg?.BeginAnimation(HeightProperty, trackHeightAnim);
        ProgressFill?.BeginAnimation(HeightProperty, trackHeightAnim);

        // 4. Floating Drag Tooltip: 1.0 ONLY while actively dragging, 0 when released or merely hovered
        if (FloatingTooltip != null)
        {
            double targetTooltipOpacity = isDragging ? 1.0 : 0.0;
            var tooltipAnim = new DoubleAnimation(targetTooltipOpacity, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = easing
            };
            FloatingTooltip.BeginAnimation(OpacityProperty, tooltipAnim);
        }
    }
}
