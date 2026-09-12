using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Photos;

public partial class PhotosWidgetView : UserControl
{
    private PhotosWidgetViewModel? _vm;
    private int _motionProfileIndex = 0;
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(900));
    private static readonly IEasingFunction FadeEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

    public PhotosWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        MouseEnter += (s, e) => _vm?.SetHovered(true);
        MouseLeave += (s, e) => _vm?.SetHovered(false);
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private DateTime _lastWheelTime = DateTime.MinValue;

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_vm == null || _vm.PhotoCount <= 1) return;

        var now = DateTime.UtcNow;
        if ((now - _lastWheelTime).TotalMilliseconds < 220)
        {
            e.Handled = true;
            return;
        }
        _lastWheelTime = now;

        if (e.Delta < 0)
        {
            _ = _vm.NextPhoto();
        }
        else if (e.Delta > 0)
        {
            _ = _vm.PreviousPhoto();
        }

        e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PhotosWidgetViewModel vm)
        {
            if (_vm != null && _vm != vm)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _vm = vm;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyInitialState(vm);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.SetHovered(false);
        }
        StopAllAnimations();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PhotosWidgetViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.SetHovered(false);
        }

        StopAllAnimations();

        if (e.NewValue is PhotosWidgetViewModel newVm)
        {
            _vm = newVm;
            if (IsLoaded)
            {
                newVm.PropertyChanged -= OnViewModelPropertyChanged;
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                ApplyInitialState(newVm);
            }
        }
        else
        {
            _vm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;

        if (e.PropertyName == nameof(PhotosWidgetViewModel.IsDisplayingA))
        {
            AnimateTransition(_vm.IsDisplayingA);
        }
        else if (e.PropertyName == nameof(PhotosWidgetViewModel.PhotoLayerA) && _vm.PhotoLayerA != null && _vm.IsDisplayingA)
        {
            ApplyInitialState(_vm);
        }
    }

    private void ApplyInitialState(PhotosWidgetViewModel vm)
    {
        StopKenBurns(ScaleA, TranslateA);
        StopKenBurns(ScaleB, TranslateB);
        LayerAGrid.BeginAnimation(OpacityProperty, null);
        LayerBGrid.BeginAnimation(OpacityProperty, null);

        if (vm.PhotoLayerA != null)
        {
            LayerAGrid.Opacity = 1.0;
            LayerBGrid.Opacity = 0.0;
            StartKenBurns(ScaleA, TranslateA, vm.IntervalSeconds);
        }
        else if (vm.PhotoLayerB != null)
        {
            LayerAGrid.Opacity = 0.0;
            LayerBGrid.Opacity = 1.0;
            StartKenBurns(ScaleB, TranslateB, vm.IntervalSeconds);
        }
        else
        {
            LayerAGrid.Opacity = 1.0;
            LayerBGrid.Opacity = 0.0;
        }
    }

    private void AnimateTransition(bool toLayerA)
    {
        int interval = _vm?.IntervalSeconds ?? 30;

        if (toLayerA)
        {
            StartKenBurns(ScaleA, TranslateA, interval);

            var animIn = new DoubleAnimation(1.0, FadeDuration) { EasingFunction = FadeEase };
            var animOut = new DoubleAnimation(0.0, FadeDuration) { EasingFunction = FadeEase };

            animOut.Completed += (s, e) =>
            {
                StopKenBurns(ScaleB, TranslateB);
            };

            LayerAGrid.BeginAnimation(OpacityProperty, animIn);
            LayerBGrid.BeginAnimation(OpacityProperty, animOut);
        }
        else
        {
            StartKenBurns(ScaleB, TranslateB, interval);

            var animIn = new DoubleAnimation(1.0, FadeDuration) { EasingFunction = FadeEase };
            var animOut = new DoubleAnimation(0.0, FadeDuration) { EasingFunction = FadeEase };

            animOut.Completed += (s, e) =>
            {
                StopKenBurns(ScaleA, TranslateA);
            };

            LayerBGrid.BeginAnimation(OpacityProperty, animIn);
            LayerAGrid.BeginAnimation(OpacityProperty, animOut);
        }
    }

    private void StartKenBurns(ScaleTransform scale, TranslateTransform translate, int intervalSeconds)
    {
        // Continuous, graceful 7.5s floating oscillation with AutoReverse for smooth ambient motion
        // NOTE: Scale is bounded strictly between 1.08 and 1.16 with maximum translation of ±8px X / ±5px Y.
        // At minimum scale (1.08), the image maintains a guaranteed >10px overscan bleed on all 4 sides,
        // making it mathematically impossible for any edge or background gap to be exposed when zoomed out.
        var duration = new Duration(TimeSpan.FromSeconds(7.5));
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

        double fromScale, toScale, fromX, toX, fromY, toY;
        switch (_motionProfileIndex % 4)
        {
            case 0:
                // Gentle Zoom In with subtle diagonal upward-right drift
                fromScale = 1.08; toScale = 1.16;
                fromX = 0; toX = 8;
                fromY = 0; toY = -5;
                break;
            case 1:
                // Gentle Zoom In with subtle diagonal downward-left drift
                fromScale = 1.08; toScale = 1.16;
                fromX = 0; toX = -8;
                fromY = 0; toY = 5;
                break;
            case 2:
                // Ambient Breathing with gentle horizontal pan
                fromScale = 1.11; toScale = 1.15;
                fromX = -6; toX = 6;
                fromY = -3; toY = 3;
                break;
            default:
                // Ambient Breathing with reverse horizontal pan
                fromScale = 1.15; toScale = 1.11;
                fromX = 6; toX = -6;
                fromY = 3; toY = -3;
                break;
        }
        _motionProfileIndex++;

        var animScaleX = new DoubleAnimation(fromScale, toScale, duration) { EasingFunction = easing, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        var animScaleY = new DoubleAnimation(fromScale, toScale, duration) { EasingFunction = easing, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        var animTransX = new DoubleAnimation(fromX, toX, duration) { EasingFunction = easing, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        var animTransY = new DoubleAnimation(fromY, toY, duration) { EasingFunction = easing, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScaleY);
        translate.BeginAnimation(TranslateTransform.XProperty, animTransX);
        translate.BeginAnimation(TranslateTransform.YProperty, animTransY);
    }

    private static void StopKenBurns(ScaleTransform scale, TranslateTransform translate)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = 1.08;
        scale.ScaleY = 1.08;
        translate.X = 0;
        translate.Y = 0;
    }

    private void StopAllAnimations()
    {
        StopKenBurns(ScaleA, TranslateA);
        StopKenBurns(ScaleB, TranslateB);
        LayerAGrid.BeginAnimation(OpacityProperty, null);
        LayerBGrid.BeginAnimation(OpacityProperty, null);
    }
}
