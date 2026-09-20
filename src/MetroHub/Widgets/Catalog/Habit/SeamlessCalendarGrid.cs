using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// A high-precision, pixel-perfect uniform grid panel engineered for seamless calendar/heatmap matrices.
/// Precalculates cumulative integer physical pixel boundaries across rows and columns using device DPI,
/// mathematically eliminating sub-pixel rasterization gaps and overlaps between adjacent cells.
/// </summary>
public class SeamlessCalendarGrid : Panel
{
    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(
            nameof(Rows),
            typeof(int),
            typeof(SeamlessCalendarGrid),
            new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(SeamlessCalendarGrid),
            new FrameworkPropertyMetadata(7, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public SeamlessCalendarGrid()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Math.Max(1, Rows);
        int cols = Math.Max(1, Columns);

        Size childAvailable = new Size(
            double.IsPositiveInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / cols,
            double.IsPositiveInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height / rows);

        double maxChildW = 0.0;
        double maxChildH = 0.0;

        foreach (UIElement child in InternalChildren)
        {
            if (child == null) continue;
            child.Measure(childAvailable);
            if (child.DesiredSize.Width > maxChildW) maxChildW = child.DesiredSize.Width;
            if (child.DesiredSize.Height > maxChildH) maxChildH = child.DesiredSize.Height;
        }

        double desiredW = double.IsPositiveInfinity(availableSize.Width) ? maxChildW * cols : availableSize.Width;
        double desiredH = double.IsPositiveInfinity(availableSize.Height) ? maxChildH * rows : availableSize.Height;

        return new Size(desiredW, desiredH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int rows = Math.Max(1, Rows);
        int cols = Math.Max(1, Columns);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // Total available physical integer screen pixels
        int totalPxX = (int)Math.Round(finalSize.Width * dpiX);
        int totalPxY = (int)Math.Round(finalSize.Height * dpiY);

        // Precompute cumulative integer physical-pixel boundaries for columns
        int[] xPx = new int[cols + 1];
        for (int c = 0; c <= cols; c++)
        {
            xPx[c] = (int)Math.Round((double)c * totalPxX / cols);
        }
        xPx[cols] = totalPxX;

        // Precompute cumulative integer physical-pixel boundaries for rows
        int[] yPx = new int[rows + 1];
        for (int r = 0; r <= rows; r++)
        {
            yPx[r] = (int)Math.Round((double)r * totalPxY / rows);
        }
        yPx[rows] = totalPxY;

        // Convert physical pixel edges to exact WPF DIP coordinates
        double[] xDip = new double[cols + 1];
        for (int c = 0; c <= cols; c++)
        {
            xDip[c] = xPx[c] / dpiX;
        }

        double[] yDip = new double[rows + 1];
        for (int r = 0; r <= rows; r++)
        {
            yDip[r] = yPx[r] / dpiY;
        }

        int index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child == null) continue;

            int r = index / cols;
            int c = index % cols;

            if (r < rows)
            {
                double left = xDip[c];
                double right = xDip[c + 1];
                double top = yDip[r];
                double bottom = yDip[r + 1];

                double width = Math.Max(0, right - left);
                double height = Math.Max(0, bottom - top);

                child.Arrange(new Rect(left, top, width, height));
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }

            index++;
        }

        return finalSize;
    }
}
