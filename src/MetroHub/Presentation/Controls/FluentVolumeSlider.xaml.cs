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
        SliderThumb.Opacity = IsMuted ? 0.45 : 0.85;
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

        double thumbWidth = SliderThumb.ActualWidth > 0 ? SliderThumb.ActualWidth : 6;
        double maxThumbLeft = Math.Max(0, width - thumbWidth);
        double thumbLeft = ratio * maxThumbLeft;

        SliderThumb.Margin = new Thickness(thumbLeft, 0, 0, 0);
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
        // Suppress tile drag / activation
        e.Handled = true;

        _isDragging = true;
        RootContainer.CaptureMouse();
        AnimateThumbScale(1.3, 1.2, 0.95);

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

            if (RootContainer.IsMouseOver)
            {
                AnimateThumbScale(1.15, 1.1, 0.9);
            }
            else
            {
                AnimateThumbScale(1.0, 1.0, IsMuted ? 0.45 : 0.85);
            }
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
            AnimateThumbScale(1.15, 1.1, 0.9);
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            AnimateThumbScale(1.0, 1.0, IsMuted ? 0.45 : 0.85);
        }
    }

    private void AnimateThumbScale(double scaleX, double scaleY, double opacity)
    {
        if (ThumbScale == null || SliderThumb == null) return;

        var animX = new DoubleAnimation(scaleX, TimeSpan.FromMilliseconds(100));
        var animY = new DoubleAnimation(scaleY, TimeSpan.FromMilliseconds(100));
        var animO = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(100));

        ThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        ThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        SliderThumb.BeginAnimation(OpacityProperty, animO);
    }
}
