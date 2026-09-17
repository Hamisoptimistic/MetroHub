namespace MetroHub.Widgets.Catalog.Weather;

public class WeatherWidgetSettings
{
    public bool IsFahrenheit { get; set; } = false;
    public bool IsAutoLocation { get; set; } = true;
    public string? CustomCity { get; set; }
    public double? CustomLatitude { get; set; }
    public double? CustomLongitude { get; set; }
}
