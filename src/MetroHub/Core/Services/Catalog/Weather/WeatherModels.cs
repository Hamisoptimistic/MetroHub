using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Core.Services.Catalog.Weather;

public sealed class LocationCacheEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public sealed class WeatherCacheEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string City { get; set; } = string.Empty;
    public OpenMeteoForecastResponse? Forecast { get; set; }
    public OpenMeteoAirQualityResponse? AirQuality { get; set; }
}

public sealed class IpApiResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }
}

public sealed class GeocodingSearchResponse
{
    [JsonPropertyName("results")]
    public List<GeocodingResultItem>? Results { get; set; }
}

public sealed class GeocodingResultItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("admin1")]
    public string? Admin1 { get; set; }
}

public sealed class OpenMeteoForecastResponse
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("current")]
    public CurrentWeatherDto? Current { get; set; }

    [JsonPropertyName("hourly")]
    public HourlyWeatherDto? Hourly { get; set; }

    [JsonPropertyName("daily")]
    public DailyWeatherDto? Daily { get; set; }
}

public sealed class CurrentWeatherDto
{
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("temperature_2m")]
    public double Temperature { get; set; }

    [JsonPropertyName("apparent_temperature")]
    public double ApparentTemperature { get; set; }

    [JsonPropertyName("relative_humidity_2m")]
    public int RelativeHumidity { get; set; }

    [JsonPropertyName("is_day")]
    public int IsDay { get; set; }

    [JsonPropertyName("precipitation")]
    public double Precipitation { get; set; }

    [JsonPropertyName("weather_code")]
    public int WeatherCode { get; set; }

    [JsonPropertyName("wind_speed_10m")]
    public double WindSpeed { get; set; }

    [JsonPropertyName("uv_index")]
    public double UvIndex { get; set; }
}

public sealed class HourlyWeatherDto
{
    [JsonPropertyName("time")]
    public List<string>? Time { get; set; }

    [JsonPropertyName("temperature_2m")]
    public List<double>? Temperature { get; set; }

    [JsonPropertyName("precipitation_probability")]
    public List<int>? PrecipitationProbability { get; set; }

    [JsonPropertyName("weather_code")]
    public List<int>? WeatherCode { get; set; }

    [JsonPropertyName("is_day")]
    public List<int>? IsDay { get; set; }

    [JsonPropertyName("uv_index")]
    public List<double>? UvIndex { get; set; }
}

public sealed class DailyWeatherDto
{
    [JsonPropertyName("time")]
    public List<string>? Time { get; set; }

    [JsonPropertyName("weather_code")]
    public List<int>? WeatherCode { get; set; }

    [JsonPropertyName("temperature_2m_max")]
    public List<double>? TemperatureMax { get; set; }

    [JsonPropertyName("temperature_2m_min")]
    public List<double>? TemperatureMin { get; set; }

    [JsonPropertyName("uv_index_max")]
    public List<double>? UvIndexMax { get; set; }

    [JsonPropertyName("precipitation_probability_max")]
    public List<int>? PrecipitationProbabilityMax { get; set; }
}

public sealed class OpenMeteoAirQualityResponse
{
    [JsonPropertyName("current")]
    public CurrentAirQualityDto? Current { get; set; }
}

public sealed class CurrentAirQualityDto
{
    [JsonPropertyName("us_aqi")]
    public int? UsAqi { get; set; }

    [JsonPropertyName("pm2_5")]
    public double? Pm25 { get; set; }

    [JsonPropertyName("pm10")]
    public double? Pm10 { get; set; }

    [JsonPropertyName("nitrogen_dioxide")]
    public double? NitrogenDioxide { get; set; }

    [JsonPropertyName("ozone")]
    public double? Ozone { get; set; }

    [JsonPropertyName("sulphur_dioxide")]
    public double? SulphurDioxide { get; set; }

    [JsonPropertyName("carbon_monoxide")]
    public double? CarbonMonoxide { get; set; }
}
