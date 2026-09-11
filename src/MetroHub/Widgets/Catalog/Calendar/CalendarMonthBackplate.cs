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

        var figure = new PathFigure
        {
            StartPoint = new Point(xStart, y0),
            IsClosed = true,
            IsFilled = true
        };

        // 1. Top edge of Row 0 to right edge
        figure.Segments.Add(new LineSegment(new Point(x7, y0), false));

        // 2. Down right edge to endRow
        figure.Segments.Add(new LineSegment(new Point(x7, yEnd), false));

        if (endCol < 6)
        {
            // Step in along top of remaining next-month days in endRow
            figure.Segments.Add(new LineSegment(new Point(xEnd, yEnd), false));
            // Down to bottom of endRow
            figure.Segments.Add(new LineSegment(new Point(xEnd, yEndPlus1), false));
        }
        else
        {
            // Month fills all 7 columns of endRow
            figure.Segments.Add(new LineSegment(new Point(x7, yEndPlus1), false));
        }

        // 3. Along bottom of endRow to left edge
        figure.Segments.Add(new LineSegment(new Point(x0, yEndPlus1), false));

        if (startCol > 0)
        {
            // Up to bottom of Row 0 (top of Row 1)
            figure.Segments.Add(new LineSegment(new Point(x0, y1), false));
            // Step in along bottom of previous-month days in Row 0
            figure.Segments.Add(new LineSegment(new Point(xStart, y1), false));
        }
        else
        {
            // Month starts at col 0, go all the way up to y0
            figure.Segments.Add(new LineSegment(new Point(x0, y0), false));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        dc.DrawGeometry(FillBrush, null, geometry);
    }
}
