using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Pomodoro;

public partial class PomodoroWidgetView : UserControl
{
    private PomodoroWidgetViewModel? _vm;

    public PomodoroWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PomodoroWidgetViewModel vm)
        {
            if (_vm != null && _vm != vm)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _vm = vm;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateVisuals(_vm);
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
        if (e.OldValue is PomodoroWidgetViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is PomodoroWidgetViewModel newVm)
        {
            _vm = newVm;
            if (IsLoaded)
            {
                newVm.PropertyChanged -= OnViewModelPropertyChanged;
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                UpdateVisuals(newVm);
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

        if (e.PropertyName is nameof(PomodoroWidgetViewModel.ProgressRatio)
                           or nameof(PomodoroWidgetViewModel.RingSize)
                           or nameof(PomodoroWidgetViewModel.IsRingOnly))
        {
            UpdateVisuals(_vm);
        }
    }

    private void UpdateVisuals(PomodoroWidgetViewModel vm)
    {
        // 1. Update 4x2 Ring
        if (RingPath4x2 != null)
        {
            UpdateRingPath(RingPath4x2, 96, 6, vm.ProgressRatio);
        }

        // 2. Update 8x3 / 8x4 Hero Ring
        if (RingPathHero != null)
        {
            double ringSize = vm.RingSize;
            if (BgRingGeometry != null)
            {
                BgRingGeometry.Center = new Point(ringSize / 2.0, ringSize / 2.0);
                BgRingGeometry.RadiusX = (ringSize - 6.0) / 2.0;
                BgRingGeometry.RadiusY = (ringSize - 6.0) / 2.0;
            }
            UpdateRingPath(RingPathHero, ringSize, 6, vm.ProgressRatio);
        }

        // 3. Update hairline progress bar
        UpdateProgressBar(vm.ProgressRatio);
    }

    private void UpdateProgressBar(double ratio)
    {
        if (ProgressBarContainer == null || ProgressFill == null) return;

        double totalWidth = ProgressBarContainer.ActualWidth;
        if (totalWidth <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        ProgressFill.Width = totalWidth * ratio;
    }

    private void ProgressBarContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm != null)
        {
            UpdateProgressBar(_vm.ProgressRatio);
        }
    }

    private static void UpdateRingPath(System.Windows.Shapes.Path path, double size, double strokeThickness, double ratio)
    {
        if (size <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double radius = (size - strokeThickness) / 2.0;
        double cx = size / 2.0;
        double cy = size / 2.0;

        if (ratio <= 0.001)
        {
            path.Data = Geometry.Empty;
            return;
        }

        if (ratio >= 0.999)
        {
            // Full circle
            path.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
            return;
        }

        // Calculate arc from top (-90 degrees) clockwise
        double startAngle = -90.0;
        double endAngle = startAngle + (360.0 * ratio);

        double startRad = startAngle * Math.PI / 180.0;
        double endRad = endAngle * Math.PI / 180.0;

        Point startPoint = new Point(cx + (radius * Math.Cos(startRad)), cy + (radius * Math.Sin(startRad)));
        Point endPoint = new Point(cx + (radius * Math.Cos(endRad)), cy + (radius * Math.Sin(endRad)));

        bool isLargeArc = ratio > 0.5;

        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        path.Data = geometry;
    }
}
