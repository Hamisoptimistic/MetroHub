namespace MetroHub.Presentation.Controls;

using System;
using System.Windows;
using System.Windows.Controls;

/// <summary>
/// A uniform grid panel with explicit inter-item column and row gaps.
/// Left-most and right-most tiles touch the panel boundaries (zero outer padding),
/// ensuring pixel-perfect alignment with surrounding headers and search controls.
/// </summary>
public class SpacedUniformGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(SpacedUniformGrid),
            new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnGapProperty =
        DependencyProperty.Register(
            nameof(ColumnGap),
            typeof(double),
            typeof(SpacedUniformGrid),
            new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty RowGapProperty =
        DependencyProperty.Register(
            nameof(RowGap),
            typeof(double),
            typeof(SpacedUniformGrid),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int cols = Math.Max(1, Columns);
        int count = InternalChildren.Count;
        if (count == 0) return new Size(0, 0);

        int rows = (int)Math.Ceiling((double)count / cols);
        double totalColGap = (cols - 1) * ColumnGap;

        double cellWidth;
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > totalColGap)
        {
            cellWidth = (availableSize.Width - totalColGap) / cols;
        }
        else
        {
            cellWidth = 52.0;
        }

        cellWidth = Math.Max(0, cellWidth);

        double maxChildHeight = 0;
        Size childConstraint = new Size(cellWidth, double.PositiveInfinity);

        foreach (UIElement child in InternalChildren)
        {
            if (child == null) continue;
            child.Measure(childConstraint);
            if (child.DesiredSize.Height > maxChildHeight)
            {
                maxChildHeight = child.DesiredSize.Height;
            }
        }

        if (maxChildHeight <= 0) maxChildHeight = 48.0;

        double totalWidth = (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
            ? availableSize.Width
            : cols * cellWidth + totalColGap;

        double totalHeight = rows * maxChildHeight + (rows - 1) * RowGap;

        return new Size(totalWidth, Math.Max(0, totalHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Math.Max(1, Columns);
        int count = InternalChildren.Count;
        if (count == 0) return finalSize;

        double totalColGap = (cols - 1) * ColumnGap;
        double cellWidth = Math.Max(0, (finalSize.Width - totalColGap) / cols);

        double maxChildHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child != null && child.DesiredSize.Height > maxChildHeight)
            {
                maxChildHeight = child.DesiredSize.Height;
            }
        }
        if (maxChildHeight <= 0) maxChildHeight = 48.0;

        for (int i = 0; i < count; i++)
        {
            UIElement child = InternalChildren[i];
            if (child == null) continue;

            int col = i % cols;
            int row = i / cols;

            double x = col * (cellWidth + ColumnGap);
            double y = row * (maxChildHeight + RowGap);

            child.Arrange(new Rect(x, y, cellWidth, maxChildHeight));
        }

        return finalSize;
    }
}
