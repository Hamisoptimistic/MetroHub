using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public class SpacedUniformGridTests
{
    [Fact]
    public void SpacedUniformGrid_CalculatesCellWidthAndZeroOuterGaps()
    {
        WpfTestHost.RunSta(() =>
        {
            var grid = new SpacedUniformGrid
            {
                Columns = 5,
                ColumnGap = 9.0,
                RowGap = 8.0
            };

            // Add 5 items (1 full row)
            for (int i = 0; i < 5; i++)
            {
                grid.Children.Add(new Rectangle { Height = 48 });
            }

            // Measure with search bar width (296)
            grid.Measure(new Size(296, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, 296, 48));

            // Verify first child starts at 0 and has width 52
            var child0 = grid.Children[0];
            var bounds0 = child0.RenderSize;
            Assert.Equal(52.0, bounds0.Width);
            Assert.Equal(48.0, bounds0.Height);

            // Verify gaps between consecutive children are exactly 9.0
            var child1 = grid.Children[1];
            var child2 = grid.Children[2];
            var child3 = grid.Children[3];
            var child4 = grid.Children[4];

            Point p0 = child0.TranslatePoint(new Point(0, 0), grid);
            Point p1 = child1.TranslatePoint(new Point(0, 0), grid);
            Point p2 = child2.TranslatePoint(new Point(0, 0), grid);
            Point p3 = child3.TranslatePoint(new Point(0, 0), grid);
            Point p4 = child4.TranslatePoint(new Point(0, 0), grid);

            Assert.Equal(0.0, p0.X);
            Assert.Equal(61.0, p1.X);
            Assert.Equal(122.0, p2.X);
            Assert.Equal(183.0, p3.X);
            Assert.Equal(244.0, p4.X);

            // Right edge of child 4 is 244 + 52 = 296 (exact alignment with right boundary)
            Assert.Equal(296.0, p4.X + child4.RenderSize.Width);
        });
    }

    [Fact]
    public void SpacedUniformGrid_MultiRowArrangement_AlignsColumnsVertically()
    {
        WpfTestHost.RunSta(() =>
        {
            var grid = new SpacedUniformGrid
            {
                Columns = 5,
                ColumnGap = 9.0,
                RowGap = 8.0
            };

            // Add 13 items (2 full rows + 3 in row 3)
            for (int i = 0; i < 13; i++)
            {
                grid.Children.Add(new Rectangle { Height = 48 });
            }

            grid.Measure(new Size(296, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, 296, 3 * 48 + 2 * 8));

            // Check item in row 2 (index 10) matches column 0 x-coordinate
            Point pRow0Col0 = grid.Children[0].TranslatePoint(new Point(0, 0), grid);
            Point pRow1Col0 = grid.Children[5].TranslatePoint(new Point(0, 0), grid);
            Point pRow2Col0 = grid.Children[10].TranslatePoint(new Point(0, 0), grid);

            Assert.Equal(pRow0Col0.X, pRow1Col0.X);
            Assert.Equal(pRow0Col0.X, pRow2Col0.X);

            // Vertical gap between rows is exactly 8.0
            Assert.Equal(0.0, pRow0Col0.Y);
            Assert.Equal(48.0 + 8.0, pRow1Col0.Y);
            Assert.Equal(2 * (48.0 + 8.0), pRow2Col0.Y);
        });
    }
}
