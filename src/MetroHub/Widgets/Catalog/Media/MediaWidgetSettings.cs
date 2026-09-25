namespace MetroHub.Widgets.Catalog.Media;

/// <summary>
/// Persisted configuration payload for the Media Widget.
/// </summary>
public class MediaWidgetSettings
{
    public bool IsAmbientGlowEnabled { get; set; } = true;

    /// <summary>
    /// WebNowPlaying adapter: when enabled, MetroHub hosts the WebSocket the WebNowPlaying
    /// browser extension connects to and browser media drives the seekbar directly.
    /// Enabled by default on port 8974.
    /// </summary>
    public bool WebNowPlayingEnabled { get; set; } = true;
}
