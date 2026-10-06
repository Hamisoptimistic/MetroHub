using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models.Geocoding;
using MetroHub.Core.Services.Catalog.Weather;

namespace MetroHub.Core.Services;

/// <summary>
/// Service providing asynchronous geocoding search with Open-Meteo and Photon fallback,
/// query parsing, validation, deduplication, and memory caching.
/// </summary>
public sealed class WeatherLocationService
{
    private static readonly Lazy<WeatherLocationService> s_instance = new(() => new WeatherLocationService());
    public static WeatherLocationService Instance => s_instance.Value;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, IReadOnlyList<GeoResult>> _queryCache = new(50, StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    private WeatherLocationService() { }

    public async Task<IReadOnlyList<GeoResult>> SearchLocationsAsync(string rawQuery, CancellationToken token = default)
    {
        string? normalized = NormalizeQuery(rawQuery);
        if (string.IsNullOrEmpty(normalized) || !normalized.Any(char.IsLetterOrDigit))
        {
            return [];
        }

        var (location, qualifier) = ParseQuery(normalized);
        if (!LongEnough(location))
        {
            return [];
        }

        lock (_cacheLock)
        {
            if (_queryCache.TryGetValue(normalized, out var cached))
            {
                return cached;
            }
        }

        (IReadOnlyList<GeoResult> Results, int RawCount) openMeteoResult = ([], 0);
        try
        {
            openMeteoResult = await GeocodeOpenMeteoAsync(location, qualifier, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            openMeteoResult = ([], 0);
        }

        if (token.IsCancellationRequested) return [];

        IReadOnlyList<GeoResult> results = openMeteoResult.Results;

        // Photon is called ONLY when Open-Meteo returned 0 results before any client-side qualifier filtering
        // and only if the query contains letters
        if (openMeteoResult.RawCount == 0 && normalized.Any(char.IsLetter))
        {
            try
            {
                results = await GeocodePhotonAsync(normalized, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                results = [];
            }

            if (token.IsCancellationRequested) return [];
        }

        results = PostProcessResults(results);

        lock (_cacheLock)
        {
            if (_queryCache.Count >= 50)
            {
                _queryCache.Clear();
            }
            _queryCache[normalized] = results;
        }

        return results;
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _queryCache.Clear();
        }
    }

    public static string? NormalizeQuery(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var formC = raw.Normalize(NormalizationForm.FormC);
            var sb = new StringBuilder(formC.Length);
            bool pendingSpace = false;
            foreach (var ch in formC)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(ch);
            }
            if (sb.Length > 100) sb.Length = 100;
            if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
            return sb.ToString();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static bool LongEnough(string location)
    {
        var n = new StringInfo(location).LengthInTextElements;
        if (n >= 3) return true;
        if (n < 2) return false;
        foreach (var r in location.EnumerateRunes())
        {
            if (Rune.IsLetter(r) && r.Value >= 0x0900) return true;
        }
        return false;
    }

    public static (string Location, string? Qualifier) ParseQuery(string normalized)
    {
        int commaIndex = normalized.IndexOf(',');
        if (commaIndex < 0)
        {
            return (normalized.Trim(), null);
        }

        string loc = normalized[..commaIndex].Trim();
        string remainder = normalized[(commaIndex + 1)..].Trim();
        int nextComma = remainder.IndexOf(',');
        string qual = (nextComma >= 0 ? remainder[..nextComma] : remainder).Trim();
        return (loc, string.IsNullOrEmpty(qual) ? null : qual);
    }

    private static async Task<(IReadOnlyList<GeoResult> Results, int RawCount)> GeocodeOpenMeteoAsync(
        string location, string? qualifier, CancellationToken token)
    {
        string nameParam;
        int count;

        if (string.IsNullOrEmpty(qualifier))
        {
            nameParam = location;
            count = 8;
        }
        else if (qualifier.Length <= 2)
        {
            nameParam = $"{location}, {qualifier}";
            count = 8;
        }
        else
        {
            nameParam = location;
            count = 50;
        }

        var url = "https://geocoding-api.open-meteo.com/v1/search"
                + $"?name={Uri.EscapeDataString(nameParam)}&count={count}&language=en&format=json";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await WeatherService.SharedHttpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return ([], 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var body = await JsonSerializer.DeserializeAsync<OpenMeteoGeocodingResponse>(
            stream, s_jsonOptions, timeout.Token).ConfigureAwait(false);

        var rawList = body?.Results ?? [];
        var validated = ValidateAndFilterGeoResults(rawList);
        int rawCount = validated.Count;

        if (qualifier != null && qualifier.Length >= 3)
        {
            var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

            var filtered = new List<GeoResult>(8);
            foreach (var r in validated)
            {
                bool matchAdmin = r.Admin1 != null && compareInfo.IsPrefix(r.Admin1, qualifier, options);
                bool matchCountry = r.Country != null && compareInfo.IsPrefix(r.Country, qualifier, options);
                if (matchAdmin || matchCountry)
                {
                    filtered.Add(r);
                    if (filtered.Count == 8) break;
                }
            }
            return (filtered, rawCount);
        }

        return (validated.Take(8).ToList(), rawCount);
    }

    private static async Task<IReadOnlyList<GeoResult>> GeocodePhotonAsync(string query, CancellationToken token)
    {
        var url = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(query)}&limit=5&osm_tag=place";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("MetroHub/1.0");

        using var response = await WeatherService.SharedHttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var body = await JsonSerializer.DeserializeAsync<PhotonResponse>(stream, s_jsonOptions, timeout.Token).ConfigureAwait(false);

        if (body?.Features == null) return [];

        var list = new List<GeoResult>(body.Features.Count);
        foreach (var feat in body.Features)
        {
            var coords = feat.Geometry?.Coordinates;
            var name = feat.Properties?.Name;
            if (coords == null || coords.Length < 2 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            double lon = coords[0];
            double lat = coords[1];

            if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0)
            {
                continue;
            }

            list.Add(new GeoResult(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                Admin1: feat.Properties?.State,
                Admin2: null,
                Country: feat.Properties?.Country,
                Timezone: null));
        }

        return list;
    }

    private static List<GeoResult> ValidateAndFilterGeoResults(IEnumerable<GeoResult> source)
    {
        var valid = new List<GeoResult>();
        foreach (var r in source)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || !r.Latitude.HasValue || !r.Longitude.HasValue)
            {
                continue;
            }

            double lat = r.Latitude.Value;
            double lon = r.Longitude.Value;
            if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0)
            {
                continue;
            }

            valid.Add(r);
        }
        return valid;
    }

    private static IReadOnlyList<GeoResult> PostProcessResults(IReadOnlyList<GeoResult> list)
    {
        if (list.Count == 0) return list;

        var deduped = new List<GeoResult>(list.Count);
        foreach (var item in list)
        {
            bool isDuplicate = false;
            foreach (var existing in deduped)
            {
                if (string.Equals(item.Name, existing.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Admin1, existing.Admin1, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Country, existing.Country, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(item.Latitude!.Value - existing.Latitude!.Value) < 0.01 &&
                    Math.Abs(item.Longitude!.Value - existing.Longitude!.Value) < 0.01)
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
            {
                deduped.Add(item);
            }
        }

        for (int i = 0; i < deduped.Count; i++)
        {
            var cur = deduped[i];
            bool hasCollision = false;
            for (int j = 0; j < deduped.Count; j++)
            {
                if (i != j)
                {
                    var other = deduped[j];
                    if (string.Equals(cur.Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(cur.Admin1, other.Admin1, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(cur.Country, other.Country, StringComparison.OrdinalIgnoreCase))
                    {
                        hasCollision = true;
                        break;
                    }
                }
            }

            var parts = new List<string>(3);
            if (hasCollision && !string.IsNullOrWhiteSpace(cur.Admin2))
            {
                parts.Add(cur.Admin2.Trim());
            }
            if (!string.IsNullOrWhiteSpace(cur.Admin1))
            {
                parts.Add(cur.Admin1.Trim());
            }
            if (!string.IsNullOrWhiteSpace(cur.Country) && !string.Equals(cur.Country.Trim(), cur.Admin1?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(cur.Country.Trim());
            }

            cur.DisplaySubtitle = string.Join(", ", parts);
        }

        return deduped;
    }
}
