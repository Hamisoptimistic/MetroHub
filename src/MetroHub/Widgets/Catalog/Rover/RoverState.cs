namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// High-level behavioral states for Rover.
/// </summary>
public enum RoverState
{
    /// <summary>
    /// Rover is sitting watchfully at rest, occasionally sniffing, scratching, or wagging his tail.
    /// </summary>
    Idle,

    /// <summary>
    /// User has been inactive for > 2 minutes: Rover is curled up fast asleep.
    /// </summary>
    Sleeping,

    /// <summary>
    /// User clicked or hovered on Rover: happy barking, panting, and tail wagging.
    /// </summary>
    Petted,

    /// <summary>
    /// User just resumed input or hovered nearby: Rover looks up attentively.
    /// </summary>
    Alert,

    /// <summary>
    /// User is performing a search query: Rover digs furiously in the dirt!
    /// </summary>
    Digging,

    /// <summary>
    /// Rover is performing a celebratory or fun trick (celebrity shades, sports, cooking, etc.).
    /// </summary>
    Trick,

    /// <summary>
    /// Windows media is actively playing: Rover perks up his ears and jams along.
    /// </summary>
    ListeningToMusic
}
