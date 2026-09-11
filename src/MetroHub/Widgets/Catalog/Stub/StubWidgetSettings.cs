namespace MetroHub.Widgets.Catalog.Stub;

/// <summary>
/// Settings payload model for the Stub widget.
/// Demonstrates source-generated JSON serialization round-trip across app restart.
/// </summary>
public class StubWidgetSettings
{
    public string Label { get; set; } = "Stub Widget";
    public string BoxColor { get; set; } = "#2563EB";
    public int Counter { get; set; } = 0;
}

