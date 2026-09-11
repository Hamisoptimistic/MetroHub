namespace MetroHub.Widgets.Catalog.Clock;

public enum ClockFontFace
{
    SegoeUI = 0,
    Monoton = 1,
    PixelifySans = 2,
    Doto = 3
}

/// <summary>
/// Persisted configuration payload for the Clock Widget.
/// </summary>
public class ClockWidgetSettings
{
    public bool Is24HourFormat { get; set; } = false;
    public ClockFontFace FontFace { get; set; } = ClockFontFace.SegoeUI;
}
