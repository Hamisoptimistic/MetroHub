using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Weather;

public enum AqiCategory
{
    Good,               // 0 - 50
    Moderate,           // 51 - 100
    UnhealthySensitive, // 101 - 150
    Unhealthy,          // 151 - 200
    VeryUnhealthy,      // 201 - 300
    Hazardous           // 301+
}

public sealed record AqiInfo(SolidColorBrush Brush, string Label);

public static class WeatherPalettes
{
    // Pre-created 5 frozen RadialGradientBrush instances for the ambient condition glow
    public static readonly RadialGradientBrush SunnyGlow;
    public static readonly RadialGradientBrush CloudyGlow;
    public static readonly RadialGradientBrush RainyGlow;
    public static readonly RadialGradientBrush SnowyGlow;
    public static readonly RadialGradientBrush ClearNightGlow;

    // Pre-created static FrozenDictionary for AQI lookups
    public static readonly FrozenDictionary<AqiCategory, AqiInfo> AqiPalette;

    static WeatherPalettes()
    {
        SunnyGlow = CreateFrozenRadialGlow(Color.FromArgb(0x55, 0xFF, 0xA0, 0x00), Color.FromArgb(0x00, 0xFF, 0xA0, 0x00));
        CloudyGlow = CreateFrozenRadialGlow(Color.FromArgb(0x44, 0x90, 0xA4, 0xAE), Color.FromArgb(0x00, 0x90, 0xA4, 0xAE));
        RainyGlow = CreateFrozenRadialGlow(Color.FromArgb(0x55, 0x00, 0xB0, 0xFF), Color.FromArgb(0x00, 0x00, 0xB0, 0xFF));
        SnowyGlow = CreateFrozenRadialGlow(Color.FromArgb(0x44, 0x80, 0xDE, 0xEA), Color.FromArgb(0x00, 0x80, 0xDE, 0xEA));
        ClearNightGlow = CreateFrozenRadialGlow(Color.FromArgb(0x55, 0x7C, 0x4D, 0xFF), Color.FromArgb(0x00, 0x7C, 0x4D, 0xFF));

        var aqiDict = new Dictionary<AqiCategory, AqiInfo>
        {
            [AqiCategory.Good] = new(CreateFrozenBrush(Color.FromRgb(0x00, 0xE6, 0x76)), "Good"),
            [AqiCategory.Moderate] = new(CreateFrozenBrush(Color.FromRgb(0xFF, 0xD6, 0x00)), "Moderate"),
            [AqiCategory.UnhealthySensitive] = new(CreateFrozenBrush(Color.FromRgb(0xFF, 0x91, 0x00)), "Sensitive"),
            [AqiCategory.Unhealthy] = new(CreateFrozenBrush(Color.FromRgb(0xFF, 0x52, 0x52)), "Unhealthy"),
            [AqiCategory.VeryUnhealthy] = new(CreateFrozenBrush(Color.FromRgb(0xD5, 0x00, 0xF9)), "Very Unhealthy"),
            [AqiCategory.Hazardous] = new(CreateFrozenBrush(Color.FromRgb(0xB7, 0x1C, 0x1C)), "Hazardous")
        };

        AqiPalette = aqiDict.ToFrozenDictionary();
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush CreateFrozenRadialGlow(Color centerColor, Color edgeColor)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.8, 0.3),
            Center = new Point(0.8, 0.3),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(centerColor, 0.0),
                new GradientStop(edgeColor, 1.0)
            }
        };
        brush.Freeze();
        return brush;
    }

    public static (string Glyph, string Description, RadialGradientBrush Glow) GetConditionInfo(int weatherCode, bool isDay)
    {
        return weatherCode switch
        {
            // Clear sky
            0 => isDay
                ? ("\uE9C4", "Clear Sky", SunnyGlow)       // Sun
                : ("\uE708", "Clear Night", ClearNightGlow),// Moon

            // Mainly clear, partly cloudy, overcast
            1 => isDay
                ? ("\uE9C5", "Mainly Clear", SunnyGlow)
                : ("\uE708", "Mainly Clear", ClearNightGlow),
            2 => ("\uE9C5", "Partly Cloudy", CloudyGlow),
            3 => ("\uE9C6", "Overcast", CloudyGlow),

            // Fog and depositing rime fog
            45 or 48 => ("\uE9C6", "Foggy", CloudyGlow),

            // Drizzle
            51 or 53 or 55 => ("\uE9C7", "Light Drizzle", RainyGlow),
            56 or 57 => ("\uE9C8", "Freezing Drizzle", SnowyGlow),

            // Rain: Slight, moderate, heavy
            61 or 63 => ("\uE9C7", "Rain", RainyGlow),
            65 => ("\uE9C7", "Heavy Rain", RainyGlow),
            66 or 67 => ("\uE9C8", "Freezing Rain", SnowyGlow),

            // Snow fall
            71 or 73 => ("\uE9C8", "Snow Fall", SnowyGlow),
            75 or 77 => ("\uE9C8", "Heavy Snow", SnowyGlow),

            // Rain showers
            80 or 81 or 82 => ("\uE9C7", "Rain Showers", RainyGlow),

            // Snow showers
            85 or 86 => ("\uE9C8", "Snow Showers", SnowyGlow),

            // Thunderstorm
            95 => ("\uE9C9", "Thunderstorm", RainyGlow),
            96 or 99 => ("\uE9C9", "Severe Thunderstorm", RainyGlow),

            _ => ("\uE9C5", "Partly Cloudy", CloudyGlow)
        };
    }

    public static AqiCategory GetAqiCategory(int usAqi) => usAqi switch
    {
        <= 50 => AqiCategory.Good,
        <= 100 => AqiCategory.Moderate,
        <= 150 => AqiCategory.UnhealthySensitive,
        <= 200 => AqiCategory.Unhealthy,
        <= 300 => AqiCategory.VeryUnhealthy,
        _ => AqiCategory.Hazardous
    };
}
