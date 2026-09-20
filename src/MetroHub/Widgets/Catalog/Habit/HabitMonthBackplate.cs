using System;
using System.Windows;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Renders the active month background as a single, contiguous Direct3D polygon.
/// Eliminates all internal sub-pixel seams, overlaps, and grid lines between day cells.
/// </summary>
public class HabitMonthBackplate : FrameworkElement
{
    public static readonly DependencyProperty FirstDayOffsetProperty =
        DependencyProperty.Register(
            nameof(FirstDayOffset),
            typeof(int),
            typeof(HabitMonthBackplate),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DaysInMonthProperty =
        DependencyProperty.Register(
            nameof(DaysInMonth),
            typeof(int),
            typeof(HabitMonthBackplate),
            new FrameworkPropertyMetadata(30, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(
            nameof(FillBrush),
            typeof(Brush),
            typeof(HabitMonthBackplate),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int FirstDayOffset
    {
        get => (int)GetValue(FirstDayOffsetProperty);
        set => SetValue(FirstDayOffsetProperty, value);
    }

    public int DaysInMonth
    {
        get => (int)GetValue(DaysInMonthProperty);
        set => SetValue(DaysInMonthProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public HabitMonthBackplate()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (FillBrush == null || DaysInMonth <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        int totalPxX = (int)Math.Round(ActualWidth * dpiX);
        int totalPxY = (int)Math.Round(ActualHeight * dpiY);

        double[] xEdges = new double[8];
        for (int c = 0; c <= 7; c++)
        {
            xEdges[c] = Math.Round((double)c * totalPxX / 7.0) / dpiX;
        }

        double[] yEdges = new double[7];
        for (int r = 0; r <= 6; r++)
        {
            yEdges[r] = Math.Round((double)r * totalPxY / 6.0) / dpiY;
        }

        int startCol = Math.Clamp(FirstDayOffset, 0, 6);
        int lastDayIndex = startCol + DaysInMonth - 1;
        int endRow = Math.Clamp(lastDayIndex / 7, 0, 5);
        int endCol = Math.Clamp(lastDayIndex % 7, 0, 6);

        double x0 = xEdges[0];
        double xStart = xEdges[startCol];
        double xEnd = xEdges[endCol + 1];
        double x7 = xEdges[7];

        double y0 = yEdges[0];
        double y1 = yEdges[1];
        double yEnd = yEdges[endRow];
        double yEndPlus1 = yEdges[endRow + 1];

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(xStart, y0), isFilled: true, isClosed: true);

            // 1. Top edge of Row 0 to right edge
            ctx.LineTo(new Point(x7, y0), isStroked: false, isSmoothJoin: false);

            // 2. Down right edge to endRow
            ctx.LineTo(new Point(x7, yEnd), isStroked: false, isSmoothJoin: false);

            if (endCol < 6)
            {
                // Step in along top of remaining next-month days in endRow
                ctx.LineTo(new Point(xEnd, yEnd), isStroked: false, isSmoothJoin: false);
                // Down to bottom of endRow
                ctx.LineTo(new Point(xEnd, yEndPlus1), isStroked: false, isSmoothJoin: false);
            }
            else
            {
                // Month fills all 7 columns of endRow
                ctx.LineTo(new Point(x7, yEndPlus1), isStroked: false, isSmoothJoin: false);
            }

            // 3. Along bottom of endRow to left edge
            ctx.LineTo(new Point(x0, yEndPlus1), isStroked: false, isSmoothJoin: false);

            if (startCol > 0)
            {
                // Up to bottom of Row 0 (top of Row 1)
                ctx.LineTo(new Point(x0, y1), isStroked: false, isSmoothJoin: false);
                // Step in along bottom of previous-month days in Row 0
                ctx.LineTo(new Point(xStart, y1), isStroked: false, isSmoothJoin: false);
            }
            else
            {
                // Month starts at col 0, go all the way up to y0
                ctx.LineTo(new Point(x0, y0), isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(FillBrush, null, geometry);
    }
}
