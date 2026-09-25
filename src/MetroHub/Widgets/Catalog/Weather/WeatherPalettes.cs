using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
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

public enum UvCategory
{
    Low,         // 0 - 2
    Moderate,    // 3 - 5
    High,        // 6 - 7
    VeryHigh,    // 8 - 10
    Extreme      // 11+
}

public sealed record AqiInfo(SolidColorBrush Brush, string Label);
public sealed record UvInfo(SolidColorBrush Brush, string Label);

/// <summary>
/// Condition metadata carrying the Meteocons icon slug, text description, frozen ambient glow, frozen horizon aura, and fallback glyph.
/// </summary>
public sealed record WeatherConditionInfo(
    string IconName,
    string Description,
    RadialGradientBrush Glow,
    LinearGradientBrush HorizonAura,
    string FallbackGlyph);

public static class WeatherPalettes
{
    // Pre-created frozen RadialGradientBrush instances for the ambient condition glow
    public static readonly RadialGradientBrush SunnyGlow;
    public static readonly RadialGradientBrush ClearNightGlow;
    public static readonly RadialGradientBrush CloudyGlow;
    public static readonly RadialGradientBrush RainGlow;
    public static readonly RadialGradientBrush SnowGlow;
    public static readonly RadialGradientBrush StormGlow;
    public static readonly RadialGradientBrush FogGlow;
    public static readonly RadialGradientBrush DefaultGlow;

    // Pre-created frozen LinearGradientBrush instances for the atmospheric horizon aura (bottom bleed)
    public static readonly LinearGradientBrush SunnyHorizonAura;
    public static readonly LinearGradientBrush ClearNightHorizonAura;
    public static readonly LinearGradientBrush CloudyHorizonAura;
    public static readonly LinearGradientBrush RainHorizonAura;
    public static readonly LinearGradientBrush SnowHorizonAura;
    public static readonly LinearGradientBrush StormHorizonAura;
    public static readonly LinearGradientBrush FogHorizonAura;
    public static readonly LinearGradientBrush HeatwaveHorizonAura;
    public static readonly LinearGradientBrush DefaultHorizonAura;

    // Pre-created static FrozenDictionary for AQI and UV lookups
    public static readonly FrozenDictionary<AqiCategory, AqiInfo> AqiPalette;
    public static readonly FrozenDictionary<UvCategory, UvInfo> UvPalette;

