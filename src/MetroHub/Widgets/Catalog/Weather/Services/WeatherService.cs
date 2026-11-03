using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MetroHub.Core.Services;

namespace MetroHub.Widgets.Catalog.Weather.Services;

public sealed class WeatherService
{
    private static string WeatherCachePath => AppPaths.ResolveCacheFile("v1_weather_cache.json");

    internal static HttpClient SharedHttpClient => HttpHelper.Client;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LocationService Location { get; }

    public WeatherService()
    {
        Location = new LocationService(SharedHttpClient);
    }

    private string? _lastSavedCurrentTime;

    public WeatherCacheEntry? GetCachedWeather()
    {
        try
        {
            if (File.Exists(WeatherCachePath))
            {
                string json = File.ReadAllText(WeatherCachePath);
                var entry = JsonSerializer.Deserialize<WeatherCacheEntry>(json, JsonOptions);
                _lastSavedCurrentTime = entry?.Forecast?.Current?.Time;
                return entry;
            }
        }
        catch
        {
            // Cache read error; ignore
        }

        return null;
    }

    public async Task<WeatherCacheEntry?> FetchWeatherAsync(
        double latitude,
        double longitude,
        string cityName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Format coordinates with invariant culture to prevent comma decimals in European locales
            string latStr = latitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            string lonStr = longitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

            // 1. Weather Forecast URL (Current conditions + today's high/low & max precipitation probability)
            string weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}" +
                                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,wind_speed_10m,uv_index" +
                                "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                                "&timezone=auto&forecast_days=1";

            // 2. Air Quality URL (Current US AQI)
            string aqiUrl = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={latStr}&longitude={lonStr}" +
                            "&current=us_aqi&timezone=auto";

            // Concurrent pure async fetch without Task.Run
            var weatherTask = FetchJsonAsync<OpenMeteoForecastResponse>(weatherUrl, cancellationToken);
            var aqiTask = FetchJsonAsync<OpenMeteoAirQualityResponse>(aqiUrl, cancellationToken);

            await Task.WhenAll(weatherTask, aqiTask).ConfigureAwait(false);

            var forecast = await weatherTask.ConfigureAwait(false);
            var airQuality = await aqiTask.ConfigureAwait(false);

            if (forecast == null) return GetMatchingCachedWeather(latitude, longitude);

            var entry = new WeatherCacheEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Latitude = latitude,
                Longitude = longitude,
                City = cityName,
                Forecast = forecast,
                AirQuality = airQuality
            };

            await SaveWeatherCacheAsync(entry, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        catch
        {
            // Network failure or cancellation; return cached if available and coordinates match
            return GetMatchingCachedWeather(latitude, longitude);
        }
    }

    private WeatherCacheEntry? GetMatchingCachedWeather(double latitude, double longitude)
    {
        var cached = GetCachedWeather();
        if (cached != null &&
            Math.Abs(cached.Latitude - latitude) < 0.05 &&
            Math.Abs(cached.Longitude - longitude) < 0.05)
        {
            return cached;
        }

        return null;
    }

    private static async Task<T?> FetchJsonAsync<T>(string url, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveWeatherCacheAsync(WeatherCacheEntry entry, CancellationToken cancellationToken)
    {
        var currentTime = entry.Forecast?.Current?.Time;
        if (currentTime != null && string.Equals(currentTime, _lastSavedCurrentTime, StringComparison.Ordinal))
        {
            return; // Identical forecast timeframe; avoid redundant disk rewrite
        }

        try
        {
            AppPaths.EnsureDirectory(WeatherCachePath);

            string json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(WeatherCachePath, json, cancellationToken).ConfigureAwait(false);
            _lastSavedCurrentTime = currentTime;
        }
        catch
        {
            // Cache write error
        }
    }
}
