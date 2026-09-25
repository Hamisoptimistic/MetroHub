using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MetroHub.Widgets.Catalog.Media;

namespace MetroHub.Widgets.Catalog.QuickControls;

public partial class QuickControlsWidgetView : UserControl
{
    private bool _isDragging = false;
    private QuickControlsWidgetViewModel? _vm;
    private double _lastDragRatio;
    private System.Windows.Threading.DispatcherTimer? _shimmerDelayTimer;
    private bool _isShimmerSweeping;

    public QuickControlsWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is QuickControlsWidgetViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopShimmerAnimation();
        DetachViewModel();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateShimmerAnimation();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (e.NewValue is QuickControlsWidgetViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void AttachViewModel(QuickControlsWidgetViewModel vm)
    {
        _vm = vm;
        if (_vm.Media != null)
        {
            _vm.Media.PropertyChanged += OnMediaPropertyChanged;
            UpdateProgressVisuals(_vm.Media.ProgressRatio);
            UpdateShimmerAnimation();
        }
    }

    private void DetachViewModel()
    {
        if (_vm?.Media != null)
        {
            _vm.Media.PropertyChanged -= OnMediaPropertyChanged;
        }
        _vm = null;
    }

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDragging) return;

        if (e.PropertyName is nameof(MediaWidgetViewModel.ProgressRatio)
                           or nameof(MediaWidgetViewModel.PositionSeconds)
                           or nameof(MediaWidgetViewModel.DurationSeconds)
                           or nameof(MediaWidgetViewModel.IsPlaying)
                           or nameof(MediaWidgetViewModel.IsPlaybackStalled))
        {
            if (_vm?.Media != null)
            {
                UpdateProgressVisuals(_vm.Media.ProgressRatio);
                UpdateShimmerAnimation();
            }
        }
        else if (e.PropertyName == nameof(MediaWidgetViewModel.HasMedia) && _vm?.Media != null)
        {
            if (!_vm.Media.HasMedia)
            {
                UpdateProgressVisuals(0);
            }
            UpdateShimmerAnimation();
        }
        else if (e.PropertyName is nameof(MediaWidgetViewModel.IsLive) or nameof(MediaWidgetViewModel.CanSeek))
        {
            if (_vm?.Media != null && (_vm.Media.IsLive || !_vm.Media.CanSeek))
            {
                UpdateProgressVisuals(0);
                AnimateHoverState(false, false);
            }
            else if (_vm?.Media != null)
            {
                UpdateProgressVisuals(_vm.Media.ProgressRatio);
            }
            UpdateShimmerAnimation();
        }
    }

    private void UpdateProgressVisuals(double progressRatio)
    {
        if (MediaSeekbarContainer == null || MediaSeekbarContainer.ActualWidth <= 0) return;

        double totalWidth = MediaSeekbarContainer.ActualWidth;
        double ratio = Math.Clamp(progressRatio, 0.0, 1.0);
        double fillWidth = totalWidth * ratio;
        const double thumbWidth = 14.0;

        if (MediaSeekProgressClip != null)
        {
            MediaSeekProgressClip.Rect = new Rect(0, -5, fillWidth, 24);
        }

        if (MediaSeekThumbTranslate != null)
        {
            double maxThumbLeft = Math.Max(0, totalWidth - thumbWidth);
            MediaSeekThumbTranslate.X = ratio * maxThumbLeft;
        }
    }

    private double CalculateRatioFromPosition(Point pos, FrameworkElement container)
    {
        double totalWidth = container.ActualWidth;
        if (totalWidth <= 0) return 0.0;

        const double thumbWidth = 14.0;
        double maxThumbLeft = totalWidth - thumbWidth;
        if (maxThumbLeft > 0)
        {
            return Math.Clamp((pos.X - (thumbWidth / 2.0)) / maxThumbLeft, 0.0, 1.0);
        }
        return Math.Clamp(pos.X / totalWidth, 0.0, 1.0);
    }

    private void Seekbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm?.Media != null)
        {
            UpdateProgressVisuals(_vm.Media.ProgressRatio);
            UpdateShimmerAnimation();
        }
    }

    private void Seekbar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_vm?.Media != null && _vm.Media.HasMedia && !_vm.Media.IsLive && _vm.Media.CanSeek)
        {
            AnimateHoverState(true, _isDragging);
        }
    }

    private void Seekbar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            AnimateHoverState(false, false);
        }
    }

    private void Seekbar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleSeekStart(e, sender as FrameworkElement ?? MediaSeekbarContainer);
    }

    private void Seekbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleSeekStart(e, sender as FrameworkElement ?? MediaSeekbarContainer);
    }

    private void HandleSeekStart(MouseButtonEventArgs e, FrameworkElement container)
    {
        if (_vm?.Media == null || !_vm.Media.HasMedia || _vm.Media.IsLive || !_vm.Media.CanSeek) return;

        _isDragging = true;
        StopShimmerAnimation();
        container.CaptureMouse();
        AnimateHoverState(true, isDragging: true);
        _vm.Media.StartScrubbing();

        double ratio = CalculateRatioFromPosition(e.GetPosition(container), container);
        _lastDragRatio = ratio;
        UpdateProgressVisuals(ratio);
    }

    private void Seekbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && _vm?.Media != null)
        {
            e.Handled = true;
            var container = sender as FrameworkElement ?? MediaSeekbarContainer;
            double ratio = CalculateRatioFromPosition(e.GetPosition(container), container);
            _lastDragRatio = ratio;
            UpdateProgressVisuals(ratio);
        }
    }

    private void Seekbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            e.Handled = true;
            _isDragging = false;
            var container = sender as FrameworkElement ?? MediaSeekbarContainer;
            container.ReleaseMouseCapture();

            bool isMouseStillOver = container.IsMouseOver;
            AnimateHoverState(isMouseStillOver, isDragging: false);

            if (_vm?.Media != null)
            {
                double ratio = CalculateRatioFromPosition(e.GetPosition(container), container);
                _lastDragRatio = ratio;
                _vm.Media.StopScrubbing(ratio);
                UpdateShimmerAnimation();
            }
        }
    }

    private void Seekbar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            var container = sender as FrameworkElement ?? MediaSeekbarContainer;
            AnimateHoverState(container.IsMouseOver, isDragging: false);
            if (_vm?.Media != null)
            {
                // Commit the last known drag position: ProgressRatio is frozen during
                // scrubbing and would seek backwards (often to 0) here.
                double ratio = _lastDragRatio;
                try
                {
                    if (container.ActualWidth > 0)
                    {
                        Point p = e.GetPosition(container);
                        if (p.X >= 0 && p.X <= container.ActualWidth)
                        {
                            ratio = CalculateRatioFromPosition(p, container);
                            _lastDragRatio = ratio;
                        }
                    }
                }
                catch { }
                _vm.Media.StopScrubbing(ratio);
                UpdateShimmerAnimation();
            }
        }
    }

    private void AnimateHoverState(bool isHovered, bool isDragging)
    {
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 1. Circular Thumb Opacity: always visible (fill —gap— ring —gap— unfilled)
        double targetOpacity = 1.0;
        var thumbOpacityAnim = new DoubleAnimation(targetOpacity, duration) { EasingFunction = easing };
        MediaSeekThumb?.BeginAnimation(OpacityProperty, thumbOpacityAnim);

        // 2. Circular Thumb Scale: 0.9 resting -> 1.05 hover -> 1.15 dragging
        double targetScale = isDragging ? 1.15 : (isHovered ? 1.05 : 0.9);
        var scaleXAnim = new DoubleAnimation(targetScale, duration) { EasingFunction = easing };
        var scaleYAnim = new DoubleAnimation(targetScale, duration) { EasingFunction = easing };
        MediaSeekThumbScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        MediaSeekThumbScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

        double targetScaleY = (isHovered || isDragging) ? 1.8 : 1.0;
        var trackAnim = new DoubleAnimation(targetScaleY, duration) { EasingFunction = easing };
        MediaSeekTrackScale?.BeginAnimation(ScaleTransform.ScaleYProperty, trackAnim);
    }

    private void UpdateShimmerAnimation()
    {
        if (_vm?.Media == null)
        {
            StopShimmerAnimation();
            return;
        }

        bool shouldAnimate = _vm.Media.IsPlaying && !_vm.Media.IsPlaybackStalled && _vm.Media.HasMedia && !_vm.Media.IsLive && !_isDragging && _vm.Media.ProgressRatio > 0.005 && IsLoaded && IsVisible;
        if (shouldAnimate)
        {
            StartShimmerCycle();
        }
        else
        {
            StopShimmerAnimation();
        }
    }

    private void StartShimmerCycle()
    {
        if (_isShimmerSweeping || _shimmerDelayTimer != null) return;
        TriggerNextShimmerSweep();
    }

    private void TriggerNextShimmerSweep()
    {
        _shimmerDelayTimer?.Stop();
        _shimmerDelayTimer = null;

        if (_vm?.Media == null || !_vm.Media.IsPlaying || _vm.Media.IsPlaybackStalled || !_vm.Media.HasMedia || _vm.Media.IsLive || _isDragging || !IsLoaded || !IsVisible)
        {
            StopShimmerAnimation();
            return;
        }

        if (MediaSeekbarContainer == null) return;
        double totalWidth = MediaSeekbarContainer.ActualWidth;
        if (totalWidth <= 0) return;

        double fillWidth = totalWidth * Math.Clamp(_vm.Media.ProgressRatio, 0.0, 1.0);
        if (fillWidth < 8) return;

        _isShimmerSweeping = true;

        var sweepDuration = TimeSpan.FromMilliseconds(1350);
        var sweepAnim = new DoubleAnimation(-50, fillWidth + 10, sweepDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var opacityAnim = new DoubleAnimationUsingKeyFrames();
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1050))));
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))));

        sweepAnim.Completed += (s, e) =>
        {
            _isShimmerSweeping = false;
            if (MediaSeekShimmer != null) MediaSeekShimmer.Opacity = 0.0;

            if (_vm?.Media != null && _vm.Media.IsPlaying && !_vm.Media.IsPlaybackStalled && IsLoaded && IsVisible)
            {
                _shimmerDelayTimer?.Stop();
                _shimmerDelayTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(2200)
                };
                _shimmerDelayTimer.Tick += (ts, te) =>
                {
                    _shimmerDelayTimer?.Stop();
                    _shimmerDelayTimer = null;
                    TriggerNextShimmerSweep();
                };
                _shimmerDelayTimer.Start();
            }
        };

        MediaSeekShimmer?.BeginAnimation(OpacityProperty, opacityAnim);
        MediaSeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, sweepAnim);
    }

    private void StopShimmerAnimation()
    {
        _shimmerDelayTimer?.Stop();
        _shimmerDelayTimer = null;
        _isShimmerSweeping = false;

        MediaSeekShimmer?.BeginAnimation(OpacityProperty, null);
        MediaSeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);

        if (MediaSeekShimmer != null)
        {
            MediaSeekShimmer.Opacity = 0.0;
        }
        if (MediaSeekShimmerTranslate != null)
        {
            MediaSeekShimmerTranslate.X = -60;
        }
    }
}
