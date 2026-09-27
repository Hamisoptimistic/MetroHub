using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Radio;

/// <summary>
/// Result returned after probing and validating an audio stream URL.
/// </summary>
public sealed record StreamProbeResult(
    bool IsValid,
    string? ResolvedStreamUrl,
    string? InferredName,
    string? ContentType,
    string? ErrorMessage,
    bool IsPlaylist = false,
    int PlaylistTrackCount = 0
);

/// <summary>
/// High-speed URL validation and stream probe service.
/// Detects direct Icecast/Shoutcast audio streams, unwraps single-station .pls/.m3u playlists,
/// rejects HTML webpages, and guards against massive multi-channel IPTV lists.
/// </summary>
public sealed class StreamUrlProbeService
{
    private static readonly Lazy<StreamUrlProbeService> _lazyInstance = new(() => new StreamUrlProbeService());
    public static StreamUrlProbeService Instance => _lazyInstance.Value;

    private readonly HttpClient _httpClient;

    public StreamUrlProbeService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(3)
            };

            _httpClient = new HttpClient(handler, disposeHandler: true);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MetroHub/1.0");
        }
    }

    /// <summary>
    /// Sanitizes and probes a stream URL.
    /// </summary>
    public async Task<StreamProbeResult> ProbeUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return new StreamProbeResult(false, null, null, null, "Please enter a stream URL.");
        }

        string sanitizedUrl = rawUrl.Trim().Trim('"', '\'');

        // Prepend https:// if protocol is omitted
        if (!sanitizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !sanitizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            sanitizedUrl = "https://" + sanitizedUrl;
        }

        if (!Uri.TryCreate(sanitizedUrl, UriKind.Absolute, out var uri) || 
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new StreamProbeResult(false, null, null, null, "Invalid URL format. Must start with http:// or https://.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(3.5));

        try
        {
            // 1. Send lightweight HEAD request to inspect headers without downloading audio body
            using var headReq = new HttpRequestMessage(HttpMethod.Head, uri);
            using var headRes = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);

            string? contentType = headRes.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            
            // If HEAD was rejected (e.g. 405 Method Not Allowed or 403), fall back to GET with byte range
            if (!headRes.IsSuccessStatusCode || string.IsNullOrWhiteSpace(contentType))
            {
                using var getReq = new HttpRequestMessage(HttpMethod.Get, uri);
                getReq.Headers.Range = new RangeHeaderValue(0, 4095); // Request only first 4KB
                using var getRes = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);

                if (!getRes.IsSuccessStatusCode)
                {
                    return new StreamProbeResult(false, null, null, null, $"Stream server returned HTTP {(int)getRes.StatusCode} ({getRes.ReasonPhrase}).");
                }

                contentType = getRes.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            }

            // 2. Reject HTML webpages (e.g. pasting youtube.com or radio landing pages)
            if (contentType != null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return new StreamProbeResult(false, null, null, contentType, 
                    "This URL points to a website page, not a direct audio stream. Please paste a direct stream link.");
            }

            // 3. Playlist detection (.pls, .m3u, .m3u8, audio/x-scpls, application/x-mpegurl)
            bool isPlaylist = uri.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
                              uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                              uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                              (contentType != null && (contentType.Contains("x-scpls") || contentType.Contains("mpegurl")));

            if (isPlaylist)
            {
                return await ParsePlaylistAsync(uri, linkedCts.Token).ConfigureAwait(false);
            }

            // 4. Direct audio stream validation
            bool isAudio = contentType != null && (
                contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                contentType == "application/ogg" ||
                contentType == "application/octet-stream"); // Common for raw Shoutcast mount points

            string inferredName = InferStationNameFromUri(uri);

            if (isAudio || uri.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                return new StreamProbeResult(
                    IsValid: true,
                    ResolvedStreamUrl: uri.ToString(),
                    InferredName: inferredName,
                    ContentType: contentType ?? "audio/mpeg",
                    ErrorMessage: null);
            }

            // Accept with warning if server sent unconventional Content-Type but connection succeeded
            return new StreamProbeResult(
                IsValid: true,
                ResolvedStreamUrl: uri.ToString(),
                InferredName: inferredName,
                ContentType: contentType,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new StreamProbeResult(false, null, null, null, "Stream connection timed out after 3.5 seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new StreamProbeResult(false, null, null, null, $"Network error connecting to stream: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new StreamProbeResult(false, null, null, null, $"Stream check failed: {ex.Message}");
        }
    }

    private async Task<StreamProbeResult> ParsePlaylistAsync(Uri playlistUri, CancellationToken ct)
    {
        try
        {
            // Limit playlist download to 64KB to prevent memory exhaustion from massive files
            using var req = new HttpRequestMessage(HttpMethod.Get, playlistUri);
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return new StreamProbeResult(false, null, null, null, $"Failed to download playlist (HTTP {(int)res.StatusCode}).");
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            char[] buffer = new char[65536];
            int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            string content = new string(buffer, 0, read);

            bool isPls = playlistUri.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
                         content.Contains("[playlist]", StringComparison.OrdinalIgnoreCase);

            if (isPls)
            {
                return ParsePlsContent(content, playlistUri);
            }

            return ParseM3uContent(content, playlistUri);
        }
        catch (Exception ex)
        {
            return new StreamProbeResult(false, null, null, null, $"Failed to parse playlist: {ex.Message}");
        }
    }

    private static StreamProbeResult ParsePlsContent(string content, Uri origin)
    {
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var fileEntries = lines.Where(l => Regex.IsMatch(l.Trim(), @"^File\d+=", RegexOptions.IgnoreCase)).ToList();

        if (fileEntries.Count == 0)
        {
            return new StreamProbeResult(false, null, null, null, "The .pls playlist contains no stream entries.");
        }

        if (fileEntries.Count > 5)
        {
            return new StreamProbeResult(false, null, null, null, 
                $"This playlist contains {fileEntries.Count} channels. MetroHub adds single stations; please paste an individual stream link.",
                IsPlaylist: true,
                PlaylistTrackCount: fileEntries.Count);
        }

        // Extract first stream URL
        string firstEntry = fileEntries[0];
        int eqIndex = firstEntry.IndexOf('=');
        string streamUrl = firstEntry[(eqIndex + 1)..].Trim();

        // Extract Title1 if present
        string? title = null;
        var titleEntry = lines.FirstOrDefault(l => Regex.IsMatch(l.Trim(), @"^Title1=", RegexOptions.IgnoreCase));
        if (titleEntry != null)
        {
            int tIndex = titleEntry.IndexOf('=');
            title = titleEntry[(tIndex + 1)..].Trim();
        }

        return new StreamProbeResult(
            IsValid: true,
            ResolvedStreamUrl: streamUrl,
            InferredName: !string.IsNullOrWhiteSpace(title) ? title : InferStationNameFromUri(origin),
            ContentType: "audio/pls",
            ErrorMessage: null,
            IsPlaylist: true,
            PlaylistTrackCount: fileEntries.Count);
    }

    private static StreamProbeResult ParseM3uContent(string content, Uri origin)
    {
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var urlEntries = lines
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && (l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (urlEntries.Count == 0)
        {
            return new StreamProbeResult(false, null, null, null, "The .m3u playlist contains no valid stream URLs.");
        }

        if (urlEntries.Count > 5)
        {
            return new StreamProbeResult(false, null, null, null, 
                $"This playlist contains {urlEntries.Count} channels. MetroHub adds single stations; please paste an individual stream link.",
                IsPlaylist: true,
                PlaylistTrackCount: urlEntries.Count);
        }

        string firstUrl = urlEntries[0];

        // Search for #EXTINF title above first URL
        string? title = null;
        var extinfLine = lines.FirstOrDefault(l => l.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase));
        if (extinfLine != null)
        {
            int commaIndex = extinfLine.IndexOf(',');
            if (commaIndex >= 0 && commaIndex < extinfLine.Length - 1)
            {
                title = extinfLine[(commaIndex + 1)..].Trim();
            }
        }

        return new StreamProbeResult(
            IsValid: true,
            ResolvedStreamUrl: firstUrl,
            InferredName: !string.IsNullOrWhiteSpace(title) ? title : InferStationNameFromUri(origin),
            ContentType: "audio/x-mpegurl",
            ErrorMessage: null,
            IsPlaylist: true,
            PlaylistTrackCount: urlEntries.Count);
    }

    private static string InferStationNameFromUri(Uri uri)
    {
        string host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        // If path has a friendly segment like /chillsynth.mp3 or /groovesalad-128-mp3
        string lastSegment = uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastSegment) && lastSegment != "listen" && lastSegment != "stream" && lastSegment != "live")
        {
            string cleanSegment = Path.GetFileNameWithoutExtension(lastSegment);
            cleanSegment = Regex.Replace(cleanSegment, @"[_-]", " ");
            cleanSegment = Regex.Replace(cleanSegment, @"\d+k(bps)?|\d+mp3", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(cleanSegment))
            {
                return CapitalizeWords(cleanSegment);
            }
        }

        return CapitalizeWords(host.Split('.')[0]);
    }

    private static string CapitalizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => p.Length > 1 ? char.ToUpperInvariant(p[0]) + p[1..] : p.ToUpperInvariant()));
    }
}
