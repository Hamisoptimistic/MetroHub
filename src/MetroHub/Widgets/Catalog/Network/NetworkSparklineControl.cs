using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MetroHub.Core.Network;

namespace MetroHub.Widgets.Catalog.Network;

public class NetworkSparklineControl : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples),
            typeof(IReadOnlyList<ThroughputSample>),
            typeof(NetworkSparklineControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ThroughputSample>? Samples
    {
        get => (IReadOnlyList<ThroughputSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private static readonly Pen LinePen;
    private static readonly Brush AreaBrush;
    private static readonly Pen GridPen;

    static NetworkSparklineControl()
    {
        var strokeColor = Color.FromRgb(0, 120, 212); // Windows 11 Blue #0078D4
        var pen = new Pen(new SolidColorBrush(strokeColor), 1.5);
        pen.Freeze();
        LinePen = pen;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x35, 0, 120, 212), 0.0),
                new GradientStop(Color.FromArgb(0x00, 0, 120, 212), 1.0)
            }
        };
        gradient.Freeze();
        AreaBrush = gradient;

        var gPen = new Pen(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), 1.0);
        gPen.Freeze();
        GridPen = gPen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        // Draw background grid lines (subtle Task Manager style)
        dc.DrawLine(GridPen, new Point(0, h * 0.33), new Point(w, h * 0.33));
        dc.DrawLine(GridPen, new Point(0, h * 0.66), new Point(w, h * 0.66));
        dc.DrawLine(GridPen, new Point(0, h), new Point(w, h));

        var samples = Samples;
        if (samples == null || samples.Count < 2) return;

        int count = samples.Count;
        double maxRate = 1024 * 1024; // Minimum 1 MB/s scale ceiling

        foreach (var s in samples)
        {
            if (s.DownloadBytesPerSec > maxRate)
            {
                maxRate = s.DownloadBytesPerSec;
            }
        }

        // Add 15% headroom
        maxRate *= 1.15;

        var lineGeom = new StreamGeometry();
        var areaGeom = new StreamGeometry();

        using (var lineCtx = lineGeom.Open())
        using (var areaCtx = areaGeom.Open())
        {
            double stepX = w / Math.Max(1, count - 1);

            double firstVal = samples[0].DownloadBytesPerSec;
            double firstY = Math.Clamp(h - (firstVal / maxRate * h), 2, h - 2);
            var firstPt = new Point(0, firstY);

            lineCtx.BeginFigure(firstPt, isFilled: false, isClosed: false);
            areaCtx.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
            areaCtx.LineTo(firstPt, isStroked: true, isSmoothJoin: true);

            for (int i = 1; i < count; i++)
            {
                double val = samples[i].DownloadBytesPerSec;
                double x = i * stepX;
                double y = Math.Clamp(h - (val / maxRate * h), 2, h - 2);
                var pt = new Point(x, y);

                lineCtx.LineTo(pt, isStroked: true, isSmoothJoin: true);
                areaCtx.LineTo(pt, isStroked: true, isSmoothJoin: true);
            }

            areaCtx.LineTo(new Point(w, h), isStroked: true, isSmoothJoin: true);
        }

        lineGeom.Freeze();
        areaGeom.Freeze();

        dc.DrawGeometry(AreaBrush, null, areaGeom);
        dc.DrawGeometry(null, LinePen, lineGeom);
    }
}
