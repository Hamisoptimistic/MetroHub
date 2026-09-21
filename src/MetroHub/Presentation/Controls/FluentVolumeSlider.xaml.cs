using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetroHub.Presentation.Controls;

public partial class FluentVolumeSlider : UserControl
{
    private static readonly SolidColorBrush DefaultProgressBrush = new((Color)ColorConverter.ConvertFromString("#00B4D8"));
    private static readonly SolidColorBrush DefaultPointerOverBrush = new((Color)ColorConverter.ConvertFromString("#33C9E8"));

    static FluentVolumeSlider()
    {
        DefaultProgressBrush.Freeze();
        DefaultPointerOverBrush.Freeze();
    }

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

    public static readonly DependencyProperty ProgressBrushProperty =
        DependencyProperty.Register(
            nameof(ProgressBrush),
            typeof(Brush),
            typeof(FluentVolumeSlider),
            new PropertyMetadata(null, OnProgressBrushChanged));

    public Brush? ProgressBrush
    {
        get => (Brush?)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public static readonly DependencyProperty ProgressPointerOverBrushProperty =
        DependencyProperty.Register(
            nameof(ProgressPointerOverBrush),
            typeof(Brush),
            typeof(FluentVolumeSlider),
            new PropertyMetadata(null));

    public Brush? ProgressPointerOverBrush
    {
        get => (Brush?)GetValue(ProgressPointerOverBrushProperty);
        set => SetValue(ProgressPointerOverBrushProperty, value);
    }

    public static readonly DependencyProperty IsDraggingProperty =
        DependencyProperty.Register(
            nameof(IsDragging),
            typeof(bool),
            typeof(FluentVolumeSlider),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public bool IsDragging
    {
        get => (bool)GetValue(IsDraggingProperty);
        set => SetValue(IsDraggingProperty, value);
    }

    private bool _isDragging;
    private System.Windows.Threading.DispatcherTimer? _dragReleaseTimer;

    public FluentVolumeSlider()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            ApplyProgressBrush();
            UpdateVisuals();
        };
        IsEnabledChanged += (s, e) =>
        {
            Opacity = IsEnabled ? 1.0 : 0.4;
        };
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

    private static void OnProgressBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FluentVolumeSlider slider)
        {
            slider.ApplyProgressBrush();
        }
    }

    /// <summary>
    /// Applies the ProgressBrush (or system accent fallback) to the fill track and thumb pip.
    /// </summary>
    private void ApplyProgressBrush()
    {
        var brush = ProgressBrush ?? DefaultProgressBrush;

        if (ProgressFill != null) ProgressFill.Background = brush;
        if (ThumbDot != null) ThumbDot.Fill = brush;
    }

    private void UpdateMutedVisuals()
    {
        if (ProgressFill != null) ProgressFill.Opacity = IsMuted ? 0.35 : 1.0;
        if (SliderThumb != null)
        {
            SliderThumb.Opacity = (_isDragging || IsMouseOver) ? (IsMuted ? 0.45 : 1.0) : 0.0;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        if (RootContainer == null || ProgressClip == null || ThumbTranslate == null) return;

        double width = RootContainer.ActualWidth;
        if (width <= 0) return;

        double range = Maximum - Minimum;
        if (range <= 0) range = 1.0;

        double ratio = Math.Clamp((Value - Minimum) / range, 0.0, 1.0);

        ProgressClip.Rect = new Rect(0, -10, ratio * width, 40);

        const double thumbWidth = 12.0;
        double maxThumbLeft = Math.Max(0, width - thumbWidth);
        double thumbLeft = ratio * maxThumbLeft;

        ThumbTranslate.X = thumbLeft;
    }

    private void UpdateValueFromPosition(Point pos)
    {
        double width = RootContainer.ActualWidth;
        if (width <= 0) return;

        const double thumbWidth = 12.0;
        double maxThumbLeft = width - thumbWidth;
        double ratio;
        if (maxThumbLeft > 0)
        {
            ratio = Math.Clamp((pos.X - (thumbWidth / 2.0)) / maxThumbLeft, 0.0, 1.0);
        }
        else
        {
            ratio = Math.Clamp(pos.X / width, 0.0, 1.0);
        }

        double range = Maximum - Minimum;
        double newVal = Minimum + (ratio * range);

        Value = Math.Round(newVal, 1);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Intercept tunneling mouse down event to completely prevent parent tile activation/drag
        e.Handled = true;

        _dragReleaseTimer?.Stop();
        _isDragging = true;
        IsDragging = true;
        RootContainer.CaptureMouse();
        UpdateHoverState(true, isDragging: true);

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
            UpdateHoverState(isMouseStillOver, isDragging: false);
            ScheduleDragRelease(650);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double step = (Maximum - Minimum) * 0.02; // 2% step
        if (step <= 0) step = 1.0;

        double delta = e.Delta > 0 ? step : -step;
        Value = Math.Clamp(Value + delta, Minimum, Maximum);

        // Flash percentage temporarily on wheel scroll as well
        IsDragging = true;
        ScheduleDragRelease(750);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            UpdateHoverState(true, isDragging: false);
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            UpdateHoverState(false, isDragging: false);
        }
    }

    private void ScheduleDragRelease(int milliseconds)
    {
        _dragReleaseTimer?.Stop();
        _dragReleaseTimer ??= new System.Windows.Threading.DispatcherTimer();
        _dragReleaseTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _dragReleaseTimer.Tick -= OnDragReleaseTimerTick;
        _dragReleaseTimer.Tick += OnDragReleaseTimerTick;
        _dragReleaseTimer.Start();
    }

    private void OnDragReleaseTimerTick(object? sender, EventArgs e)
    {
        _dragReleaseTimer?.Stop();
        IsDragging = false;
    }

    private void UpdateHoverState(bool isHovered, bool isDragging)
    {
        if (SliderThumb != null)
        {
            SliderThumb.Opacity = (isHovered || isDragging) ? (IsMuted ? 0.45 : 1.0) : 0.0;
        }

        if (ThumbDot != null)
        {
            ThumbDot.Fill = (isHovered || isDragging)
                ? (ProgressPointerOverBrush ?? GetEffectivePointerOverBrush())
                : (ProgressBrush ?? DefaultProgressBrush);
        }
    }

    private Brush GetEffectivePointerOverBrush()
    {
        if (ProgressPointerOverBrush != null) return ProgressPointerOverBrush;
        if (ProgressBrush is SolidColorBrush scb)
        {
            Color c = scb.Color;
            byte r = (byte)Math.Min(255, c.R + 40);
            byte g = (byte)Math.Min(255, c.G + 40);
            byte b = (byte)Math.Min(255, c.B + 40);
            return new SolidColorBrush(Color.FromArgb(c.A, r, g, b));
        }
        return DefaultPointerOverBrush;
    }
}