    static WeatherPalettes()
    {
        SunnyGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x48, 0xF5, 0x9E, 0x0B), // Warm daylight amber core
            Color.FromArgb(0x32, 0xD9, 0x77, 0x06), // Golden aura
            Color.FromArgb(0x1E, 0x78, 0x35, 0x0F), // Deep warm tone
            Color.FromArgb(0x12, 0x2A, 0x14, 0x06)  // Ambient warm dark base
        );

        ClearNightGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x4C, 0x8B, 0x5C, 0xF6), // Celestial violet-indigo core
            Color.FromArgb(0x35, 0x3B, 0x82, 0xF6), // Deep sapphire aura
            Color.FromArgb(0x22, 0x1E, 0x1B, 0x4B), // Midnight abyss
            Color.FromArgb(0x16, 0x0C, 0x0D, 0x24)  // Dark cosmos base
        );

        CloudyGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x3E, 0x64, 0x74, 0x8B), // Soft slate
            Color.FromArgb(0x2A, 0x47, 0x55, 0x69), // Steel overcast
            Color.FromArgb(0x1C, 0x33, 0x41, 0x55), // Deep graphite
            Color.FromArgb(0x12, 0x0F, 0x17, 0x2A)  // Dark slate base
        );

        RainGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x48, 0x02, 0x84, 0xC7), // Ocean rain azure
            Color.FromArgb(0x32, 0x03, 0x69, 0xA1), // Stormy oceanic
            Color.FromArgb(0x20, 0x0C, 0x4A, 0x6E), // Deep sea navy
            Color.FromArgb(0x14, 0x06, 0x1B, 0x2E)  // Dark ocean base
        );

        SnowGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x42, 0x38, 0xBD, 0xF8), // Glacial cyan
            Color.FromArgb(0x2E, 0x02, 0x84, 0xC7), // Ice blue
            Color.FromArgb(0x1E, 0x0C, 0x4A, 0x6E), // Arctic navy
            Color.FromArgb(0x14, 0x08, 0x1C, 0x2D)  // Dark frost base
        );

        StormGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x4C, 0x7C, 0x3A, 0xED), // Electric storm violet
            Color.FromArgb(0x32, 0x6D, 0x28, 0xD9), // Thunder purple
            Color.FromArgb(0x20, 0x3B, 0x07, 0x64), // Dark storm abyss
            Color.FromArgb(0x15, 0x14, 0x07, 0x2A)  // Deep storm base
        );

        FogGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x3A, 0x71, 0x71, 0x7A), // Ethereal mist
            Color.FromArgb(0x26, 0x52, 0x52, 0x5B), // Cool smoke
            Color.FromArgb(0x1A, 0x3F, 0x3F, 0x46), // Dark charcoal
            Color.FromArgb(0x12, 0x18, 0x18, 0x1B)  // Obsidian mist base
        );

        DefaultGlow = CreateAtmosphericGlow(
            Color.FromArgb(0x32, 0x47, 0x55, 0x69),
            Color.FromArgb(0x22, 0x33, 0x41, 0x55),
            Color.FromArgb(0x16, 0x1E, 0x29, 0x3B),
            Color.FromArgb(0x10, 0x0F, 0x17, 0x2A)
        );

        // Pre-frozen Horizon Aura linear gradients (horizontal color bloom, 100% GPU Direct3D)
        SunnyHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xEE, 0xF5, 0x9E, 0x0B), // Radiant amber gold
            Color.FromArgb(0xEE, 0xFB, 0x71, 0x85)  // Warm coral rose
        );

        ClearNightHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xE0, 0x8B, 0x5C, 0xF6), // Celestial violet
            Color.FromArgb(0xE0, 0x38, 0xBD, 0xF8)  // Midnight sapphire
        );

        CloudyHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xD0, 0x64, 0x74, 0x8B), // Soft slate
            Color.FromArgb(0xD0, 0x94, 0xA3, 0xB8)  // Steel mist
        );

        RainHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xEE, 0x06, 0xB6, 0xD4), // Electric cyan
            Color.FromArgb(0xEE, 0x25, 0x63, 0xEB)  // Ocean azure
        );

        SnowHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xE5, 0x2D, 0xD4, 0xBF), // Glacial mint
            Color.FromArgb(0xE5, 0x38, 0xBD, 0xF8)  // Arctic ice blue
        );

        StormHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xF0, 0x93, 0x33, 0xEA), // Neon storm violet
            Color.FromArgb(0xF0, 0x4F, 0x46, 0xE5)  // Deep indigo thunder
        );

        FogHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xD8, 0xA1, 0xA1, 0xAA), // Ethereal silver
            Color.FromArgb(0xD8, 0x71, 0x71, 0x7A)  // Charcoal smoke
        );

        HeatwaveHorizonAura = CreateHorizonBloom(
            Color.FromArgb(0xF5, 0xFF, 0xC4, 0x00), // Solar amber (left)
            Color.FromArgb(0xF5, 0xFF, 0x2D, 0x78), // Neon magenta core (center)
            Color.FromArgb(0xF5, 0xFF, 0x45, 0x3A)  // Ember red coral (right)
        );

        DefaultHorizonAura = CreateHorizonAura(
            Color.FromArgb(0xC0, 0x47, 0x55, 0x69),
            Color.FromArgb(0xC0, 0x33, 0x41, 0x55)
        );

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

        var uvDict = new Dictionary<UvCategory, UvInfo>
        {
            [UvCategory.Low] = new(CreateFrozenBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), "Low"),
            [UvCategory.Moderate] = new(CreateFrozenBrush(Color.FromRgb(0xFF, 0xD6, 0x00)), "Moderate"),
            [UvCategory.High] = new(CreateFrozenBrush(Color.FromRgb(0xFF, 0x91, 0x00)), "High"),
            [UvCategory.VeryHigh] = new(CreateFrozenBrush(Color.FromRgb(0xFF, 0x52, 0x52)), "Very High"),
            [UvCategory.Extreme] = new(CreateFrozenBrush(Color.FromRgb(0xAA, 0x00, 0xFF)), "Extreme")
        };
        UvPalette = uvDict.ToFrozenDictionary();
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateHorizonAura(Color leftColor, Color rightColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(leftColor, 0.0),
                new GradientStop(rightColor, 1.0)
            }
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Tri-color heat-bloom aura (amber → magenta → ember): the neon heat-map bleed.
    /// Same cost class as <see cref="CreateHorizonAura"/> — one frozen brush, GPU-shaded.
    /// </summary>
    private static LinearGradientBrush CreateHorizonBloom(Color leftColor, Color midColor, Color rightColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(leftColor, 0.0),
                new GradientStop(midColor, 0.5),
                new GradientStop(rightColor, 1.0)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush CreateAtmosphericGlow(
        Color coreColor,
        Color midColor,
        Color outerColor,
        Color baseColor)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.48, 0.40),
            Center = new Point(0.48, 0.40),
            RadiusX = 1.35,
            RadiusY = 1.55,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(coreColor, 0.0),
                new GradientStop(midColor, 0.38),
                new GradientStop(outerColor, 0.72),
                new GradientStop(baseColor, 1.0)
            }
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Resolves condition metadata for an Open-Meteo WMO weather code. Returns shared frozen instances.
    /// Never returns null or throws.
    /// </summary>
    public static WeatherConditionInfo GetConditionInfo(int weatherCode, bool isDay, double? tempC = null)
    {
        var sunnyHorizon = (tempC.HasValue && tempC.Value >= 33.0) ? HeatwaveHorizonAura : SunnyHorizonAura;

        return weatherCode switch
        {
            // Clear sky
            0 => isDay
                ? new WeatherConditionInfo("clear-day", "Clear Sky", SunnyGlow, sunnyHorizon, "\uE9C4")
                : new WeatherConditionInfo("clear-night", "Clear Night", ClearNightGlow, ClearNightHorizonAura, "\uE708"),

            // Mainly clear
            1 => isDay
                ? new WeatherConditionInfo("mostly-clear-day", "Mainly Clear", SunnyGlow, sunnyHorizon, "\uE9C5")
                : new WeatherConditionInfo("mostly-clear-night", "Mainly Clear", ClearNightGlow, ClearNightHorizonAura, "\uE708"),

            // Partly cloudy
            2 => isDay
                ? new WeatherConditionInfo("partly-cloudy-day", "Partly Cloudy", CloudyGlow, CloudyHorizonAura, "\uE9C5")
                : new WeatherConditionInfo("partly-cloudy-night", "Partly Cloudy", ClearNightGlow, ClearNightHorizonAura, "\uE708"),

            // Overcast
            3 => new WeatherConditionInfo("overcast", "Overcast", CloudyGlow, CloudyHorizonAura, "\uE9C6"),

            // Fog and depositing rime fog
            45 or 48 => new WeatherConditionInfo("fog", "Foggy", FogGlow, FogHorizonAura, "\uE9C6"),

            // Drizzle
            51 or 53 or 55 => new WeatherConditionInfo("drizzle", "Light Drizzle", RainGlow, RainHorizonAura, "\uE9C7"),

            // Freezing drizzle
            56 or 57 => new WeatherConditionInfo("sleet", "Freezing Drizzle", SnowGlow, SnowHorizonAura, "\uE9C8"),

            // Rain: Slight, moderate, heavy
            61 or 63 => isDay
                ? new WeatherConditionInfo("rain", "Rain", RainGlow, RainHorizonAura, "\uE9C7")
                : new WeatherConditionInfo("night-rain", "Rain", RainGlow, RainHorizonAura, "\uE708"),
            65 => new WeatherConditionInfo("heavy-rain", "Heavy Rain", RainGlow, RainHorizonAura, "\uE9C7"),

            // Freezing rain
            66 or 67 => new WeatherConditionInfo("sleet", "Freezing Rain", SnowGlow, SnowHorizonAura, "\uE9C8"),

            // Snow fall
            71 or 73 => new WeatherConditionInfo("snow", "Snow Fall", SnowGlow, SnowHorizonAura, "\uE9C8"),
            75 or 77 => new WeatherConditionInfo("snow", "Heavy Snow", SnowGlow, SnowHorizonAura, "\uE9C8"),

            // Rain showers
            80 or 81 => isDay
                ? new WeatherConditionInfo("rain", "Rain Showers", RainGlow, RainHorizonAura, "\uE9C7")
                : new WeatherConditionInfo("night-rain", "Rain Showers", RainGlow, RainHorizonAura, "\uE708"),
            82 => new WeatherConditionInfo("heavy-rain", "Violent Rain Showers", RainGlow, RainHorizonAura, "\uE9C7"),

            // Snow showers
            85 or 86 => new WeatherConditionInfo("snow", "Snow Showers", SnowGlow, SnowHorizonAura, "\uE9C8"),

            // Thunderstorm
            95 => isDay
                ? new WeatherConditionInfo("thunderstorms", "Thunderstorm", StormGlow, StormHorizonAura, "\uE9C9")
                : new WeatherConditionInfo("thunderstorms-night", "Thunderstorm", StormGlow, StormHorizonAura, "\uE9C9"),

            // Thunderstorm with hail / heavy rain
            96 or 99 => new WeatherConditionInfo("thunderstorms-rain", "Severe Thunderstorm", StormGlow, StormHorizonAura, "\uE9C9"),

            // Unmapped / Fallback
            _ => HandleUnknownWeatherCode(weatherCode)
        };
    }

    private static WeatherConditionInfo HandleUnknownWeatherCode(int weatherCode)
    {
        Debug.WriteLine($"[WeatherPalettes] Warning: Received unmapped WMO weather code: {weatherCode}. Falling back to 'not-available'.");
        return new WeatherConditionInfo("not-available", "Unknown", DefaultGlow, DefaultHorizonAura, "\uE9C5");
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

    public static UvCategory GetUvCategory(double uvIndex) => uvIndex switch
    {
        < 3.0 => UvCategory.Low,
        < 6.0 => UvCategory.Moderate,
        < 8.0 => UvCategory.High,
        < 11.0 => UvCategory.VeryHigh,
        _ => UvCategory.Extreme
    };
}
