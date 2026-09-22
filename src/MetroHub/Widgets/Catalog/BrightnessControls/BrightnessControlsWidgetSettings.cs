namespace MetroHub.Widgets.Catalog.BrightnessControls;

public class BrightnessControlsWidgetSettings
{
    public bool IsLinked { get; set; } = true;
    public bool IsNightMode { get; set; }
    public double DayBrightness { get; set; } = 80.0;
    public double NightBrightness { get; set; } = 25.0;
}
