using System;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Persisted user settings for an individual Focus Radio widget tile instance.
/// Persisted via TileModel.SettingsJson through WidgetJsonContext and WidgetSerializer.
/// </summary>
public sealed class RadioWidgetSettings
{
    /// <summary>
    /// ID of the last active category tab ("ambient", "nature", "lofi", "coding").
    /// </summary>
    public string LastSelectedCategory { get; set; } = "ambient";

    /// <summary>
    /// ID of the last selected radio station.
    /// </summary>
    public string? LastStationId { get; set; }
}
