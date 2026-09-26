namespace MetroHub.Widgets.Catalog.Clock;

public enum ClockFontFace
{
    SegoeUI = 0,
    Monoton = 1,
    Digital7 = 2,
    FffForward = 3,
    KarnivoreDigit = 4,
    Now = 5
}

/// <summary>
/// Persisted configuration payload for the Clock Widget.
/// </summary>
public class ClockWidgetSettings
{
    public bool Is24HourFormat { get; set; } = false;
    public ClockFontFace FontFace { get; set; } = ClockFontFace.SegoeUI;
}
