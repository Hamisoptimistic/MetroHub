using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Network;

/// <summary>
/// Clean Modern Fluent Speedometer Arc Gauge.
/// Features a semi-circular 180° track, smooth physics-damped needle and glowing progress arc.
/// Completely free of text clutter to ensure a cohesive, non-overlapping design.
/// </summary>
public class SpeedometerArcControl : FrameworkElement
{
    public static readonly DependencyProperty SpeedMbpsProperty =
        DependencyProperty.Register(
            nameof(SpeedMbps),
            typeof(double),
            typeof(SpeedometerArcControl),
            new FrameworkPropertyMetadata(0.0, OnSpeedMbpsChanged));

    public static readonly DependencyProperty AnimatedProgressProperty =
        DependencyProperty.Register(
            nameof(AnimatedProgress),
            typeof(double),
            typeof(SpeedometerArcControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(SpeedometerArcControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxSpeedMbpsProperty =
        DependencyProperty.Register(
            nameof(MaxSpeedMbps),
            typeof(double),
            typeof(SpeedometerArcControl),
            new FrameworkPropertyMetadata(1000.0, OnSpeedMbpsChanged));

    public static readonly DependencyProperty ProgressBrushProperty =
        DependencyProperty.Register(
            nameof(ProgressBrush),
            typeof(Brush),
            typeof(SpeedometerArcControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnProgressBrushChanged));

    public double SpeedMbps
    {
        get => (double)GetValue(SpeedMbpsProperty);
        set => SetValue(SpeedMbpsProperty, value);
    }

    public double MaxSpeedMbps
    {
        get => (double)GetValue(MaxSpeedMbpsProperty);
        set => SetValue(MaxSpeedMbpsProperty, value);
    }

    public Brush? ProgressBrush
    {
        get => (Brush?)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public double AnimatedProgress
    {
        get => (double)GetValue(AnimatedProgressProperty);
        set => SetValue(AnimatedProgressProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private double _targetProgress = 0.0;
    private double _currentProgress = 0.0;
    private bool _isAnimating = false;
    private long _lastRenderTimestamp = 0;

    private Pen _cachedActivePen = DefaultActivePen;
    private Pen _cachedGlowPen = DefaultGlowPen;

    public SpeedometerArcControl()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateCachedBrushes();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCachedBrushes();
        if (Math.Abs(_targetProgress - _currentProgress) > 0.001)
        {
            EnsureAnimationActive();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    public static double SpeedToFraction(double speed)
    {
        if (speed <= 0) return 0.0;
        if (speed <= 5.0) return (speed / 5.0) * (1.0 / 8.0);
        if (speed <= 10.0) return (1.0 / 8.0) + ((speed - 5.0) / 5.0) * (1.0 / 8.0);
        if (speed <= 50.0) return (2.0 / 8.0) + ((speed - 10.0) / 40.0) * (1.0 / 8.0);
        if (speed <= 100.0) return (3.0 / 8.0) + ((speed - 50.0) / 50.0) * (1.0 / 8.0);
        if (speed <= 250.0) return (4.0 / 8.0) + ((speed - 100.0) / 150.0) * (1.0 / 8.0);
        if (speed <= 500.0) return (5.0 / 8.0) + ((speed - 250.0) / 250.0) * (1.0 / 8.0);
        if (speed <= 750.0) return (6.0 / 8.0) + ((speed - 500.0) / 250.0) * (1.0 / 8.0);
        if (speed <= 1000.0) return (7.0 / 8.0) + ((speed - 750.0) / 250.0) * (1.0 / 8.0);
        return 1.0;
    }

    private static void OnProgressBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpeedometerArcControl control)
        {
            control.UpdateCachedBrushes();
        }
    }

    private const double ArcThickness = 15.0;
    private const double GlowThickness = 21.0;

    private void UpdateCachedBrushes()
    {
        if (ProgressBrush == null)
        {
            _cachedActivePen = DefaultActivePen;
            _cachedGlowPen = DefaultGlowPen;
            return;
        }

        var activePen = new Pen(ProgressBrush, ArcThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        activePen.Freeze();
        _cachedActivePen = activePen;

        if (ProgressBrush is SolidColorBrush scb)
        {
            Color c = scb.Color;
            var gpBrush = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
            gpBrush.Freeze();
            var glowPen = new Pen(gpBrush, GlowThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            glowPen.Freeze();
            _cachedGlowPen = glowPen;
        }
        else
        {
            _cachedGlowPen = DefaultGlowPen;
        }
    }

    private static void OnSpeedMbpsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SpeedometerArcControl control) return;

        control._targetProgress = SpeedToFraction(control.SpeedMbps);
        if (control.IsLoaded)
        {
            control.EnsureAnimationActive();
        }
        else
        {
            // If control is not currently loaded in the visual tree, update progress directly
            // avoiding running an invisible CompositionTarget.Rendering loop
            control._currentProgress = control._targetProgress;
            control.AnimatedProgress = control._targetProgress;
        }
    }

    private void EnsureAnimationActive()
    {
        if (!_isAnimating && IsLoaded)
        {
            _isAnimating = true;
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }
    }

    private void StopAnimation()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastRenderTimestamp) / (double)Stopwatch.Frequency;
        _lastRenderTimestamp = now;

        if (dt > 0.1) dt = 0.1;
        if (dt <= 0) return;

        // Smooth exponential damping approach (time constant ~130ms)
        double lambda = 1.0 - Math.Exp(-dt / 0.13);
        _currentProgress += (_targetProgress - _currentProgress) * lambda;

        // Settle check
        if (Math.Abs(_targetProgress - _currentProgress) < 0.0005)
        {
            _currentProgress = _targetProgress;
            StopAnimation();
        }

        AnimatedProgress = _currentProgress;
        InvalidateVisual();
    }

    private static readonly Pen TrackPen;
    private static readonly Pen DefaultActivePen;
    private static readonly Pen DefaultGlowPen;

    static SpeedometerArcControl()
    {
        // Background track: 15.0px subtle translucent acrylic gray
        var trackBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        trackBrush.Freeze();
        TrackPen = new Pen(trackBrush, ArcThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        TrackPen.Freeze();

        // Default active sweep: 15.0px Windows 11 Fluent Blue (#0091FF)
        var activeBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x91, 0xFF));
        activeBrush.Freeze();
        DefaultActivePen = new Pen(activeBrush, ArcThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        DefaultActivePen.Freeze();

        // Ambient glow pen behind active sweep
        var glowBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x91, 0xFF));
        glowBrush.Freeze();
        DefaultGlowPen = new Pen(glowBrush, GlowThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        DefaultGlowPen.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        double strokeMargin = 12.0;
        double radius = Math.Min(w / 2.0 - strokeMargin, h - strokeMargin - 4.0);
        if (radius <= 10) return;

        double cx = w / 2.0;
        double cy = radius + strokeMargin;

        // Clean 180° semi-circle arc: starts at 180° (9 o'clock) and ends at 360° (3 o'clock)
        // Stays strictly above the baseline cy so it CANNOT ever overlap with elements below
        const double startAngleDeg = 180.0;
        const double totalSweepDeg = 180.0;

        // 1. Draw background track arc
        var trackGeom = CreateArcGeometry(cx, cy, radius, startAngleDeg, totalSweepDeg);
        if (trackGeom != null)
        {
            dc.DrawGeometry(null, TrackPen, trackGeom);
        }

        // 2. Draw active progress arc (thick sweep with ambient glow, no needle or glowing thumb)
        double progress = Math.Clamp(AnimatedProgress, 0.0, 1.0);
        if (progress > 0.003)
        {
            double activeSweepDeg = totalSweepDeg * progress;
            var activeGeom = CreateArcGeometry(cx, cy, radius, startAngleDeg, activeSweepDeg);
            if (activeGeom != null)
            {
                // Ambient glow behind active arc (cached & frozen)
                dc.DrawGeometry(null, _cachedGlowPen, activeGeom);

                // Sharp active line (cached & frozen)
                dc.DrawGeometry(null, _cachedActivePen, activeGeom);
            }
        }
    }

    private static StreamGeometry? CreateArcGeometry(double cx, double cy, double radius, double startAngleDeg, double sweepAngleDeg)
    {
        if (sweepAngleDeg <= 0.1) return null;

        double startRad = startAngleDeg * Math.PI / 180.0;
        double endRad = (startAngleDeg + sweepAngleDeg) * Math.PI / 180.0;

        var startPoint = new Point(cx + radius * Math.Cos(startRad), cy + radius * Math.Sin(startRad));
        var endPoint = new Point(cx + radius * Math.Cos(endRad), cy + radius * Math.Sin(endRad));

        bool isLargeArc = sweepAngleDeg > 180.0;

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(startPoint, isFilled: false, isClosed: false);
            ctx.ArcTo(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
        }

        geom.Freeze();
        return geom;
    }
}
