using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MetroHub.Widgets.Catalog.Media;

public partial class MediaWidgetView : UserControl
{
    private bool _isDragging = false;
    private FrameworkElement? _activeDragContainer;
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
                           or nameof(MediaWidgetViewModel.IsPlaying)
                           or nameof(MediaWidgetViewModel.IsSlimMode)
                           or nameof(MediaWidgetViewModel.IsZuneMode)
                           or nameof(MediaWidgetViewModel.IsPlaybackStalled))
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

        bool shouldAnimate = _vm.IsPlaying && !_vm.IsPlaybackStalled && _vm.HasMedia && !_vm.IsLive && !_isDragging && _vm.ProgressRatio > 0.005 && IsLoaded && IsVisible;
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

        if (_vm == null || !_vm.IsPlaying || _vm.IsPlaybackStalled || !_vm.HasMedia || _vm.IsLive || _isDragging || !IsLoaded || !IsVisible)
        {
            StopShimmerAnimation();
            return;
        }

        bool isSlim = _vm.IsSlimMode;
        bool isZune = _vm.IsZuneMode;
        FrameworkElement? container = isSlim ? SlimSeekbarContainer 
                                    : isZune ? ZuneSeekbarContainer 
                                    : SeekbarContainer;
        if (container == null) return;

        double totalWidth = container.ActualWidth;
        if (totalWidth <= 0) return;

        double fillWidth = totalWidth * Math.Clamp(_vm.ProgressRatio, 0.0, 1.0);
        if (fillWidth < 8) return;

        _isShimmerSweeping = true;

        var sweepDuration = TimeSpan.FromMilliseconds(1350);
        double shimmerStart = isSlim ? -50 : -70;
        var sweepAnim = new DoubleAnimation(shimmerStart, fillWidth + 10, sweepDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var opacityAnim = new DoubleAnimationUsingKeyFrames();
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1050))));
        opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))));

        Rectangle? shimmer = isSlim ? SlimSeekShimmer 
                           : isZune ? ZuneSeekShimmer 
                           : SeekShimmer;
        TranslateTransform? shimmerTranslate = isSlim ? SlimSeekShimmerTranslate 
                                             : isZune ? ZuneSeekShimmerTranslate 
                                             : SeekShimmerTranslate;

        sweepAnim.Completed += (s, e) =>
        {
            _isShimmerSweeping = false;
            if (shimmer != null) shimmer.Opacity = 0.0;

            if (_vm != null && _vm.IsPlaying && !_vm.IsPlaybackStalled && IsLoaded && IsVisible)
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

        shimmer?.BeginAnimation(OpacityProperty, opacityAnim);
        shimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, sweepAnim);
    }

    private void StopShimmerAnimation()
    {
        _shimmerDelayTimer?.Stop();
        _shimmerDelayTimer = null;
        _isShimmerSweeping = false;

        SeekShimmer?.BeginAnimation(OpacityProperty, null);
        SeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
        SlimSeekShimmer?.BeginAnimation(OpacityProperty, null);
        SlimSeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
        ZuneSeekShimmer?.BeginAnimation(OpacityProperty, null);
        ZuneSeekShimmerTranslate?.BeginAnimation(TranslateTransform.XProperty, null);

        if (SeekShimmer != null)
        {
            SeekShimmer.Opacity = 0.0;
        }
        if (SeekShimmerTranslate != null)
        {
            SeekShimmerTranslate.X = -80;
        }
        if (SlimSeekShimmer != null)
        {
            SlimSeekShimmer.Opacity = 0.0;
        }
        if (SlimSeekShimmerTranslate != null)
        {
            SlimSeekShimmerTranslate.X = -60;
        }
        if (ZuneSeekShimmer != null)
        {
            ZuneSeekShimmer.Opacity = 0.0;
        }
        if (ZuneSeekShimmerTranslate != null)
        {
            ZuneSeekShimmerTranslate.X = -80;
        }
    }

    private void UpdateProgressVisuals(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        const double thumbWidth = 12.0;

        // Standard Seekbar
        if (SeekbarContainer != null && SeekbarContainer.ActualWidth > 0)
        {
            double totalWidth = SeekbarContainer.ActualWidth;
            double fillWidth = totalWidth * ratio;

            if (SeekProgressClip != null)
            {
                SeekProgressClip.Rect = new Rect(0, -5, fillWidth, 24);
            }

            if (SeekThumbTranslate != null)
            {
                double maxThumbLeft = Math.Max(0, totalWidth - thumbWidth);
                SeekThumbTranslate.X = ratio * maxThumbLeft;
            }
        }

        // Slim Seekbar
        if (SlimSeekbarContainer != null && SlimSeekbarContainer.ActualWidth > 0)
        {
            double totalWidth = SlimSeekbarContainer.ActualWidth;
            double fillWidth = totalWidth * ratio;

            if (SlimSeekProgressClip != null)
            {
                SlimSeekProgressClip.Rect = new Rect(0, -5, fillWidth, 24);
            }

            if (SlimSeekThumbTranslate != null)
            {
                double maxThumbLeft = Math.Max(0, totalWidth - thumbWidth);
                SlimSeekThumbTranslate.X = ratio * maxThumbLeft;
            }
        }

        // Zune Seekbar
        if (ZuneSeekbarContainer != null && ZuneSeekbarContainer.ActualWidth > 0)
        {
            double totalWidth = ZuneSeekbarContainer.ActualWidth;
            double fillWidth = totalWidth * ratio;

            if (ZuneSeekProgressClip != null)
            {
                ZuneSeekProgressClip.Rect = new Rect(0, -5, fillWidth, 24);
            }

            if (ZuneSeekThumbTranslate != null)
            {
                double maxThumbLeft = Math.Max(0, totalWidth - thumbWidth);
                ZuneSeekThumbTranslate.X = ratio * maxThumbLeft;
            }
        }
    }

    private double CalculateRatioFromPosition(Point pos, FrameworkElement container)
    {
        double totalWidth = container.ActualWidth;
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
        HandleSeekStart(e, sender as FrameworkElement ?? SeekbarContainer);
    }

    private void Seekbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleSeekStart(e, sender as FrameworkElement ?? SeekbarContainer);
    }

    private void HandleSeekStart(MouseButtonEventArgs e, FrameworkElement container)
    {
        if (_vm == null || !_vm.HasMedia || _vm.IsLive || !_vm.CanSeek) return;

        _isDragging = true;
        _activeDragContainer = container;
        StopShimmerAnimation();
        container.CaptureMouse();
        AnimateHoverState(true, isDragging: true);
        _vm.StartScrubbing();

        double ratio = CalculateRatioFromPosition(e.GetPosition(container), container);
        UpdateProgressVisuals(ratio);
    }

    private void Seekbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && _vm != null)
        {
            e.Handled = true;
            var container = _activeDragContainer ?? sender as FrameworkElement ?? SeekbarContainer;
            double ratio = CalculateRatioFromPosition(e.GetPosition(container), container);
            UpdateProgressVisuals(ratio);
        }
    }

    private void Seekbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            e.Handled = true;
            _isDragging = false;
            var container = _activeDragContainer ?? sender as FrameworkElement ?? SeekbarContainer;
            container.ReleaseMouseCapture();
            _activeDragContainer = null;

            bool isMouseStillOver = container.IsMouseOver;
            AnimateHoverState(isMouseStillOver, isDragging: false);

            if (_vm != null)
            {
                double ratio = CalculateRatioFromPosition(e.GetPosition(container), container);
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
            var container = _activeDragContainer ?? sender as FrameworkElement ?? SeekbarContainer;
            _activeDragContainer = null;
            AnimateHoverState(container.IsMouseOver, isDragging: false);
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
        SlimSeekThumb?.BeginAnimation(OpacityProperty, thumbOpacityAnim);
        ZuneSeekThumb?.BeginAnimation(OpacityProperty, thumbOpacityAnim);

        // 2. Circular Thumb Scale: 0.85 resting -> 1.0 hover -> 1.08 dragging
        double targetScale = isDragging ? 1.08 : (isHovered ? 1.0 : 0.85);
        var scaleXAnim = new DoubleAnimation(targetScale, duration) { EasingFunction = easing };
        var scaleYAnim = new DoubleAnimation(targetScale, duration) { EasingFunction = easing };
        SeekThumbScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        SeekThumbScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        SlimSeekThumbScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        SlimSeekThumbScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        ZuneSeekThumbScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        ZuneSeekThumbScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

        // 3. Track Height: 1.0 resting (2px) -> 1.8 on hover/drag (3.6px) via GPU ScaleY (zero layout passes, zero NaN)
        double targetScaleY = (isHovered || isDragging) ? 1.8 : 1.0;
        var trackAnim = new DoubleAnimation(targetScaleY, duration) { EasingFunction = easing };
        SeekTrackScale?.BeginAnimation(ScaleTransform.ScaleYProperty, trackAnim);
        SlimSeekTrackScale?.BeginAnimation(ScaleTransform.ScaleYProperty, trackAnim);
        ZuneSeekTrackScale?.BeginAnimation(ScaleTransform.ScaleYProperty, trackAnim);
    }
}
