using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>
/// Request routing for the phone bridge. Every route lives under <c>/m/</c>.
/// </summary>
/// <remarks>
/// Security posture, in order of evaluation:
/// <list type="number">
/// <item>Only same-LAN peers are ever accepted (enforced in the transport).</item>
/// <item>The shell assets are static and contain no secrets.</item>
/// <item>Everything else requires the per-session token from header, query, or cookie.</item>
/// <item>Mutating routes additionally require <c>Content-Type: application/json</c> (pin) or an
/// image content type (upload). Browsers cannot set either on a cross-site form post, so
/// form-based CSRF is structurally impossible.</item>
/// <item>No CORS headers are emitted at all: cross-origin scripts cannot read any response.
/// The phone app is same-origin, so it never needs them.</item>
/// </list>
/// </remarks>
public sealed class MobileBridgeEndpoints
{
    public const string TokenHeaderName = "X-MetroHub-Token";
    public const string TokenCookieName = "mh_token";
    public const int MaxPinBodyBytes = 64 * 1024;
    public const long MaxUploadBytes = 32L * 1024 * 1024;

    private const int TokenCookieMaxAgeSeconds = 60 * 60 * 24 * 30;
    private const int UploadTimeoutSeconds = 120;
    private const int MaxConcurrentUploads = 2;
    private const int ShellRequestLimitPerMinute = 120;
    private const int MutationLimitPerMinute = 45;

    private static readonly byte[] PingFrame = Encoding.UTF8.GetBytes(": ping\n\n");
    private static readonly byte[] RetryFrame = Encoding.UTF8.GetBytes("retry: 3000\n\n");

    /// <summary>
    /// Cadence of the comment frames that keep the event stream alive. A property rather than a
    /// constant so tests can shrink the interval instead of waiting a whole beat to observe the
    /// teardown that follows a phone hanging up.
    /// </summary>
    internal static TimeSpan SseHeartbeat { get; set; } = TimeSpan.FromSeconds(20);

