namespace MetroHub.Core.Models;

/// <summary>
/// Specifies the artistic rendering style applied to the system accent color brushes.
/// </summary>
public enum AccentStyleMode
{
    /// <summary>
    /// Classic flat solid color matching standard Windows 11 presentation.
    /// </summary>
    Flat = 0,

    /// <summary>
    /// Apple Studio Glass / Fluent luminous gradient with a specular top catch and rich accent base.
    /// </summary>
    StudioGlass = 1
}
