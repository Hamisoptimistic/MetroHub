using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.MetroSync.Controls;

/// <summary>
/// A lightweight panel implementing Telegram Desktop's exact chat bubble layout algorithm:
/// 1. If text and timestamp fit on a single line, they are placed side-by-side with no wasted vertical space.
/// 2. If multi-line, checks if the last line of text has room for the timestamp; if so, tucks the timestamp
///    into the trailing whitespace of the last line without increasing bubble height.
/// 3. If the last line is completely full, wraps the timestamp directly beneath, docked bottom-right.
/// </summary>
public sealed class MetroSyncBubblePanel : Panel
{
    private const double Spacing = 8.0;
    private const double SingleLineHeightThreshold = 26.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return new Size(0, 0);
        }

        UIElement content = InternalChildren[0];
        UIElement? time = InternalChildren.Count > 1 ? InternalChildren[1] : null;

        double maxBubbleWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? 340.0
            : availableSize.Width;

        // Measure time element first to know its dimensions
        Size timeSize = new Size(0, 0);
        if (time != null)
        {
            time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            timeSize = time.DesiredSize;
        }

        // Measure content element with max bubble width
        content.Measure(new Size(maxBubbleWidth, double.PositiveInfinity));
        Size contentSize = content.DesiredSize;

        if (time == null)
        {
            return contentSize;
        }

        var (isSingleLine, lastLineWidth) = InspectContent(content);

        // Case 1: Single line fit (Text and Time on the same line)
        if (isSingleLine && (contentSize.Width + Spacing + timeSize.Width <= maxBubbleWidth))
        {
            double width = contentSize.Width + Spacing + timeSize.Width;
            double height = Math.Max(contentSize.Height, timeSize.Height);
            return new Size(width, height);
        }

        // Case 2: Multi-line text. Check if the last line has trailing space for the timestamp
        if (lastLineWidth + Spacing + timeSize.Width <= maxBubbleWidth)
        {
            double desiredWidth = Math.Min(maxBubbleWidth, Math.Max(contentSize.Width, lastLineWidth + Spacing + timeSize.Width));
            return new Size(desiredWidth, contentSize.Height);
        }

        // Case 3: Forced wrap (Last line is full, timestamp drops directly below)
        double wrapWidth = Math.Min(maxBubbleWidth, Math.Max(contentSize.Width, timeSize.Width));
        double wrapHeight = contentSize.Height + timeSize.Height + 1.0;
        return new Size(wrapWidth, wrapHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        UIElement content = InternalChildren[0];
        UIElement? time = InternalChildren.Count > 1 ? InternalChildren[1] : null;

        if (time == null)
        {
            content.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }

        Size contentSize = content.DesiredSize;
        Size timeSize = time.DesiredSize;

        var (isSingleLine, lastLineWidth) = InspectContent(content);

        // 1. Single-line arrangement: dock time right next to content with standard Spacing
        if (isSingleLine && (contentSize.Width + Spacing + timeSize.Width <= finalSize.Width + 0.5))
        {
            content.Arrange(new Rect(0, 0, contentSize.Width, finalSize.Height));
            double timeX = contentSize.Width + Spacing;
            double timeY = Math.Max(0, finalSize.Height - timeSize.Height);
            time.Arrange(new Rect(timeX, timeY, timeSize.Width, timeSize.Height));
            return finalSize;
        }

        // 2. Multi-line arrangement with trailing space on last line
        if (lastLineWidth + Spacing + timeSize.Width <= finalSize.Width + 0.5)
        {
            double arrangeWidth = content is Grid ? Math.Max(contentSize.Width, finalSize.Width) : contentSize.Width;
            content.Arrange(new Rect(0, 0, arrangeWidth, contentSize.Height));
            double timeX = finalSize.Width - timeSize.Width;
            double timeY = Math.Max(0, contentSize.Height - timeSize.Height);
            time.Arrange(new Rect(timeX, timeY, timeSize.Width, timeSize.Height));
            return finalSize;
        }

        // 3. Forced wrap arrangement
        double wrapContentWidth = content is Grid ? Math.Max(contentSize.Width, finalSize.Width) : contentSize.Width;
        content.Arrange(new Rect(0, 0, wrapContentWidth, contentSize.Height));
        double wrapTimeX = finalSize.Width - timeSize.Width;
        double wrapTimeY = contentSize.Height + 1.0;
        time.Arrange(new Rect(wrapTimeX, wrapTimeY, timeSize.Width, timeSize.Height));

        return finalSize;
    }

    private static (bool isSingleLine, double lastLineWidth) InspectContent(UIElement content)
    {
        if (content is MetroSyncSelectableTextBlock direct)
        {
            return (direct.IsSingleLine, direct.LastLineWidth);
        }

        if (content is Panel p)
        {
            double extraWidth = 0;
            MetroSyncSelectableTextBlock? found = null;
            foreach (UIElement child in p.Children)
            {
                if (child is MetroSyncSelectableTextBlock stb)
                {
                    found = stb;
                }
                else if (child.Visibility == Visibility.Visible)
                {
                    extraWidth += child.DesiredSize.Width;
                    if (child is FrameworkElement fe)
                    {
                        extraWidth += fe.Margin.Left + fe.Margin.Right;
                    }
                }
            }

            if (found != null)
            {
                return (found.IsSingleLine, extraWidth + found.LastLineWidth);
            }
        }

        bool singleLine = content.DesiredSize.Height <= SingleLineHeightThreshold;
        return (singleLine, content.DesiredSize.Width);
    }
}