    private static readonly List<MobileBridgeSizeDto> AllowedSizes = new()
    {
        new MobileBridgeSizeDto(2, 2, "2x2"),
        new MobileBridgeSizeDto(4, 2, "4x2")
    };

    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
        ["image/bmp"] = ".bmp"
    };

    private readonly MobileBridgeService _bridge;
    private readonly MobileBridgeRateLimiter _shellLimit = new(ShellRequestLimitPerMinute, TimeSpan.FromMinutes(1));
    private readonly MobileBridgeRateLimiter _mutationLimit = new(MutationLimitPerMinute, TimeSpan.FromMinutes(1));
    private int _activeUploads;

    public MobileBridgeEndpoints(MobileBridgeService bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task HandleAsync(MobileHttpRequest request, MobileHttpResponse response, CancellationToken ct)
    {
        string path = request.RawPath;

        // ── CORS & Private Network Access (W3C PNA) ──────────────────────────────
        response.SetHeader("Access-Control-Allow-Origin", "*");
        response.SetHeader("Access-Control-Allow-Private-Network", "true");

        if (request.Method == "OPTIONS")
        {
            response.SetStatus(204);
            response.SetHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            response.SetHeader("Access-Control-Allow-Headers", "Content-Type, X-MetroHub-Token");
            response.SetHeader("Access-Control-Max-Age", "86400");
            await response.WriteAsync(Array.Empty<byte>(), "text/plain", ct).ConfigureAwait(false);
            return;
        }

        // ── Static shell (no secrets, no auth) ──────────────────────────────────
        if (request.Method == "GET" && MobileBridgeAssets.TryGet(path, out byte[] assetBytes, out string assetType))
        {
            await response.WriteAsync(assetBytes, assetType, ct).ConfigureAwait(false);
            return;
        }

        // ── Convenience: bare host redirects to the shell ───────────────────────
        if (request.Method == "GET" && (path.Length == 0 || path == "/"))
        {
            response.SetStatus(302);
            response.SetHeader("Location", "/m/");
            await response.WriteTextAsync(string.Empty, "text/plain; charset=utf-8", ct).ConfigureAwait(false);
            return;
        }

        // ── Unauthenticated liveness probe ──────────────────────────────────────
        if (request.Method == "GET" && path.Equals("/m/api/health", StringComparison.OrdinalIgnoreCase))
        {
            var health = new MobileBridgeHealthResponse("MetroHub", "2.0", Bridge: true, RequiresToken: true);
            await WriteJsonAsync(response, 200, health, MobileBridgeJsonContext.Default.MobileBridgeHealthResponse, ct)
                .ConfigureAwait(false);
            return;
        }

        bool isApiRoute = path.StartsWith("/m/api/", StringComparison.OrdinalIgnoreCase);
        bool isShellRoute = path.Equals("/m", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals("/m/", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals("/m/share", StringComparison.OrdinalIgnoreCase);

        if (!isApiRoute && !isShellRoute)
        {
            await FailAsync(response, 404, $"Route not found: '{path}'.", ct).ConfigureAwait(false);
            return;
        }

        // ── Token gate ──────────────────────────────────────────────────────────
        string? queryToken = request.QueryValue("t");
        string? headerToken = request.Header(TokenHeaderName);
        string? cookieToken = ReadCookie(request, TokenCookieName);

        MobileBridgeAuthorization auth = _bridge.Authorize(headerToken);
        if (!auth.IsAuthorized)
        {
            auth = _bridge.Authorize(queryToken);
        }

        if (!auth.IsAuthorized)
        {
            auth = _bridge.Authorize(cookieToken);
        }

        if (!auth.IsAuthorized)
        {
            if (isShellRoute)
            {
                response.SetStatus(401);
                await response.WriteTextAsync(PairingExpiredHtml, "text/html; charset=utf-8", ct).ConfigureAwait(false);
            }
            else
            {
                await FailAsync(response, 401, "Pairing token missing or no longer valid. Scan a fresh QR code from MetroHub.", ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        // ── Authenticated shell ─────────────────────────────────────────────────
        if (isShellRoute)
        {
            if (!_shellLimit.TryAcquire(request.RemoteAddress.ToString()))
            {
                await FailAsync(response, 429, "Too many requests. Slow down and retry.", ct).ConfigureAwait(false);
                return;
            }

            // First navigation from the QR code or pasted link. The phone is handed a durable device
            // token here, so this is the last time it ever has to scan: the session token keeps
            // rotating, the device token does not.
            if (auth.IsSession)
            {
                (PairedPhone pairedDevice, string deviceToken) = _bridge.PairDevice(DescribePhone(request));
                auth = new MobileBridgeAuthorization(MobileBridgeCredential.Device, pairedDevice);
                response.SetHeader("Set-Cookie", BuildTokenCookie(deviceToken, TokenCookieMaxAgeSeconds));
            }
            else if (_bridge.IsAuthorized(queryToken))
            {
                // Already-paired phone that reopened a link with a stale session token: keep it working.
                response.SetHeader("Set-Cookie", BuildTokenCookie(_bridge.TokenValue, TokenCookieMaxAgeSeconds));
            }

            if (!MobileBridgeAssets.TryGet("/m/index.html", out byte[] shellBytes, out string shellType))
            {
                await FailAsync(response, 500, "The phone app assets are unavailable.", ct).ConfigureAwait(false);
                return;
            }

            await response.WriteAsync(shellBytes, shellType, ct).ConfigureAwait(false);
            return;
        }

        // ── Authenticated API ───────────────────────────────────────────────────
        if (request.Method == "GET" && path.Equals("/m/api/context", StringComparison.OrdinalIgnoreCase))
        {
            await HandleContextAsync(request, response, auth, ct).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path.Equals("/m/api/unpair", StringComparison.OrdinalIgnoreCase))
        {
            bool removed = _bridge.UnpairDevice(cookieToken) ||
                           _bridge.UnpairDevice(headerToken);

            response.SetHeader("Set-Cookie", BuildTokenCookie(string.Empty, 0));

            var payload = new MobileBridgeActionResponse(
                Success: true,
                Message: removed
                    ? "This phone is no longer paired."
                    : "This phone was not in the paired list.");

            await WriteJsonAsync(response, 200, payload, MobileBridgeJsonContext.Default.MobileBridgeActionResponse, ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path.Equals("/m/api/pin", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePinAsync(request, response, ct).ConfigureAwait(false);
            return;
        }

        if ((request.Method == "PUT" || request.Method == "POST") &&
            path.Equals("/m/api/upload", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUploadAsync(request, response, ct).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path.Equals("/m/api/events", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEventsAsync(request, response, ct).ConfigureAwait(false);
            return;
        }

        await FailAsync(response, 404, $"Route not found: '{path}'.", ct).ConfigureAwait(false);
    }

    // ── Context ────────────────────────────────────────────────────────────────

    private async Task HandleContextAsync(
        MobileHttpRequest request,
        MobileHttpResponse response,
        MobileBridgeAuthorization auth,
        CancellationToken ct)
    {
        string? newDeviceToken = null;
        if (auth.IsSession)
        {
            (PairedPhone pairedDevice, string deviceToken) = _bridge.PairDevice(DescribePhone(request));
            auth = new MobileBridgeAuthorization(MobileBridgeCredential.Device, pairedDevice);
            newDeviceToken = deviceToken;
            response.SetHeader("Set-Cookie", BuildTokenCookie(deviceToken, TokenCookieMaxAgeSeconds));
        }

        IReadOnlyList<CompanionWorkspaceDto> workspaces = _bridge.GetWorkspaces();
        string activeId = workspaces.Count > 0 ? workspaces[0].Id : "default";
        string activeName = workspaces.Count > 0 ? workspaces[0].Name : "Default Workspace";

        for (int i = 0; i < workspaces.Count; i++)
        {
            if (workspaces[i].IsActive)
            {
                activeId = workspaces[i].Id;
                activeName = workspaces[i].Name;
                break;
            }
        }

        var context = new MobileBridgeContextResponse(
            DeviceName: _bridge.DeviceName,
            ActiveWorkspaceId: activeId,
            ActiveWorkspaceName: activeName,
            Workspaces: workspaces,
            AllowedSizes: AllowedSizes,
            MaxUploadBytes: MaxUploadBytes,
            CanPushToPhone: _bridge.AllowPushToPhone,
            Recent: _bridge.RecentItems,
            Secure: request.IsSecure,
            HttpsPort: 0,
            PairedAs: auth.Device?.Label,
            CanInstallAsApp: true,
            DeviceToken: newDeviceToken);

        await WriteJsonAsync(response, 200, context, MobileBridgeJsonContext.Default.MobileBridgeContextResponse, ct)
            .ConfigureAwait(false);
    }

    // ── Pin (link or note) ─────────────────────────────────────────────────────

    private async Task HandlePinAsync(MobileHttpRequest request, MobileHttpResponse response, CancellationToken ct)
    {
        string? contentType = request.Header("Content-Type");
        if (contentType == null || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            // Blocks simple cross-site form posts, which cannot set an application/json content type.
            await FailAsync(response, 400, "Content-Type must be application/json.", ct).ConfigureAwait(false);
            return;
        }

        MobileBodyReadResult read = await request.ReadBodyAsync(MaxPinBodyBytes, ct).ConfigureAwait(false);
        switch (read.Status)
        {
            case MobileBodyReadStatus.TooLarge:
                await FailAsync(response, 413, "Payload too large: request body exceeds 64 KB.", ct).ConfigureAwait(false);
                return;
            case MobileBodyReadStatus.Empty:
            case MobileBodyReadStatus.Truncated:
                await FailAsync(response, 400, "Empty or truncated request body.", ct).ConfigureAwait(false);
                return;
        }

        CompanionPinRequest? pinRequest;
        try
        {
            pinRequest = JsonSerializer.Deserialize(
                read.Data.AsSpan(),
                CompanionJsonContext.Default.CompanionPinRequest);
        }
        catch (JsonException)
        {
            await FailAsync(response, 400, "Invalid JSON payload.", ct).ConfigureAwait(false);
            return;
        }

        if (!_mutationLimit.TryAcquire(request.RemoteAddress.ToString()))
        {
            await FailAsync(response, 429, "Too many pins in a row. Wait a moment and retry.", ct).ConfigureAwait(false);
            return;
        }

        var validation = CompanionPinValidator.Validate(pinRequest, _bridge.GetWorkspaces);
        if (validation.Request == null)
        {
            await FailAsync(response, validation.StatusCode, validation.Error ?? "Invalid pin request.", ct)
                .ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(LocalCompanionService.DispatcherTimeoutSeconds));

        try
        {
            CompanionPinResult result = await _bridge.PinAsync(validation.Request, timeoutCts.Token).ConfigureAwait(false);

            _bridge.RecordRecent(new MobileBridgeRecentItemDto(
                Id: result.TileId ?? Guid.NewGuid().ToString("N"),
                Direction: "toPc",
                Kind: !string.IsNullOrWhiteSpace(validation.Request.Note) ? "note" : "link",
                Title: FirstNonEmpty(validation.Request.Title, validation.Request.Url, "Pinned item"),
                Url: string.IsNullOrWhiteSpace(validation.Request.Url) ? null : validation.Request.Url,
                Note: string.IsNullOrWhiteSpace(validation.Request.Note) ? null : validation.Request.Note,
                SentAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Duplicate: result.Duplicate));

            await WritePinResultAsync(response, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response.SetHeader("Retry-After", "2");
            await FailAsync(response, 503, "Service Unavailable: desktop UI thread busy. Please retry.", ct)
                .ConfigureAwait(false);
        }
    }

    // ── Upload (raw image bytes) ───────────────────────────────────────────────

    private async Task HandleUploadAsync(MobileHttpRequest request, MobileHttpResponse response, CancellationToken ct)
    {
        string? contentType = request.Header("Content-Type");
        string? extension = contentType != null && ImageExtensions.TryGetValue(contentType, out string? ext) ? ext : null;
        if (extension == null)
        {
            await FailAsync(response, 415, "Unsupported image type. Send JPEG, PNG, WebP, GIF, or BMP.", ct)
                .ConfigureAwait(false);
            return;
        }

        if (request.ContentLength <= 0)
        {
            await FailAsync(response, 400, "Empty upload.", ct).ConfigureAwait(false);
            return;
        }

        if (request.ContentLength > MaxUploadBytes)
        {
            await FailAsync(
                response,
                413,
                $"Photo too large. Limit is {MaxUploadBytes / (1024 * 1024)} MB.",
                ct).ConfigureAwait(false);
            return;
        }

        if (!_mutationLimit.TryAcquire(request.RemoteAddress.ToString()))
        {
            await FailAsync(response, 429, "Too many uploads in a row. Wait a moment and retry.", ct).ConfigureAwait(false);
            return;
        }

        if (!TryBeginUpload())
        {
            await FailAsync(response, 429, "Another upload is already in progress. Try again in a moment.", ct)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await ProcessUploadAsync(request, response, extension, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeUploads);
        }
    }

    /// <summary>
    /// Lock-free upload gate. Keeps at most two uploads writing to disk at once without owning a
    /// disposable primitive, so the endpoints object needs no teardown of its own.
    /// </summary>
    private bool TryBeginUpload()
    {
        int current = Volatile.Read(ref _activeUploads);

        while (true)
        {
            if (current >= MaxConcurrentUploads)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeUploads, current + 1, current) == current)
            {
                return true;
            }

            current = Volatile.Read(ref _activeUploads);
        }
    }

    private async Task ProcessUploadAsync(
        MobileHttpRequest request,
        MobileHttpResponse response,
        string extension,
        CancellationToken ct)
    {
        string imagesDir = AppPaths.PastedImagesDir;
        string tempPath = Path.Combine(imagesDir, $"phone_{Guid.NewGuid():N}.tmp");
        string? finalPath = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(UploadTimeoutSeconds));

        try
        {
            Directory.CreateDirectory(imagesDir);

            // Hash while streaming: the content-addressed file name gives free deduplication
            // and guarantees the on-disk name never comes from the client.
            byte[] hash;
            long written;

            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                bool copied = await request
                    .CopyBodyToAsync(fileStream, MaxUploadBytes, timeoutCts.Token, hasher)
                    .ConfigureAwait(false);

                if (!copied)
                {
                    await FailAsync(response, 400, "Upload was interrupted or exceeded the size limit.", ct)
                        .ConfigureAwait(false);
                    return;
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);
                hash = hasher.GetHashAndReset();
                written = fileStream.Length;
            }

            if (written <= 0)
            {
                await FailAsync(response, 400, "Empty upload.", ct).ConfigureAwait(false);
                return;
            }

            if (!LooksLikeImage(tempPath))
            {
                await FailAsync(response, 415, "That file is not a readable image.", ct).ConfigureAwait(false);
                return;
            }

            finalPath = Path.Combine(imagesDir, $"img_{Convert.ToHexStringLower(hash)}{extension}");

            if (File.Exists(finalPath))
            {
                TryDelete(tempPath);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }

            string title = BuildUploadTitle(request.QueryValue("name"));

            (int spanX, int spanY) = ResolveSpan(request);
            string? workspaceId = request.QueryValue("workspaceId");

            using var pinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pinCts.CancelAfter(TimeSpan.FromSeconds(LocalCompanionService.DispatcherTimeoutSeconds + 1));

            CompanionPinResult result = await _bridge.PinLocalImageAsync(
                finalPath,
                title,
                workspaceId,
                spanX,
                spanY,
                pinCts.Token).ConfigureAwait(false);

            if (result.Success)
            {
                _bridge.RecordRecent(new MobileBridgeRecentItemDto(
                    Id: result.TileId ?? Guid.NewGuid().ToString("N"),
                    Direction: "toPc",
                    Kind: "photo",
                    Title: title,
                    Url: null,
                    Note: null,
                    SentAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Duplicate: result.Duplicate));
            }

            await WritePinResultAsync(response, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await FailAsync(response, 503, "Upload timed out. Check the Wi-Fi connection and retry.", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Upload failed");
            await FailAsync(response, 500, "Upload failed on the PC.", ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    // ── Server-sent events ─────────────────────────────────────────────────────

    private async Task HandleEventsAsync(MobileHttpRequest request, MobileHttpResponse response, CancellationToken ct)
    {
        var client = new MobileBridgeSseClient
        {
            DeviceLabel = request.RemoteAddress.ToString()
        };

        try
        {
            if (!_bridge.Hub.TryRegister(client))
            {
                await FailAsync(response, 503, "Too many phones are already listening. Close an old tab and retry.", ct)
                    .ConfigureAwait(false);
                return;
            }

            HttpChunkedWriter? writer = await response.BeginEventStreamAsync(ct).ConfigureAwait(false);
            if (writer == null)
            {
                return;
            }

            await writer.WriteAsync(RetryFrame, ct).ConfigureAwait(false);

            ChannelReader<string> reader = client.Reader;
            Task<bool> pendingRead = reader.WaitToReadAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task delay = Task.Delay(SseHeartbeat, delayCts.Token);

                Task finished = await Task.WhenAny(pendingRead, delay).ConfigureAwait(false);
                delayCts.Cancel();

                if (finished == pendingRead)
                {
                    if (!await pendingRead.ConfigureAwait(false))
                    {
                        break;
                    }

                    while (reader.TryRead(out string? frame))
                    {
                        await writer
                            .WriteAsync(Encoding.UTF8.GetBytes(frame), ct)
                            .ConfigureAwait(false);
                    }

                    pendingRead = reader.WaitToReadAsync(ct).AsTask();
                }
                else
                {
                    // Comment frame keeps the connection and any intermediate proxy awake.
                    await writer.WriteAsync(PingFrame, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown.
        }
        catch (IOException)
        {
            // Phone navigated away or dropped off the network.
        }
        catch (ObjectDisposedException)
        {
            // Bridge shutting down.
        }
        finally
        {
            // Unregister removes the subscriber and disposes it; the extra Dispose covers the
            // rejected-registration path and is idempotent.
            _bridge.Hub.Unregister(client);
            client.Dispose();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task WritePinResultAsync(MobileHttpResponse response, CompanionPinResult result, CancellationToken ct)
    {
        var payload = new CompanionPinResponse(
            Success: result.Success,
            Duplicate: result.Duplicate,
            TileId: result.TileId,
            Col: result.Col,
            Row: result.Row,
            Message: result.Message);

        int status = result.Success ? 200 : 400;
        await WriteJsonAsync(response, status, payload, CompanionJsonContext.Default.CompanionPinResponse, ct)
            .ConfigureAwait(false);
    }

    private static Task WriteJsonAsync<T>(
        MobileHttpResponse response,
        int statusCode,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        response.SetStatus(statusCode);
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        return response.WriteJsonAsync(utf8, ct);
    }

    /// <summary>
    /// Builds the token cookie. Written as a first-party, host-scoped cookie so it also travels to
    /// the HTTPS port, which is what lets a phone paired over HTTP keep working after installing the
    /// certificate. A zero max-age clears it.
    /// </summary>
    private static string BuildTokenCookie(string token, int maxAgeSeconds) =>
        $"{TokenCookieName}={token}; Path=/; Max-Age={maxAgeSeconds}; SameSite=Lax; HttpOnly";

    /// <summary>
    /// Short label for a paired phone, derived from its User-Agent. Best effort only: the desktop's
    /// paired-phone list is cosmetic, so an unrecognized agent falls back to a generic name.
    /// </summary>
    private static string? DescribePhone(MobileHttpRequest request)
    {
        string? agent = request.Header("User-Agent");
        if (string.IsNullOrWhiteSpace(agent))
        {
            return null;
        }

        if (agent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android phone";
        }

        if (agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "iPhone";
        }

        if (agent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return "iPad";
        }

        return null;
    }

    private static Task FailAsync(MobileHttpResponse response, int statusCode, string message, CancellationToken ct)
    {
        response.SetStatus(statusCode);
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(
            new CompanionErrorResponse(message),
            CompanionJsonContext.Default.CompanionErrorResponse);
        return response.WriteJsonAsync(utf8, ct);
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return "Pinned item";
    }

    private static (int SpanX, int SpanY) ResolveSpan(MobileHttpRequest request)
    {
        int spanX = ParseSpan(request.QueryValue("spanX"), 2);
        int spanY = ParseSpan(request.QueryValue("spanY"), 2);

        if ((spanX == 2 && spanY == 2) || (spanX == 4 && spanY == 2))
        {
            return (spanX, spanY);
        }

        return (2, 2);
    }

    private static int ParseSpan(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    /// <summary>Uses the client file name only as a display title, never as a path.</summary>
    private static string BuildUploadTitle(string? clientName)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(clientName))
        {
            return $"Photo {stamp}";
        }

        string name = clientName.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        name = Path.GetFileNameWithoutExtension(name).Trim();
        if (name.Length == 0)
        {
            return $"Photo {stamp}";
        }

        if (name.Length > 128)
        {
            name = name[..128];
        }

        return name;
    }

    private static bool LooksLikeImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32, false);
            Span<byte> head = stackalloc byte[12];
            int read = stream.Read(head);
            if (read < 4)
            {
                return false;
            }

            bool isJpeg = head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
            bool isPng = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
            bool isGif = head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38;
            bool isBmp = head[0] == 0x42 && head[1] == 0x4D;
            bool isWebp = read >= 12 &&
                          head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
                          head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50;

            return isJpeg || isPng || isGif || isBmp || isWebp;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Failed to sniff uploaded image header");
            return false;
        }
    }

    private static string? ReadCookie(MobileHttpRequest request, string name)
    {
        string? cookieHeader = request.Header("Cookie");
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return null;
        }

        foreach (string part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (part.AsSpan(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim();
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private const string PairingExpiredHtml =
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<title>MetroHub</title></head>" +
        "<body style=\"margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
        "background:#0f1115;color:#f2f4f8;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif\">" +
        "<div style=\"max-width:22rem;padding:2rem;text-align:center\">" +
        "<h1 style=\"font-size:1.25rem;margin:0 0 .75rem\">This phone is not paired</h1>" +
        "<p style=\"margin:0;color:#9aa3b2;line-height:1.5\">Scan the QR code on the MetroHub Phone Bridge tile " +
        "once to pair. After that this phone stays paired until you forget it on the PC or unpair it here.</p>" +
        "</div></body></html>";
}
