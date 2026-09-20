namespace MetroHub.Widgets.Catalog.Dino;

/// <summary>
/// Persistent settings payload for the Chrome Dino Game widget.
/// Serialized with source-generated zero-reflection JSON (Phase 0.6).
/// </summary>
public sealed class DinoWidgetSettings
{
    public int HighScore { get; set; } = 0;
    public bool IsMuted { get; set; } = false;
    public bool? ReducedMotion { get; set; } = null; // null = follow Windows system setting
}
