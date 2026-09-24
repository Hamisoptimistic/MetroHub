namespace MetroHub.Widgets.Catalog.Weather;

public enum WeatherBackgroundStyle
{
    AmbientGlow,
    HorizonAura,
    None
}

public class WeatherWidgetSettings
{
    public bool IsFahrenheit { get; set; } = false;
    public bool IsAutoLocation { get; set; } = true;
    public string? CustomCity { get; set; }
    public double? CustomLatitude { get; set; }
    public double? CustomLongitude { get; set; }
    public WeatherBackgroundStyle BackgroundStyle { get; set; } = WeatherBackgroundStyle.HorizonAura;

    // Backward compatibility for existing serialized JSON
    public bool? IsAmbientGlowEnabled { get; set; }
    public bool? IsHorizonAuraEnabled { get; set; }
}
