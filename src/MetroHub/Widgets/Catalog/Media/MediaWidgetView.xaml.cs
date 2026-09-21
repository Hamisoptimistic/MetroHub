using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Media;

public partial class MediaWidgetView : UserControl
{
    private bool _isDragging = false;
    private MediaWidgetViewModel? _vm;

    public MediaWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MediaWidgetViewModel vm)
        {
            if (_vm != null && _vm != vm)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _vm = vm;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateProgressVisuals(_vm.ProgressRatio);
            UpdateShimmerAnimation();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopShimmerAnimation();
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateShimmerAnimation();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MediaWidgetViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is MediaWidgetViewModel newVm)
        {
            _vm = newVm;
            if (IsLoaded)
            {
                newVm.PropertyChanged -= OnViewModelPropertyChanged;
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                UpdateProgressVisuals(newVm.ProgressRatio);
            }
        }
        else
        {
            _vm = null;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _shimmerDelayTimer;
    private bool _isShimmerSweeping;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDragging) return;

        if (e.PropertyName is nameof(MediaWidgetViewModel.ProgressRatio)
                           or nameof(MediaWidgetViewModel.DurationSeconds)
                           or nameof(MediaWidgetViewModel.PositionSeconds)
                           or nameof(MediaWidgetViewModel.IsPlaying))
        {
            if (_vm != null)
            {
                UpdateProgressVisuals(_vm.ProgressRatio);
                UpdateShimmerAnimation();
            }
        }
        else if (e.PropertyName == nameof(MediaWidgetViewModel.HasMedia) && _vm != null)
        {
            if (!_vm.HasMedia)
            {
                UpdateProgressVisuals(0);
            }
            UpdateShimmerAnimation();
        }
        else if (e.PropertyName is nameof(MediaWidgetViewModel.IsLive) or nameof(MediaWidgetViewModel.CanSeek))
        {
            if (_vm != null && (_vm.IsLive || !_vm.CanSeek))
            {
                UpdateProgressVisuals(0);
                AnimateHoverState(false, false);
            }
            else if (_vm != null)
            {
                UpdateProgressVisuals(_vm.ProgressRatio);
            }
            UpdateShimmerAnimation();
        }
    }

    private void UpdateShimmerAnimation()
    {
        if (_vm == null)
        {
            StopShimmerAnimation();
            return;
        }

        bool shouldAnimate = _vm.IsPlaying && _vm.HasMedia && !_vm.IsLive && !_isDragging && _vm.ProgressRatio > 0.005 && IsLoaded && IsVisible;
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

        if (_vm == null || !_vm.IsPlaying || !_vm.HasMedia || _vm.IsLive || _isDragging || !IsLoaded || !IsVisible)
        {
            StopShimmerAnimation();
            return;
        }

        double totalWidth = SeekbarContainer.ActualWidth;
        if (totalWidth <= 0) return;

        double fillWidth = totalWidth * Math.Clamp(_vm.ProgressRatio, 0.0, 1.0);
        if (fillWidth < 8) return;

        _isShimmerSweeping = true;

        var sweepDuration = TimeSpan.FromMilliseconds(1350);
        var sweepAnim = new DoubleAnimation(-70, fillWidth + 10, sweepDuration)
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
            if (SeekShimmer != null) SeekShimmer.Opacity = 0.0;

            if (_vm != null && _vm.IsPlaying && IsLoaded && IsVisible)
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

        SeekShimmer?.BeginAnimation(OpacityProperty, opacityAnim);
        SeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, sweepAnim);
    }

    private void StopShimmerAnimation()
    {
        _shimmerDelayTimer?.Stop();
        _shimmerDelayTimer = null;
        _isShimmerSweeping = false;

        SeekShimmer?.BeginAnimation(OpacityProperty, null);
        SeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);

        if (SeekShimmer != null)
        {
            SeekShimmer.Opacity = 0.0;
        }
        if (SeekShimmerTranslate != null)
        {
            SeekShimmerTranslate.X = -80;
        }
    }

    private void UpdateProgressVisuals(double ratio)
    {
        double totalWidth = SeekbarContainer.ActualWidth;
        if (totalWidth <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double fillWidth = totalWidth * ratio;
        
        if (SeekProgressClip != null)
        {
            SeekProgressClip.Rect = new Rect(0, -5, fillWidth, 24);
        }

        if (SeekThumbTranslate != null)
        {
            const double thumbWidth = 12.0;
            double maxThumbLeft = Math.Max(0, totalWidth - thumbWidth);
            double thumbLeft = ratio * maxThumbLeft;
            SeekThumbTranslate.X = thumbLeft;
        }
    }

    private double CalculateRatioFromPosition(Point pos)
    {
        double totalWidth = SeekbarContainer.ActualWidth;
        if (totalWidth <= 0) return 0.0;

        const double thumbWidth = 12.0;
        double maxThumbLeft = totalWidth - thumbWidth;
        if (maxThumbLeft > 0)
        {
            return Math.Clamp((pos.X - (thumbWidth / 2.0)) / maxThumbLeft, 0.0, 1.0);
        }
        return Math.Clamp(pos.X / totalWidth, 0.0, 1.0);
    }

    private void Seekbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm != null)
        {
            UpdateProgressVisuals(_vm.ProgressRatio);
            UpdateShimmerAnimation();
        }
    }

    private void Seekbar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_vm != null && _vm.HasMedia && !_vm.IsLive && _vm.CanSeek)
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
        // Intercept tunneling mouse down event to completely prevent parent tile activation/drag
        e.Handled = true;
        HandleSeekStart(e);
    }

    private void Seekbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleSeekStart(e);
    }

    private void HandleSeekStart(MouseButtonEventArgs e)
    {
        if (_vm == null || !_vm.HasMedia || _vm.IsLive || !_vm.CanSeek) return;

        _isDragging = true;
        StopShimmerAnimation();
        SeekbarContainer.CaptureMouse();
        AnimateHoverState(true, isDragging: true);
        _vm.StartScrubbing();

        double ratio = CalculateRatioFromPosition(e.GetPosition(SeekbarContainer));
        UpdateProgressVisuals(ratio);
    }

    private void Seekbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && _vm != null)
        {
            e.Handled = true;
            double ratio = CalculateRatioFromPosition(e.GetPosition(SeekbarContainer));
            UpdateProgressVisuals(ratio);
        }
    }

    private void Seekbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            e.Handled = true;
            _isDragging = false;
            SeekbarContainer.ReleaseMouseCapture();

            bool isMouseStillOver = SeekbarContainer.IsMouseOver;
            AnimateHoverState(isMouseStillOver, isDragging: false);

            if (_vm != null)
            {
                double ratio = CalculateRatioFromPosition(e.GetPosition(SeekbarContainer));
                _vm.StopScrubbing(ratio);
                UpdateShimmerAnimation();
            }
        }
    }

    private void Seekbar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            AnimateHoverState(SeekbarContainer.IsMouseOver, isDragging: false);
            if (_vm != null)
            {
                _vm.StopScrubbing(_vm.ProgressRatio);
                UpdateShimmerAnimation();
            }
        }
    }

    private void AnimateHoverState(bool isHovered, bool isDragging)
    {
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 1. Circular Thumb Opacity: 0 when resting, 1 when hovered or scrubbing
        double targetOpacity = (isHovered || isDragging) ? 1.0 : 0.0;
        var thumbOpacityAnim = new DoubleAnimation(targetOpacity, duration) { EasingFunction = easing };
        SeekThumb?.BeginAnimation(OpacityProperty, thumbOpacityAnim);

        // 2. Circular Thumb Scale: 0.85 resting -> 1.0 hover -> 1.08 dragging
        double targetScale = isDragging ? 1.08 : (isHovered ? 1.0 : 0.85);
        var scaleXAnim = new DoubleAnimation(targetScale, duration) { EasingFunction = easing };
        var scaleYAnim = new DoubleAnimation(targetScale, duration) { EasingFunction = easing };
        SeekThumbScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        SeekThumbScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

        // 3. Track Height: 1.0 resting (2px) -> 1.8 on hover/drag (3.6px) via GPU ScaleY (zero layout passes, zero NaN)
        double targetScaleY = (isHovered || isDragging) ? 1.8 : 1.0;
        var trackAnim = new DoubleAnimation(targetScaleY, duration) { EasingFunction = easing };
        SeekTrackScale?.BeginAnimation(ScaleTransform.ScaleYProperty, trackAnim);
    }
}
