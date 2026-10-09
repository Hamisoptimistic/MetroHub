using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Core.Services;

#region Companion DTO Models

public sealed record CompanionHealthResponse(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("activePort")] int ActivePort
);

public sealed record CompanionWorkspaceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isActive")] bool IsActive
);

public sealed record CompanionPinRequest(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("workspaceId")] string? WorkspaceId = null,
    [property: JsonPropertyName("spanX")] int? SpanX = null,
    [property: JsonPropertyName("spanY")] int? SpanY = null,
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl = null
);

public sealed record CompanionPinResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("tileId")] string? TileId,
    [property: JsonPropertyName("col")] int Col,
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("message")] string? Message = null
);

public sealed record CompanionPinResult(
    bool Success,
    bool Duplicate,
    string? TileId,
    int Col,
    int Row,
    string? Message = null
);

public sealed record CompanionErrorResponse(
    [property: JsonPropertyName("error")] string Error
);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompanionHealthResponse))]
[JsonSerializable(typeof(List<CompanionWorkspaceDto>))]
[JsonSerializable(typeof(CompanionPinRequest))]
[JsonSerializable(typeof(CompanionPinResponse))]
[JsonSerializable(typeof(CompanionErrorResponse))]
internal partial class CompanionJsonContext : JsonSerializerContext
{
}

#endregion

#region Anti-SSRF Image Fetcher

