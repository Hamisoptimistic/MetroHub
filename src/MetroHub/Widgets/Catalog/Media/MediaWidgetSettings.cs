namespace MetroHub.Widgets.Catalog.Media;

/// <summary>
/// Controls the visual render mode for the ambient radial glow.
/// </summary>
public enum MediaGlowMode
{
    Off = 0,
    Static = 1,
    Animated = 2
}

/// <summary>
/// Persisted configuration payload for the Media Widget.
/// </summary>
public class MediaWidgetSettings
{
    public MediaGlowMode GlowMode { get; set; } = MediaGlowMode.Static;
}
