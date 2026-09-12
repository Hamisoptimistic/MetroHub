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
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
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
            }
        }
        else if (e.PropertyName == nameof(MediaWidgetViewModel.HasMedia) && _vm != null)
        {
            if (!_vm.HasMedia)
            {
                UpdateProgressVisuals(0);
            }
        }
    }

    private void UpdateProgressVisuals(double ratio)
    {
        double totalWidth = SeekbarContainer.ActualWidth;
        if (totalWidth <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double fillWidth = totalWidth * ratio;
        SeekProgressFill.Width = fillWidth;

        // Center 5px sleek vertical pill thumb on the progress edge
        double thumbLeft = Math.Clamp(fillWidth - 2.5, 0, Math.Max(0, totalWidth - 5.0));
        SeekThumb.Margin = new Thickness(thumbLeft, 0, 0, 0);
    }

    private void Seekbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm != null)
        {
            UpdateProgressVisuals(_vm.ProgressRatio);
        }
    }

    private void Seekbar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_vm != null && _vm.HasMedia)
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
        if (_vm == null || !_vm.HasMedia) return;

        _isDragging = true;
        SeekbarContainer.CaptureMouse();
        AnimateHoverState(true, isDragging: true);
        _vm.StartScrubbing();

        double x = e.GetPosition(SeekbarContainer).X;
        double ratio = Math.Clamp(x / Math.Max(1.0, SeekbarContainer.ActualWidth), 0.0, 1.0);
        UpdateProgressVisuals(ratio);
    }

    private void Seekbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && _vm != null)
        {
            e.Handled = true;
            double x = e.GetPosition(SeekbarContainer).X;
            double ratio = Math.Clamp(x / Math.Max(1.0, SeekbarContainer.ActualWidth), 0.0, 1.0);
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
                double x = e.GetPosition(SeekbarContainer).X;
                double ratio = Math.Clamp(x / Math.Max(1.0, SeekbarContainer.ActualWidth), 0.0, 1.0);
                _vm.StopScrubbing(ratio);
            }
        }
    }

    private void AnimateHoverState(bool isHovered, bool isDragging)
    {
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 1. Thumb Opacity: 0 when resting, 1 when hovered or scrubbing
        double targetOpacity = (isHovered || isDragging) ? 1.0 : 0.0;
        var thumbOpacityAnim = new DoubleAnimation(targetOpacity, duration) { EasingFunction = easing };
        SeekThumb.BeginAnimation(OpacityProperty, thumbOpacityAnim);

        // 2. Vertical Pill Thumb Scale: 0.6x0.7 resting -> 1.0x1.0 hover -> 1.2x1.1 dragging
        double targetScaleX = isDragging ? 1.2 : (isHovered ? 1.0 : 0.6);
        double targetScaleY = isDragging ? 1.1 : (isHovered ? 1.0 : 0.7);
        var scaleXAnim = new DoubleAnimation(targetScaleX, duration) { EasingFunction = easing };
        var scaleYAnim = new DoubleAnimation(targetScaleY, duration) { EasingFunction = easing };
        SeekThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        SeekThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

        // 3. Track Height: 3px resting -> 5px on hover/drag
        double targetHeight = (isHovered || isDragging) ? 5.0 : 3.0;
        var trackHeightAnim = new DoubleAnimation(targetHeight, duration) { EasingFunction = easing };
        SeekTrackTrough?.BeginAnimation(HeightProperty, trackHeightAnim);
        SeekTrackBg?.BeginAnimation(HeightProperty, trackHeightAnim);
        SeekProgressFill?.BeginAnimation(HeightProperty, trackHeightAnim);
    }
}