/// <summary>
/// Hardened asynchronous image fetcher with socket-level IP validation (ConnectCallback)
/// and manual redirect inspection to prevent DNS rebinding and SSRF attacks.
/// </summary>
public static class CompanionImageDownloader
{
    private static readonly SocketsHttpHandler SafeHandler = new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new HttpRequestException("DNS resolution yielded no IP addresses.");
            }

            foreach (var ip in addresses)
            {
                if (LocalCompanionService.IsBlockedIp(ip))
                {
                    throw new SocketException((int)SocketError.AccessDenied);
                }
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    private static readonly HttpClient SafeClient = new(SafeHandler)
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>
    /// Downloads an image from the specified URL after verifying anti-SSRF boundaries,
    /// manual redirect hops, 2 MB body cap, and image/* MIME type.
    /// Returns the absolute cached path on disk or null if invalid/rejected.
    /// </summary>
    public static async Task<string?> DownloadImageAsync(string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? currentUri) ||
            (currentUri.Scheme != Uri.UriSchemeHttp && currentUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            string cacheDir = AppPaths.PrimaryIconsDir;
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            string hash = IconExtractorService.ComputeDeterministicHash(imageUrl);
            string destinationPath = Path.Combine(cacheDir, $"ext_thumb_{hash}.png");

            if (File.Exists(destinationPath))
            {
                var fi = new FileInfo(destinationPath);
                if (fi.Length > 200)
                {
                    return destinationPath;
                }
            }

            HttpResponseMessage? response = null;
            for (int hop = 0; hop < 3; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MetroHub/2.0");

                response?.Dispose();
                response = await SafeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
                {
                    var location = response.Headers.Location;
                    if (location == null) return null;

                    currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                    if (currentUri.Scheme != Uri.UriSchemeHttp && currentUri.Scheme != Uri.UriSchemeHttps)
                    {
                        return null;
                    }
                    continue;
                }

                break;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                response?.Dispose();
                return null;
            }

            using (response)
            {
                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrEmpty(mediaType) && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024)
                {
                    return null;
                }

                string tmpFile = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var fileStream = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        byte[] buffer = new byte[81920];
                        long totalRead = 0;
                        int read;
                        while ((read = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            totalRead += read;
                            if (totalRead > 2 * 1024 * 1024)
                            {
                                return null;
                            }
                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                    }

                    var fi = new FileInfo(tmpFile);
                    if (!fi.Exists || fi.Length < 100)
                    {
                        return null;
                    }

                    File.Move(tmpFile, destinationPath, overwrite: true);
                    return destinationPath;
                }
                finally
                {
                    if (File.Exists(tmpFile))
                    {
                        try { File.Delete(tmpFile); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"[CompanionImageDownloader] Failed to fetch image from '{imageUrl}'");
            return null;
        }
    }
}

#endregion

#region LocalCompanionService

/// <summary>
/// Ultra-secure, low-overhead HTTP loopback service enabling browser extensions
/// (Chrome, Edge, Firefox, Zen) to send tabs and links directly to MetroHub canvas.
/// Enforces DNS-rebinding immunity, pinned extension origins, and anti-SSRF protections.
/// </summary>
public sealed class LocalCompanionService : IDisposable
{
    public const int BasePort = 48842;
    public const int MaxPort = 48846;

    /// <summary>
    /// Exact pinned Chrome Extension Origin derived from deterministic RSA public key.
    /// </summary>
    public const string PinnedChromeOrigin = "chrome-extension://eacfogjghpoogfdmpggaoohcmjmickfp";

    public const string ClientHeaderName = "X-MetroHub-Client";
    public const string ClientHeaderExpected = "BrowserExtension";
    public const int MaxRequestBodyBytes = 64 * 1024; // 64 KB
    public const int DispatcherTimeoutSeconds = 2;

    public static readonly FrozenSet<string> TrackingParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source",
        "utm_medium",
        "utm_campaign",
        "utm_term",
        "utm_content",
        "utm_id",
        "fbclid",
        "gclid",
        "msclkid",
        "mc_eid",
        "si",
        "igshid",
        "_hsenc",
        "_hsmi"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<LocalCompanionService> _instance = new(() => new LocalCompanionService());
    public static LocalCompanionService Instance => _instance.Value;

    private HttpListener? _listener;
    private int _activePort;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly object _lock = new();

    public bool IsRunning => _listener?.IsListening == true;
    public int ActivePort => _activePort;

    /// <summary>
    /// Pluggable delegate for testing or decoupling tile placement from WPF Dispatcher.
    /// </summary>
    public Func<CompanionPinRequest, CancellationToken, Task<CompanionPinResult>>? PinTileHandler { get; set; }

    /// <summary>
    /// Pluggable delegate for testing workspace discovery.
    /// </summary>
    public Func<IReadOnlyList<CompanionWorkspaceDto>>? WorkspacesHandler { get; set; }

    public LocalCompanionService()
    {
    }

    /// <summary>
    /// Starts the loopback HTTP companion listener.
    /// Probes sequential ports from BasePort (48842) to MaxPort (48846) until binding succeeds.
    /// </summary>
    public Task StartAsync(int? preferredPort = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            int startPort = preferredPort ?? BasePort;
            int endPort = preferredPort.HasValue ? Math.Max(MaxPort, preferredPort.Value + 10) : MaxPort;

            for (int port = startPort; port <= endPort; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                try
                {
                    listener.Start();
                    _listener = listener;
                    _activePort = port;
                    _cts = new CancellationTokenSource();
                    _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);

                    Serilog.Log.Information("[LocalCompanionService] Listening on http://127.0.0.1:{Port}/", port);
                    return Task.CompletedTask;
                }
                catch (HttpListenerException ex)
                {
                    try { listener.Close(); } catch { }
                    Serilog.Log.Warning(ex, "[LocalCompanionService] Port {Port} unavailable: {Message}", port, ex.Message);
                }
            }

            Serilog.Log.Warning("[LocalCompanionService] Failed to bind to any port in range {StartPort}-{EndPort}.", startPort, endPort);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stops the companion listener gracefully without blocking.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        HttpListener? listener;
        Task? listenTask;

        lock (_lock)
        {
            cts = _cts;
            _cts = null;
            listener = _listener;
            _listener = null;
            listenTask = _listenTask;
            _listenTask = null;
            _activePort = 0;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (listener != null)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch { }
        }

        if (listenTask != null)
        {
            try { await listenTask.ConfigureAwait(false); } catch { }
        }

        if (cts != null)
        {
            try { cts.Dispose(); } catch { }
        }

        Serilog.Log.Information("[LocalCompanionService] Stopped.");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListener? listener = _listener;
            if (listener == null || !listener.IsListening) break;

            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestSafeAsync(context, ct), CancellationToken.None);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Safe.Log(ex, "[LocalCompanionService] Error accepting connection");
                }
            }
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await ProcessRequestAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[LocalCompanionService] Unhandled exception processing request");
            try
            {
                context.Response.StatusCode = 500;
                await SendJsonAsync(context.Response, 500, new CompanionErrorResponse("Internal server error."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            }
            catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        // ── 1. Host Header Check (Anti-DNS Rebinding) ───────────────────────────
        string? host = request.Headers["Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            await SendJsonAsync(response, 403, new CompanionErrorResponse("Host header missing."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        bool isValidHost = host.Equals($"127.0.0.1:{_activePort}", StringComparison.OrdinalIgnoreCase) ||
                           host.Equals($"localhost:{_activePort}", StringComparison.OrdinalIgnoreCase);

        if (!isValidHost)
        {
            await SendJsonAsync(response, 403, new CompanionErrorResponse("Forbidden: Host header mismatch."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        // ── 2. Origin Header Allowlist Check ────────────────────────────────────
        string? origin = request.Headers["Origin"];
        bool hasAllowedOrigin = false;

        if (string.IsNullOrEmpty(origin))
        {
            // Allowed: Direct local requests (curl, diagnostic CLI)
            hasAllowedOrigin = true;
        }
        else if (origin.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(response, 403, new CompanionErrorResponse("Forbidden: Origin 'null' is not permitted."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }
        else if (origin.Equals(PinnedChromeOrigin, StringComparison.Ordinal))
        {
            hasAllowedOrigin = true;
        }
        else if (origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase))
        {
            hasAllowedOrigin = true;
        }
        else
        {
            await SendJsonAsync(response, 403, new CompanionErrorResponse($"Forbidden: Origin '{origin}' is not permitted."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        // ── 3. Attach CORS Response Headers for Allowed Origins ─────────────────
        if (!string.IsNullOrEmpty(origin) && hasAllowedOrigin)
        {
            ApplyCorsHeaders(response, origin);
        }

        // ── 4. Method & Header Gate ─────────────────────────────────────────────
        string httpMethod = request.HttpMethod.ToUpperInvariant();

        // OPTIONS requests (CORS preflight) are EXEMPT from custom header requirement
        if (httpMethod == "OPTIONS")
        {
            response.StatusCode = 204;
            response.ContentLength64 = 0;
            return;
        }

        // GET & POST require X-MetroHub-Client: BrowserExtension
        string? clientHeader = request.Headers[ClientHeaderName];
        if (string.IsNullOrWhiteSpace(clientHeader) || !clientHeader.Equals(ClientHeaderExpected, StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse($"Missing or invalid required header: '{ClientHeaderName}'."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        // ── 5. Route Handling ───────────────────────────────────────────────────
        string rawPath = request.Url?.AbsolutePath ?? "/";

        if (httpMethod == "GET" && rawPath.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            var health = new CompanionHealthResponse("MetroHub", "1.0", _activePort);
            await SendJsonAsync(response, 200, health, CompanionJsonContext.Default.CompanionHealthResponse, ct).ConfigureAwait(false);
            return;
        }

        if (httpMethod == "GET" && rawPath.Equals("/api/workspaces", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<CompanionWorkspaceDto> workspaces = WorkspacesHandler != null
                ? WorkspacesHandler()
                : GetDefaultWorkspaces();

            var list = workspaces is List<CompanionWorkspaceDto> l ? l : new List<CompanionWorkspaceDto>(workspaces);
            await SendJsonAsync(response, 200, list, CompanionJsonContext.Default.ListCompanionWorkspaceDto, ct).ConfigureAwait(false);
            return;
        }

        if (httpMethod == "POST" && rawPath.Equals("/api/tiles", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePostTilesAsync(request, response, ct).ConfigureAwait(false);
            return;
        }

        // Route not found
        await SendJsonAsync(response, 404, new CompanionErrorResponse($"Route not found: '{rawPath}'."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
    }

    private async Task HandlePostTilesAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        // Body reading up to 64 KB cap
        byte[] buffer = new byte[MaxRequestBodyBytes + 1];
        int totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await request.InputStream.ReadAsync(buffer.AsMemory(totalBytesRead, buffer.Length - totalBytesRead), ct).ConfigureAwait(false)) > 0)
        {
            totalBytesRead += bytesRead;
            if (totalBytesRead > MaxRequestBodyBytes)
            {
                await SendJsonAsync(response, 413, new CompanionErrorResponse("Payload too large: request body exceeds 64 KB."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
                return;
            }
        }

        if (totalBytesRead == 0)
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse("Empty request body."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        CompanionPinRequest? pinRequest;
        try
        {
            pinRequest = JsonSerializer.Deserialize(buffer.AsSpan(0, totalBytesRead), CompanionJsonContext.Default.CompanionPinRequest);
        }
        catch (JsonException)
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse("Invalid JSON payload."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        if (pinRequest == null || string.IsNullOrWhiteSpace(pinRequest.Url))
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse("Missing required 'url' field."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        // Validate URL scheme
        string rawUrl = pinRequest.Url.Trim();
        if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse("Invalid URL scheme: only http and https are permitted."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse("Malformed URL."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        // Validate ThumbnailUrl scheme if provided
        if (!string.IsNullOrWhiteSpace(pinRequest.ThumbnailUrl))
        {
            string rawThumb = pinRequest.ThumbnailUrl.Trim();
            if (!rawThumb.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !rawThumb.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await SendJsonAsync(response, 400, new CompanionErrorResponse("Invalid thumbnailUrl scheme: only http and https are permitted."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
                return;
            }
            if (!Uri.TryCreate(rawThumb, UriKind.Absolute, out Uri? parsedThumb) ||
                (parsedThumb.Scheme != Uri.UriSchemeHttp && parsedThumb.Scheme != Uri.UriSchemeHttps))
            {
                await SendJsonAsync(response, 400, new CompanionErrorResponse("Malformed thumbnailUrl."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
                return;
            }
        }

        // Clamping & dimensions validation (only 2x2 or 4x2 permitted)
        int spanX = pinRequest.SpanX ?? 2;
        int spanY = pinRequest.SpanY ?? 2;
        if (!((spanX == 2 && spanY == 2) || (spanX == 4 && spanY == 2)))
        {
            await SendJsonAsync(response, 400, new CompanionErrorResponse("Invalid span dimensions. Allowed tile dimensions are 2x2 or 4x2."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
            return;
        }

        // Truncate fields
        string title = pinRequest.Title?.Trim() ?? string.Empty;
        if (title.Length > 256)
        {
            title = title.Substring(0, 256);
        }

        string note = pinRequest.Note?.Trim() ?? string.Empty;
        if (note.Length > 1024)
        {
            note = note.Substring(0, 1024);
        }

        string sanitizedUrl = SanitizeUrl(rawUrl);

        var sanitizedRequest = new CompanionPinRequest(
            Url: sanitizedUrl,
            Title: title,
            Note: note,
            WorkspaceId: pinRequest.WorkspaceId,
            SpanX: spanX,
            SpanY: spanY,
            ThumbnailUrl: pinRequest.ThumbnailUrl
        );

        // Validate workspace if explicitly specified
        if (!string.IsNullOrWhiteSpace(sanitizedRequest.WorkspaceId))
        {
            var workspaces = WorkspacesHandler != null ? WorkspacesHandler() : GetDefaultWorkspaces();
            bool exists = false;
            foreach (var ws in workspaces)
            {
                if (ws.Id.Equals(sanitizedRequest.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                await SendJsonAsync(response, 404, new CompanionErrorResponse($"Workspace '{sanitizedRequest.WorkspaceId}' not found."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
                return;
            }
        }

        // Dispatcher invocation with 2.0s cancellation token
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(DispatcherTimeoutSeconds));

        try
        {
            CompanionPinResult result;
            if (PinTileHandler != null)
            {
                result = await PinTileHandler(sanitizedRequest, cts.Token).ConfigureAwait(false);
            }
            else
            {
                result = await PlaceTileDefaultAsync(sanitizedRequest, cts.Token).ConfigureAwait(false);
            }

            var pinResponse = new CompanionPinResponse(
                Success: result.Success,
                Duplicate: result.Duplicate,
                TileId: result.TileId,
                Col: result.Col,
                Row: result.Row,
                Message: result.Message
            );

            await SendJsonAsync(response, 200, pinResponse, CompanionJsonContext.Default.CompanionPinResponse, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response.Headers["Retry-After"] = "2";
            await SendJsonAsync(response, 503, new CompanionErrorResponse("Service Unavailable: Desktop UI thread busy. Please retry."), CompanionJsonContext.Default.CompanionErrorResponse, ct).ConfigureAwait(false);
        }
    }

    private static void ApplyCorsHeaders(HttpListenerResponse response, string origin)
    {
        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-MetroHub-Client";
        response.Headers["Access-Control-Max-Age"] = "86400";
        response.Headers["Vary"] = "Origin";
    }

    private static async Task SendJsonAsync<T>(
        HttpListenerResponse response,
        int statusCode,
        T payload,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken ct)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonTypeInfo);
        response.ContentLength64 = utf8Bytes.Length;
        await response.OutputStream.WriteAsync(utf8Bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalizes URL and removes advertising tracking parameters while preserving functional ones.
    /// </summary>
    public static string SanitizeUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri)) return rawUrl;

        if (string.IsNullOrEmpty(uri.Query)) return rawUrl;

        string query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query)) return rawUrl;

        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var retained = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            int eqIdx = part.IndexOf('=');
            string key = eqIdx >= 0 ? part.Substring(0, eqIdx) : part;
            if (!TrackingParameters.Contains(key))
            {
                retained.Add(part);
            }
        }

        var uriBuilder = new UriBuilder(uri)
        {
            Query = retained.Count > 0 ? string.Join("&", retained) : string.Empty
        };

        return uriBuilder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Validates IP address against loopback, private RFC1918, CGNAT, link-local,
    /// broadcast, and IPv6 unique-local ranges for Anti-SSRF defense.
    /// </summary>
    public static bool IsBlockedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        byte[] bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 0) return true; // 0.0.0.0/8
            if (bytes[0] == 10) return true; // 10.0.0.0/8
            if (bytes[0] == 100 && (bytes[1] >= 64 && bytes[1] <= 127)) return true; // 100.64.0.0/10 Carrier-Grade NAT
            if (bytes[0] == 127) return true; // 127.0.0.0/8 Loopback
            if (bytes[0] == 169 && bytes[1] == 254) return true; // 169.254.0.0/16 Link-Local
            if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true; // 172.16.0.0/12 Private
            if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16 Private
            if (bytes[0] >= 224) return true; // 224.0.0.0/4 Multicast / Reserved / Broadcast
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            // fc00::/7 Unique Local IPv6 (fc00:: - fdff::)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            return false;
        }

        return true;
    }

    private static IReadOnlyList<CompanionWorkspaceDto> GetDefaultWorkspaces()
    {
        // Safe fallback when running without UI or during initialization
        return new List<CompanionWorkspaceDto>
        {
            new("default", "Default Workspace", true)
        };
    }

    private static Task<CompanionPinResult> PlaceTileDefaultAsync(CompanionPinRequest request, CancellationToken ct)
    {
        // Default stub for Phase 1 before Phase 2 wires full TileManager
        string tileId = Guid.NewGuid().ToString("N");
        return Task.FromResult(new CompanionPinResult(
            Success: true,
            Duplicate: false,
            TileId: tileId,
            Col: 0,
            Row: 0
        ));
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}

#endregion
