namespace MetroHub.Widgets.Messaging;

/// <summary>
/// Dispatched when MetroHub is shown or hidden (ShowScreen/HideScreen).
/// Widgets subscribe to pause/resume background timers or polling.
/// </summary>
public record HubVisibilityChangedMessage(bool IsVisible);

/// <summary>
/// Dispatched to request all or a specific widget to refresh its data.
/// </summary>
public record WidgetRefreshRequestedMessage(string? WidgetId = null);

/// <summary>
/// Dispatched when widget settings are modified.
/// </summary>
public record WidgetSettingsChangedMessage(string TileId, string? SettingsJson);

/// <summary>
/// Generic metric update message for live-updating widgets without coupling.
/// </summary>
public record WidgetMetricUpdateMessage<T>(string MetricId, T Data);
