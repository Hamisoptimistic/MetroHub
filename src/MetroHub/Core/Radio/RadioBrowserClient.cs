using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;

namespace MetroHub.Core.Radio;

/// <summary>
/// Data transfer object representing a radio station returned by the Radio-Browser Community API.
/// </summary>
public sealed record RadioBrowserStationDto
{
    [JsonPropertyName("stationuuid")]
    public string StationUuid { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url_resolved")]
    public string UrlResolved { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; init; }

    [JsonPropertyName("codec")]
    public string Codec { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public string Tags { get; init; } = string.Empty;

    [JsonPropertyName("countrycode")]
    public string CountryCode { get; init; } = string.Empty;

    [JsonPropertyName("lastcheckok")]
    public int LastCheckOk { get; init; }

    /// <summary>Best direct stream URL available (resolved edge endpoint or raw URL).</summary>
    [JsonIgnore]
    public string BestStreamUrl => !string.IsNullOrWhiteSpace(UrlResolved) ? UrlResolved : Url;
}

/// <summary>
/// Zero-dependency, resilient client for the free community Radio-Browser API (radio-browser.info).
/// Features 3-second timeouts, round-robin DNS mirror failover (de1, nl1, at1),
/// and desktop User-Agent headers to prevent CDN/Cloudflare rate-limits.
/// </summary>
public sealed class RadioBrowserClient
{
    private static readonly Lazy<RadioBrowserClient> _lazyInstance = new(() => new RadioBrowserClient());
    public static RadioBrowserClient Instance => _lazyInstance.Value;

    private static readonly string[] DefaultMirrors = new[]
    {
        "https://de1.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
        "https://at1.api.radio-browser.info"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<string> _mirrors;
    private int _primaryMirrorIndex;

    public RadioBrowserClient(HttpClient? httpClient = null, IReadOnlyList<string>? mirrors = null)
    {
        _mirrors = mirrors is { Count: > 0 } ? mirrors : DefaultMirrors;
        _httpClient = httpClient ?? HttpHelper.Client;
    }

    /// <summary>
    /// Searches for online radio stations by name or tag with automatic mirror failover and debounced cancellation.
    /// </summary>
    /// <param name="query">Search term (e.g. "Lofi", "SomaFM", "Chill").</param>
    /// <param name="limit">Maximum number of results (default: 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching stations.</returns>
    public async Task<IReadOnlyList<RadioBrowserStationDto>> SearchStationsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<RadioBrowserStationDto>();
        }

        string encodedQuery = Uri.EscapeDataString(query.Trim());
        string endpointPath = $"/json/stations/byname/{encodedQuery}?limit={Math.Clamp(limit, 1, 100)}&order=clickcount&reverse=true";

        var results = await ExecuteWithMirrorFailoverAsync(async (baseUrl, linkedCt) =>
        {
            string url = baseUrl + endpointPath;
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCt).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCt).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<List<RadioBrowserStationDto>>(stream, JsonOptions, linkedCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return results ?? (IReadOnlyList<RadioBrowserStationDto>)Array.Empty<RadioBrowserStationDto>();
    }

    /// <summary>
    /// Resolves an active stream URL for self-healing dead stations by UUID or station name.
    /// </summary>
    public async Task<string?> ResolveWorkingUrlAsync(string? stationUuid, string? stationName, CancellationToken ct = default)
    {
        // 1. First attempt resolution by unique station UUID
        if (!string.IsNullOrWhiteSpace(stationUuid))
        {
            string endpointPath = $"/json/stations/byuuid/{Uri.EscapeDataString(stationUuid.Trim())}";
            var byUuid = await ExecuteWithMirrorFailoverAsync(async (baseUrl, linkedCt) =>
            {
                string url = baseUrl + endpointPath;
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCt).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                await using var stream = await response.Content.ReadAsStreamAsync(linkedCt).ConfigureAwait(false);
                var list = await JsonSerializer.DeserializeAsync<List<RadioBrowserStationDto>>(stream, JsonOptions, linkedCt).ConfigureAwait(false);
                return list?.FirstOrDefault();
            }, ct).ConfigureAwait(false);

            if (byUuid != null && byUuid.LastCheckOk == 1 && !string.IsNullOrWhiteSpace(byUuid.BestStreamUrl))
            {
                return byUuid.BestStreamUrl;
            }
        }

        // 2. Fallback resolution by exact or closest station name
        if (!string.IsNullOrWhiteSpace(stationName))
        {
            var searchResults = await SearchStationsAsync(stationName, limit: 3, ct).ConfigureAwait(false);
            var match = searchResults.FirstOrDefault(s => s.LastCheckOk == 1 && !string.IsNullOrWhiteSpace(s.BestStreamUrl));
            if (match != null)
            {
                return match.BestStreamUrl;
            }
        }

        return null;
    }

    private async Task<T?> ExecuteWithMirrorFailoverAsync<T>(Func<string, CancellationToken, Task<T?>> action, CancellationToken ct) where T : class
    {
        int startIndex = _primaryMirrorIndex;
        int mirrorCount = _mirrors.Count;

        for (int i = 0; i < mirrorCount; i++)
        {
            if (ct.IsCancellationRequested) return null;

            int currentMirrorIndex = (startIndex + i) % mirrorCount;
            string currentMirror = _mirrors[currentMirrorIndex];

            using var perMirrorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perMirrorCts.CancelAfter(TimeSpan.FromSeconds(3.5)); // Strict 3.5s per mirror limit

            try
            {
                var result = await action(currentMirror, perMirrorCts.Token).ConfigureAwait(false);
                if (result != null)
                {
                    _primaryMirrorIndex = currentMirrorIndex; // Remember working mirror
                    return result;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Debug.WriteLine($"[RadioBrowserClient] Mirror {currentMirror} timed out, trying next...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioBrowserClient] Mirror {currentMirror} failed ({ex.Message}), trying next...");
            }
        }

        return null;
    }
}
