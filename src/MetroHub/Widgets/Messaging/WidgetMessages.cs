namespace MetroHub.Widgets.Messaging;

/// <summary>
/// Dispatched when MetroHub is shown or hidden (ShowScreen/HideScreen).
/// Widgets subscribe to pause/resume background timers or polling.
/// </summary>
public record HubVisibilityChangedMessage(bool IsVisible);

/// <summary>
/// Dispatched when widget settings are modified.
/// </summary>
public record WidgetSettingsChangedMessage(string TileId, string? SettingsJson);
