namespace MetroHub.Widgets;

/// <summary>
/// Represents a grid span dimension and display name supported by a widget (Phase 0.3).
/// Declares the standard size palette for MetroHub widgets.
/// Individual widgets choose which subset of these sizes they permit.
/// </summary>
public record WidgetSize(int SpanX, int SpanY, string DisplayName)
{
    // Square & Standard Compact
    public static readonly WidgetSize Small = new(1, 1, "Small (1x1)");
    public static readonly WidgetSize Medium = new(2, 2, "Medium (2x2)");

    // Wide & Horizontal Banners
    public static readonly WidgetSize SlimWide = new(4, 1, "Slim Wide (4x1)");
    public static readonly WidgetSize Wide = new(4, 2, "Wide (4x2)");
    public static readonly WidgetSize ExtraWide = new(6, 2, "Extra Wide (6x2)");
    public static readonly WidgetSize Banner = new(8, 2, "Banner (8x2)");
    public static readonly WidgetSize Banner3 = new(8, 3, "Banner (8x3)");

    // Tall & Vertical Cards
    public static readonly WidgetSize SmallTall = new(1, 2, "Small Tall (1x2)");
    public static readonly WidgetSize Tall = new(2, 4, "Tall (2x4)");
    public static readonly WidgetSize ExtraTall = new(2, 6, "Extra Tall (2x6)");

    // Large & Hero Dashboards
    public static readonly WidgetSize Large = new(4, 4, "Large (4x4)");
    public static readonly WidgetSize LargeWide = new(6, 4, "Large Wide (6x4)");
    public static readonly WidgetSize Mega = new(8, 4, "Mega (8x4)");
    public static readonly WidgetSize Huge = new(8, 6, "Huge (8x6)");

    public override string ToString() => DisplayName;
}
