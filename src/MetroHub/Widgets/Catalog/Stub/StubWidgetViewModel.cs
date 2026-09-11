using MetroHub.Core.Models;

namespace MetroHub.Widgets.Catalog.Stub;

/// <summary>
/// Minimal stub ViewModel representing a generic or uninitialized widget tile.
/// Contains zero WPF UI dependencies.
/// </summary>
public class StubWidgetViewModel
{
    public TileModel Model { get; }

    public StubWidgetViewModel(TileModel model)
    {
        Model = model;
    }
}
