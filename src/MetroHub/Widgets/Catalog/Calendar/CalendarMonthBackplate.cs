using System;
using System.Windows;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Calendar;

/// <summary>
/// Renders the active month background as a single, contiguous geometric polygon.
/// By drawing the entire month block as a single Direct3D primitive rather than 
/// 30 individual cell borders, internal sub-pixel seams, overlaps, and horizontal/vertical
/// grid lines are completely eliminated.
/// </summary>
public class CalendarMonthBackplate : FrameworkElement
{
    public static readonly DependencyProperty FirstDayOffsetProperty =
        DependencyProperty.Register(
            nameof(FirstDayOffset),
            typeof(int),
            typeof(CalendarMonthBackplate),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DaysInMonthProperty =
        DependencyProperty.Register(
            nameof(DaysInMonth),
            typeof(int),
            typeof(CalendarMonthBackplate),
            new FrameworkPropertyMetadata(30, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(
            nameof(FillBrush),
            typeof(Brush),
            typeof(CalendarMonthBackplate),
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

    public CalendarMonthBackplate()
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

        double colW = ActualWidth / 7.0;
        double rowH = ActualHeight / 6.0;

        int startCol = Math.Clamp(FirstDayOffset, 0, 6);
        int lastDayIndex = startCol + DaysInMonth - 1;
        int endRow = Math.Clamp(lastDayIndex / 7, 0, 5);
        int endCol = Math.Clamp(lastDayIndex % 7, 0, 6);

        double x0 = 0;
        double xStart = startCol * colW;
        double xEnd = (endCol + 1) * colW;
        double x7 = ActualWidth;

        double y0 = 0;
        double y1 = 1 * rowH;
        double yEnd = endRow * rowH;
        double yEndPlus1 = (endRow + 1) * rowH;

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
