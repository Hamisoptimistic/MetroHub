namespace MetroHub.Widgets.Catalog.Stub;

/// <summary>
/// Settings payload model for the Stub widget.
/// Demonstrates source-generated JSON serialization round-trip.
/// </summary>
public class StubWidgetSettings
{
    public string? Note { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 60;
}
