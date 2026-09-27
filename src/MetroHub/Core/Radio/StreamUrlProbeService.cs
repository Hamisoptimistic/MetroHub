using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
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
    int PlaylistTrackCount = 0,
    int BitrateKbps = 128
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
                ConnectTimeout = TimeSpan.FromSeconds(4),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };

            _httpClient = new HttpClient(handler, disposeHandler: true);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MetroHub/1.0");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "1");
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
        bool schemeWasOmitted = false;

        // Prepend https:// if protocol is omitted
        if (!sanitizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !sanitizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            sanitizedUrl = "https://" + sanitizedUrl;
            schemeWasOmitted = true;
        }

        if (!Uri.TryCreate(sanitizedUrl, UriKind.Absolute, out var uri) || 
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new StreamProbeResult(false, null, null, null, "Invalid URL format. Must start with http:// or https://.");
        }

        var result = await ProbeUriInternalAsync(uri, ct).ConfigureAwait(false);

        // If HTTPS failed and protocol was omitted by the user, fallback to plain HTTP (common for Icecast servers on port 8000)
        if (!result.IsValid && schemeWasOmitted && uri.Scheme == Uri.UriSchemeHttps)
        {
            var httpUri = new UriBuilder(uri) { Scheme = "http", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
            var httpResult = await ProbeUriInternalAsync(httpUri, ct).ConfigureAwait(false);
            if (httpResult.IsValid)
            {
                return httpResult;
            }
        }

        return result;
    }

    private async Task<StreamProbeResult> ProbeUriInternalAsync(Uri uri, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(4.5));

        try
        {
            HttpResponseMessage? response = null;
            bool headSucceeded = false;

            // 1. First attempt: Send lightweight HEAD request to inspect headers without downloading audio body
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, uri);
                headReq.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                var headRes = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                if (headRes.IsSuccessStatusCode)
                {
                    response = headRes;
                    headSucceeded = true;
                }
                else
                {
                    headRes.Dispose();
                }
            }
            catch
            {
                // HEAD request failed or rejected by server/proxy (e.g. FastCast4u/Centova proxy returning RPC-ERR or 405).
                // Gracefully fall through to GET with byte range.
            }

            // 2. Fall back to GET with byte range (Range: bytes=0-4095)
            if (!headSucceeded || response == null)
            {
                var getReq = new HttpRequestMessage(HttpMethod.Get, uri);
                getReq.Headers.Range = new RangeHeaderValue(0, 4095);
                getReq.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                response = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new StreamProbeResult(false, null, null, null, $"Stream server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                string? contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();

                // Extract ICY metadata headers if provided by Icecast/Shoutcast
                string? icyName = GetHeader(response, "icy-name");
                string? icyBr = GetHeader(response, "icy-br");
                string? icyGenre = GetHeader(response, "icy-genre");

                int bitrate = 128;
                if (!string.IsNullOrWhiteSpace(icyBr) && int.TryParse(icyBr, out int parsedBr) && parsedBr > 0)
                {
                    bitrate = parsedBr;
                }

                // 3. Reject HTML webpages unless it is a Shoutcast root that can be converted with '/;'
                if (contentType != null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    if (uri.AbsolutePath == "/" || string.IsNullOrEmpty(uri.AbsolutePath))
                    {
                        try
                        {
                            var shoutcastUri = new Uri(uri, "/;");
                            using var scReq = new HttpRequestMessage(HttpMethod.Get, shoutcastUri);
                            scReq.Headers.Range = new RangeHeaderValue(0, 4095);
                            scReq.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                            using var scRes = await _httpClient.SendAsync(scReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                            if (scRes.IsSuccessStatusCode)
                            {
                                string? scType = scRes.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                                if (scType != null && (scType.StartsWith("audio/") || scType == "application/ogg" || scType == "application/octet-stream"))
                                {
                                    string scName = GetHeader(scRes, "icy-name") ?? InferStationNameFromUri(uri);
                                    string? scBr = GetHeader(scRes, "icy-br");
                                    int scBitrate = (int.TryParse(scBr, out int b) && b > 0) ? b : bitrate;
                                    return new StreamProbeResult(
                                        IsValid: true,
                                        ResolvedStreamUrl: shoutcastUri.ToString(),
                                        InferredName: scName,
                                        ContentType: scType,
                                        ErrorMessage: null,
                                        BitrateKbps: scBitrate);
                                }
                            }
                        }
                        catch { }
                    }

                    return new StreamProbeResult(false, null, null, contentType, 
                        "This URL points to a website page, not a direct audio stream. Please paste a direct stream link.");
                }

                // 4. Playlist detection (.pls, .m3u, .m3u8, .asx, .xspf, audio/x-scpls, application/x-mpegurl, etc.)
                bool isPlaylist = uri.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
                                  uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                                  uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                                  uri.AbsolutePath.EndsWith(".asx", StringComparison.OrdinalIgnoreCase) ||
                                  uri.AbsolutePath.EndsWith(".xspf", StringComparison.OrdinalIgnoreCase) ||
                                  (contentType != null && (
                                      contentType.Contains("x-scpls") ||
                                      contentType.Contains("mpegurl") ||
                                      contentType.Contains("vnd.apple.mpegurl") ||
                                      contentType.Contains("x-ms-asf") ||
                                      contentType.Contains("xspf")));

                if (isPlaylist)
                {
                    return await ParsePlaylistAsync(uri, linkedCts.Token).ConfigureAwait(false);
                }

                // 5. Direct audio stream validation
                bool isAudio = contentType != null && (
                    contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                    contentType == "application/ogg" ||
                    contentType == "application/octet-stream"); // Common for raw Shoutcast mount points

                string inferredName;
                if (!string.IsNullOrWhiteSpace(icyName) &&
                    !string.Equals(icyName, "stream", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(icyName, "live", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(icyName, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    inferredName = icyName.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(icyGenre) &&
                         !string.Equals(icyGenre, "various", StringComparison.OrdinalIgnoreCase))
                {
                    inferredName = CapitalizeWords(icyGenre.Trim());
                }
                else
                {
                    inferredName = InferStationNameFromUri(uri);
                }

                if (isAudio || uri.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    return new StreamProbeResult(
                        IsValid: true,
                        ResolvedStreamUrl: uri.ToString(),
                        InferredName: inferredName,
                        ContentType: contentType ?? "audio/mpeg",
                        ErrorMessage: null,
                        BitrateKbps: bitrate);
                }

                // Accept with warning if server sent unconventional Content-Type but connection succeeded
                return new StreamProbeResult(
                    IsValid: true,
                    ResolvedStreamUrl: uri.ToString(),
                    InferredName: inferredName,
                    ContentType: contentType,
                    ErrorMessage: null,
                    BitrateKbps: bitrate);
            }
        }
        catch (OperationCanceledException)
        {
            return new StreamProbeResult(false, null, null, null, "Stream connection timed out after 4.5 seconds.");
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

            bool isAsx = playlistUri.AbsolutePath.EndsWith(".asx", StringComparison.OrdinalIgnoreCase) ||
                         content.Contains("<asx", StringComparison.OrdinalIgnoreCase);

            if (isAsx)
            {
                return ParseAsxContent(content, playlistUri);
            }

            bool isXspf = playlistUri.AbsolutePath.EndsWith(".xspf", StringComparison.OrdinalIgnoreCase) ||
                          content.Contains("<playlist", StringComparison.OrdinalIgnoreCase);

            if (isXspf)
            {
                return ParseXspfContent(content, playlistUri);
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

    private static StreamProbeResult ParseAsxContent(string content, Uri origin)
    {
        var hrefMatches = Regex.Matches(content, @"<ref\s+[^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (hrefMatches.Count == 0)
        {
            return new StreamProbeResult(false, null, null, null, "The .asx playlist contains no stream entries.");
        }

        if (hrefMatches.Count > 5)
        {
            return new StreamProbeResult(false, null, null, null,
                $"This playlist contains {hrefMatches.Count} channels. MetroHub adds single stations; please paste an individual stream link.",
                IsPlaylist: true,
                PlaylistTrackCount: hrefMatches.Count);
        }

        string firstHref = hrefMatches[0].Groups[1].Value.Trim();
        if (!Uri.TryCreate(origin, firstHref, out var resolvedUri))
        {
            return new StreamProbeResult(false, null, null, null, "The .asx playlist stream entry is an invalid URL.");
        }

        string? title = null;
        var titleMatch = Regex.Match(content, @"<title>([^<]+)</title>", RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            title = titleMatch.Groups[1].Value.Trim();
        }

        return new StreamProbeResult(
            IsValid: true,
            ResolvedStreamUrl: resolvedUri.ToString(),
            InferredName: !string.IsNullOrWhiteSpace(title) ? title : InferStationNameFromUri(origin),
            ContentType: "video/x-ms-asf",
            ErrorMessage: null,
            IsPlaylist: true,
            PlaylistTrackCount: hrefMatches.Count);
    }

    private static StreamProbeResult ParseXspfContent(string content, Uri origin)
    {
        var locMatches = Regex.Matches(content, @"<location>([^<]+)</location>", RegexOptions.IgnoreCase);
        if (locMatches.Count == 0)
        {
            return new StreamProbeResult(false, null, null, null, "The .xspf playlist contains no stream entries.");
        }

        if (locMatches.Count > 5)
        {
            return new StreamProbeResult(false, null, null, null,
                $"This playlist contains {locMatches.Count} channels. MetroHub adds single stations; please paste an individual stream link.",
                IsPlaylist: true,
                PlaylistTrackCount: locMatches.Count);
        }

        string firstLoc = locMatches[0].Groups[1].Value.Trim();
        if (!Uri.TryCreate(origin, firstLoc, out var resolvedUri))
        {
            return new StreamProbeResult(false, null, null, null, "The .xspf playlist stream entry is an invalid URL.");
        }

        string? title = null;
        var titleMatch = Regex.Match(content, @"<title>([^<]+)</title>", RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            title = titleMatch.Groups[1].Value.Trim();
        }

        return new StreamProbeResult(
            IsValid: true,
            ResolvedStreamUrl: resolvedUri.ToString(),
            InferredName: !string.IsNullOrWhiteSpace(title) ? title : InferStationNameFromUri(origin),
            ContentType: "application/xspf+xml",
            ErrorMessage: null,
            IsPlaylist: true,
            PlaylistTrackCount: locMatches.Count);
    }

    private static StreamProbeResult ParseM3uContent(string content, Uri origin)
    {
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // 1. Detect HLS (HTTP Live Streaming) playlists (master or media playlists)
        bool isHls = content.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase) ||
                     origin.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

        if (isHls)
        {
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

            int bitrate = 128;
            var streamInfLine = lines.FirstOrDefault(l => l.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase));
            if (streamInfLine != null)
            {
                var match = Regex.Match(streamInfLine, @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int bps) && bps > 0)
                {
                    bitrate = Math.Max(32, bps / 1000);
                }
            }

            return new StreamProbeResult(
                IsValid: true,
                ResolvedStreamUrl: origin.ToString(),
                InferredName: !string.IsNullOrWhiteSpace(title) ? title : InferStationNameFromUri(origin),
                ContentType: "application/vnd.apple.mpegurl",
                ErrorMessage: null,
                IsPlaylist: true,
                PlaylistTrackCount: 1,
                BitrateKbps: bitrate);
        }

        // 2. Standard M3U playlist handling (supports absolute and relative URLs)
        var rawEntries = lines
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
            .ToList();

        var urlEntries = new List<string>();
        foreach (var entry in rawEntries)
        {
            if (Uri.TryCreate(origin, entry, out var resolvedUri) &&
                (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps))
            {
                urlEntries.Add(resolvedUri.ToString());
            }
        }

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
        string? m3uTitle = null;
        var extinf = lines.FirstOrDefault(l => l.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase));
        if (extinf != null)
        {
            int commaIndex = extinf.IndexOf(',');
            if (commaIndex >= 0 && commaIndex < extinf.Length - 1)
            {
                m3uTitle = extinf[(commaIndex + 1)..].Trim();
            }
        }

        return new StreamProbeResult(
            IsValid: true,
            ResolvedStreamUrl: firstUrl,
            InferredName: !string.IsNullOrWhiteSpace(m3uTitle) ? m3uTitle : InferStationNameFromUri(origin),
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

        // Filter generic path segments to find station identifier
        string[] genericSegments = { "listen", "stream", "live", "playlist", "chunklist", "index", "radio", "audio", "proxy" };
        var meaningfulSegments = uri.Segments
            .Select(s => Path.GetFileNameWithoutExtension(s.Trim('/')))
            .Where(s => !string.IsNullOrWhiteSpace(s) && !genericSegments.Contains(s.ToLowerInvariant()))
            .ToList();

        string? candidate = meaningfulSegments.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            string cleanSegment = Regex.Replace(candidate, @"[_-]", " ");
            cleanSegment = Regex.Replace(cleanSegment, @"\d+k(bps)?|\d+mp3", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(cleanSegment) && !genericSegments.Contains(cleanSegment.ToLowerInvariant()))
            {
                return CapitalizeWords(cleanSegment);
            }
        }

        return CapitalizeWords(host.Split('.')[0]);
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }
        if (response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            return contentValues.FirstOrDefault();
        }
        return null;
    }

    private static string CapitalizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => p.Length > 1 ? char.ToUpperInvariant(p[0]) + p[1..] : p.ToUpperInvariant()));
    }
}
