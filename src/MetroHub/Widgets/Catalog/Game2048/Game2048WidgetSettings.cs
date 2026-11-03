using System.Collections.Generic;

namespace MetroHub.Widgets.Catalog.Game2048;

/// <summary>
/// Persistent settings and saved board state for the 2048 game widget.
/// Serialized with source-generated zero-reflection JSON.
/// </summary>
public sealed class Game2048WidgetSettings
{
    public int HighScore { get; set; }
    public int CurrentScore { get; set; }
    public List<int>? SavedGrid { get; set; }
}
