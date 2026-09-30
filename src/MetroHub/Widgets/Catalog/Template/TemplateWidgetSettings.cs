namespace MetroHub.Widgets.Catalog.Template;

/// <summary>
/// Settings payload model for the Template widget.
/// Demonstrates source-generated JSON serialization round-trip across app restart.
/// Any properties added here will be automatically serialized to JSON in TileModel.SettingsJson.
/// </summary>
public class TemplateWidgetSettings
{
    public string Label { get; set; } = "Template Widget";
    public string BoxColor { get; set; } = "#2563EB";
    public int Counter { get; set; } = 0;
    public bool IsFeatureEnabled { get; set; } = true;
}
