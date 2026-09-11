using System.Collections.Generic;
using MetroHub.Core.Models;

namespace MetroHub.Widgets.Catalog.Stub;

/// <summary>
/// Minimal stub ViewModel representing a generic or uninitialized widget tile.
/// Implements IWidgetViewModel with customizable/declared allowed sizes (Phase 0.3).
/// Contains zero WPF UI dependencies.
/// </summary>
public class StubWidgetViewModel : IWidgetViewModel
{
    public TileModel Model { get; }

    public IReadOnlyList<WidgetSize> AllowedSizes { get; set; } = new List<WidgetSize>
    {
        WidgetSize.Small,    // 1x1
        WidgetSize.Medium,   // 2x2
        WidgetSize.Wide,     // 4x2
        WidgetSize.Tall,     // 2x4 (Vertical)
        WidgetSize.Large,    // 4x4 (Hero Square)
        WidgetSize.Banner    // 8x2 (Full Track Width)
    };

    public StubWidgetViewModel(TileModel model)
    {
        Model = model;
    }
}
