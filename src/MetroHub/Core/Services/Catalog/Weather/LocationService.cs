using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Services.Catalog.Weather;

public sealed class LocationService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroHub");

    private static readonly string LocationCachePath = Path.Combine(AppDataDir, "v1_location_cache.json");

    private readonly HttpClient _httpClient;
    private static readonly TimeSpan LocationTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LocationService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<LocationCacheEntry> ResolveLocationAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check disk cache first (7-day TTL)
        var cached = LoadLocationFromCache();
        if (cached != null && (DateTime.UtcNow - cached.TimestampUtc) < LocationTtl)
        {
            return cached;
        }

        // 2. Try Windows WinRT Geolocator (graceful fall-through)
        var winRtLocation = await TryGetWinRtLocationAsync(cancellationToken).ConfigureAwait(false);
        if (winRtLocation != null)
        {
            SaveLocationToCache(winRtLocation);
            return winRtLocation;
        }

        // 3. Try IP Geolocation (ip-api.com with explicit status check)
        var ipLocation = await TryGetIpLocationAsync(cancellationToken).ConfigureAwait(false);
        if (ipLocation != null)
        {
            SaveLocationToCache(ipLocation);
            return ipLocation;
        }

        // 4. Fallback if offline or all fail: return cached if available, or default
        if (cached != null)
        {
            return cached;
        }

        return new LocationCacheEntry
        {
            Latitude = 51.5074,
            Longitude = -0.1278,
            City = "London",
            Country = "United Kingdom",
            TimestampUtc = DateTime.UtcNow
        };
    }

    private static async Task<LocationCacheEntry?> TryGetWinRtLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
            {
                return null;
            }

            var geolocator = new Windows.Devices.Geolocation.Geolocator
            {
                DesiredAccuracyInMeters = 10000
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var geoposition = await geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(60),
                timeout: TimeSpan.FromSeconds(3)
            ).AsTask(linkedCts.Token).ConfigureAwait(false);

            if (geoposition?.Coordinate?.Point?.Position != null)
            {
                return new LocationCacheEntry
                {
                    Latitude = Math.Round(geoposition.Coordinate.Point.Position.Latitude, 4),
                    Longitude = Math.Round(geoposition.Coordinate.Point.Position.Longitude, 4),
                    City = "My Location",
                    Country = string.Empty,
                    TimestampUtc = DateTime.UtcNow
                };
            }
        }
        catch
        {
            // Windows Location services may be disabled, disallowed, or throw on Windows N/LTSC editions.
            // Fall through gracefully.
        }

        return null;
    }

    private async Task<LocationCacheEntry?> TryGetIpLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                "http://ip-api.com/json/?fields=status,message,lat,lon,city,country",
                cancellationToken
            ).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var ipData = await JsonSerializer.DeserializeAsync<IpApiResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (ipData == null || !string.Equals(ipData.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (ipData.Lat.HasValue && ipData.Lon.HasValue)
            {
                return new LocationCacheEntry
                {
                    Latitude = Math.Round(ipData.Lat.Value, 4),
                    Longitude = Math.Round(ipData.Lon.Value, 4),
                    City = ipData.City ?? "Local",
                    Country = ipData.Country ?? string.Empty,
                    TimestampUtc = DateTime.UtcNow
                };
            }
        }
        catch
        {
            // Network failure or timeout; fall through gracefully.
        }

        return null;
    }

    public async Task<LocationCacheEntry?> SearchCityAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=1&language=en&format=json";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var geocoding = await JsonSerializer.DeserializeAsync<GeocodingSearchResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (geocoding?.Results != null && geocoding.Results.Count > 0)
            {
                var first = geocoding.Results[0];
                return new LocationCacheEntry
                {
                    Latitude = Math.Round(first.Latitude, 4),
                    Longitude = Math.Round(first.Longitude, 4),
                    City = first.Name,
                    Country = first.Country ?? string.Empty,
                    TimestampUtc = DateTime.UtcNow
                };
            }
        }
        catch
        {
            // Geocoding network error
        }

        return null;
    }

    private static LocationCacheEntry? LoadLocationFromCache()
    {
        try
        {
            if (File.Exists(LocationCachePath))
            {
                string json = File.ReadAllText(LocationCachePath);
                return JsonSerializer.Deserialize<LocationCacheEntry>(json, JsonOptions);
            }
        }
        catch
        {
            // Cache read error; ignore and re-resolve
        }

        return null;
    }

    public static void SaveLocationToCache(LocationCacheEntry entry)
    {
        try
        {
            if (!Directory.Exists(AppDataDir))
            {
                Directory.CreateDirectory(AppDataDir);
            }

            string json = JsonSerializer.Serialize(entry, JsonOptions);
            File.WriteAllText(LocationCachePath, json);
        }
        catch
        {
            // Disk write failure
        }
    }
}
