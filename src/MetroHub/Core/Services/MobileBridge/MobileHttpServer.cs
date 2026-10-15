using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>Outcome of a bounded request-body read.</summary>
public enum MobileBodyReadStatus
{
    Ok,
    Empty,
    TooLarge,
    Truncated
}

/// <summary>Result of a bounded request-body read.</summary>
public readonly struct MobileBodyReadResult
{
    public MobileBodyReadResult(MobileBodyReadStatus status, byte[]? data)
    {
        Status = status;
        Data = data;
    }

    public MobileBodyReadStatus Status { get; }

    public byte[]? Data { get; }
}

/// <summary>
/// Immutable, already-parsed inbound request. The body is exposed as a stream so large uploads
/// never land on the managed large object heap by default.
/// </summary>
public sealed class MobileHttpRequest
{
    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.OrdinalIgnoreCase);

    public string Method { get; init; } = "GET";

    public string RawPath { get; init; } = "/";

    public string RawQuery { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Query { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyMap;

    public IPAddress RemoteAddress { get; init; } = IPAddress.Loopback;

    public long ContentLength { get; init; }

    /// <summary>
    /// Raw connection stream. Read the declared body through <see cref="ReadBodyAsync"/> or
    /// <see cref="CopyBodyToAsync"/> instead: they also serve the body bytes that arrived in the
    /// same socket read as the header block, which a plain read of this stream cannot see.
    /// </summary>
    public Stream Body { get; init; } = Stream.Null;

    public bool IsHttp11 { get; init; } = true;

    /// <summary>True when the request arrived over TLS.</summary>
    public bool IsSecure { get; init; }

    public string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;

    public string? QueryValue(string name) =>
        Query.TryGetValue(name, out string? value) ? value : null;

    /// <summary>
    /// Bytes of the declared body already pulled off the socket. The transport uses this to
    /// decide whether a graceful drain is needed before closing the connection.
    /// </summary>
    internal long ConsumedLength { get; private set; }

    private byte[] _prefix = Array.Empty<byte>();
    private int _prefixOffset;

    /// <summary>
    /// Accepts the body bytes that came off the socket together with the header block. They are
    /// already read, so they must be served before touching the socket again; they also count as
    /// consumed when the transport decides whether a graceful drain is still needed.
    /// </summary>
    internal void SeedBody(ReadOnlySpan<byte> prefix)
    {
        int take = (int)Math.Min(prefix.Length, ContentLength);
        if (take <= 0)
        {
            return;
        }

        _prefix = prefix[..take].ToArray();
        _prefixOffset = 0;
        ConsumedLength = take;
    }

    /// <summary>
    /// Reads the next slice of the declared body, buffered leftovers first and then the socket.
    /// Only bytes pulled off the socket advance <see cref="ConsumedLength"/>.
    /// </summary>
    private async Task<int> ReadBodyChunkAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_prefixOffset < _prefix.Length)
        {
            int available = Math.Min(_prefix.Length - _prefixOffset, count);
            Array.Copy(_prefix, _prefixOffset, buffer, offset, available);
            _prefixOffset += available;
            return available;
        }

        int read = await Body.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        if (read > 0)
        {
            ConsumedLength += read;
        }

        return read;
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of the declared body.
    /// Returns TooLarge without reading when the declared length already exceeds the cap.
    /// </summary>
    public async Task<MobileBodyReadResult> ReadBodyAsync(int maxBytes, CancellationToken ct)
    {
        if (ContentLength <= 0)
        {
            return new MobileBodyReadResult(MobileBodyReadStatus.Empty, null);
        }

        if (ContentLength > maxBytes)
        {
            return new MobileBodyReadResult(MobileBodyReadStatus.TooLarge, null);
        }

        byte[] buffer = new byte[(int)ContentLength];
        int offset = 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(MobileHttpServer.BodyReadIdleTimeout);

        try
        {
            while (offset < buffer.Length)
            {
                timeoutCts.CancelAfter(MobileHttpServer.BodyReadIdleTimeout);

                int read = await ReadBodyChunkAsync(buffer, offset, buffer.Length - offset, timeoutCts.Token)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MobileBodyReadResult(MobileBodyReadStatus.Truncated, null);
        }

        if (offset == 0)
        {
            return new MobileBodyReadResult(MobileBodyReadStatus.Empty, null);
        }

        if (offset != buffer.Length)
        {
            return new MobileBodyReadResult(MobileBodyReadStatus.Truncated, null);
        }

        return new MobileBodyReadResult(MobileBodyReadStatus.Ok, buffer);
    }

    /// <summary>
    /// Streams the declared body to <paramref name="destination"/> while enforcing a hard byte cap.
    /// When <paramref name="hash"/> is supplied every byte is folded into it during the copy, so
    /// uploaded files can be content-addressed without a second pass over the data.
    /// Returns false when the cap is exceeded or the connection dies mid-transfer.
    /// </summary>
    public async Task<bool> CopyBodyToAsync(
        Stream destination,
        long maxBytes,
        CancellationToken ct,
        System.Security.Cryptography.IncrementalHash? hash = null)
    {
        if (ContentLength <= 0 || ContentLength > maxBytes)
        {
            return false;
        }

        byte[] buffer = new byte[81920];
        long remaining = ContentLength;
        long written = 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (remaining > 0)
        {
            timeoutCts.CancelAfter(MobileHttpServer.BodyReadIdleTimeout);

            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read;
            try
            {
                read = await ReadBodyChunkAsync(buffer, 0, toRead, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }

            if (read <= 0)
            {
                return false;
            }

            written += read;

            if (written > maxBytes)
            {
                return false;
            }

            if (hash != null)
            {
                hash.AppendData(buffer, 0, read);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }

        return written == ContentLength;
    }
}

/// <summary>
/// Writes exactly one HTTP response over a connection-scoped stream.
/// Every response closes the connection afterwards, which keeps the server free of pipelining state.
/// </summary>
public sealed class MobileHttpResponse
{
    private static readonly byte[] ChunkTerminator = Encoding.ASCII.GetBytes("0\r\n\r\n");

    private readonly Stream _stream;
    private readonly bool _isHttp11;
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    internal MobileHttpResponse(Stream stream, bool isHttp11)
    {
        _stream = stream;
        _isHttp11 = isHttp11;
    }

    public int StatusCode { get; private set; } = 200;

    public bool Started => _started;

    /// <summary>True once the handler took over the raw connection (event stream).</summary>
    public bool IsStreaming { get; private set; }

    public void SetStatus(int statusCode) => StatusCode = statusCode;

    public void SetHeader(string name, string value) => _headers[name] = value;

    public Task WriteTextAsync(string body, string contentType, CancellationToken ct) =>
        WriteAsync(Encoding.UTF8.GetBytes(body), contentType, ct);

    public Task WriteJsonAsync(byte[] utf8Json, CancellationToken ct) =>
        WriteAsync(utf8Json, "application/json; charset=utf-8", ct);

    public async Task WriteAsync(byte[] payload, string contentType, CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        SetHeader("Content-Type", contentType);
        await WriteHeadAsync(payload.Length, chunked: false, ct).ConfigureAwait(false);
        _started = true;

        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a chunked <c>text/event-stream</c> response. Returns a writer the caller keeps
    /// feeding until the client disconnects.
    /// </summary>
    public async Task<HttpChunkedWriter?> BeginEventStreamAsync(CancellationToken ct)
    {
        if (_started)
        {
            return null;
        }

        SetHeader("Content-Type", "text/event-stream; charset=utf-8");
        SetHeader("Cache-Control", "no-cache, no-store, no-transform");
        SetHeader("X-Accel-Buffering", "no");

        bool chunked = _isHttp11;
        await WriteHeadAsync(0, chunked, ct).ConfigureAwait(false);
        _started = true;
        IsStreaming = true;

        return new HttpChunkedWriter(_stream, chunked);
    }

    internal async Task CloseAsync()
    {
        if (!_started || !IsStreaming)
        {
            return;
        }

        try
        {
            await _stream.WriteAsync(ChunkTerminator, CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Client already gone; nothing to flush.
        }
    }

    internal static byte[] EncodeChunkHeader(int length) =>
        Encoding.ASCII.GetBytes(length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");

    private async Task WriteHeadAsync(long contentLength, bool chunked, CancellationToken ct)
    {
        var head = new StringBuilder(320);
        head.Append("HTTP/1.1 ").Append(StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(ReasonPhrase(StatusCode)).Append("\r\n");

        if (!_headers.ContainsKey("Cache-Control"))
        {
            head.Append("Cache-Control: no-store\r\n");
        }

        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("Connection: close\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");

        foreach (var pair in _headers)
        {
            head.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
        }

        if (chunked)
        {
            head.Append("Transfer-Encoding: chunked\r\n");
        }
        else
        {
            head.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        head.Append("\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await _stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        411 => "Length Required",
        413 => "Payload Too Large",
        414 => "URI Too Long",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status"
    };
}

/// <summary>Chunked transfer-encoding writer used by the server-sent-events stream.</summary>
public sealed class HttpChunkedWriter
{
    private static readonly byte[] NewLine = Encoding.ASCII.GetBytes("\r\n");

    private readonly Stream _stream;
    private readonly bool _chunked;

    internal HttpChunkedWriter(Stream stream, bool chunked)
    {
        _stream = stream;
        _chunked = chunked;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length == 0)
        {
            return;
        }

        if (_chunked)
        {
            await _stream.WriteAsync(MobileHttpResponse.EncodeChunkHeader(payload.Length), ct).ConfigureAwait(false);
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
        }
        else
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Minimal HTTP/1.1 server built directly on <see cref="TcpListener"/>.
/// </summary>
/// <remarks>
/// Deliberately avoids <c>HttpListener</c> / HTTP.sys: any non-loopback prefix there requires an
/// elevated process or a <c>netsh http add urlacl</c> reservation, which would break the
/// "no admin prompt" requirement of the phone bridge. Raw sockets need no reservation.
///
/// Scope is intentionally tiny: request line + headers + a declared <c>Content-Length</c> body,
/// one response per connection, no keep-alive, no chunked request bodies, no multipart parsing.
/// Anything outside that contract is rejected explicitly.
/// </remarks>
public sealed class MobileHttpServer : IDisposable
{
    internal static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan BodyReadIdleTimeout = TimeSpan.FromSeconds(30);

    private const int MaxRequestHeaderBytes = 8 * 1024;
    private const int MaxRequestLineBytes = 2048;
    private const int AcceptBacklog = 64;
    private const int MaxConcurrentConnections = 24;
    private const long MaxDrainBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(750);

    private static readonly byte[] HeaderTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");

    private readonly IPAddress _bindAddress;
    private readonly int _requestedPort;
    private readonly Func<MobileHttpRequest, MobileHttpResponse, CancellationToken, Task> _handler;
    private readonly X509Certificate2? _serverCertificate;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly object _lifecycleLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    /// <summary>
    /// Creates a listener. When <paramref name="serverCertificate"/> is set the socket speaks HTTPS,
    /// which is what makes the phone page a secure context (service worker, install, share target).
    /// </summary>
    public MobileHttpServer(
        IPAddress bindAddress,
        int port,
        Func<MobileHttpRequest, MobileHttpResponse, CancellationToken, Task> handler,
        X509Certificate2? serverCertificate = null)
    {
        _bindAddress = bindAddress ?? throw new ArgumentNullException(nameof(bindAddress));
        _requestedPort = port;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _serverCertificate = serverCertificate;
    }

    /// <summary>True when this listener serves HTTPS.</summary>
    public bool IsSecure => _serverCertificate != null;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _listener != null;
            }
        }
    }

    public IPAddress BindAddress => _bindAddress;

    public int ActivePort { get; private set; }

    /// <summary>Requests currently being served. Diagnostics only.</summary>
    public int InFlightRequests { get; private set; }

    /// <summary>Binds the listener. Throws when the port is taken or the address cannot be bound.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_listener != null)
            {
                return;
            }

            var listener = new TcpListener(_bindAddress, _requestedPort);
            listener.Server.NoDelay = true;
            listener.Start(AcceptBacklog);

            _listener = listener;
            ActivePort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);

            Serilog.Log.Information(
                "[MobileBridge] Listening on {Scheme}://{Address}:{Port}/m/",
                _serverCertificate == null ? "http" : "https",
                _bindAddress,
                ActivePort);
        }
    }

    public async Task StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;

        lock (_lifecycleLock)
        {
            listener = _listener;
            cts = _cts;
            acceptTask = _acceptTask;
            _listener = null;
            _cts = null;
            _acceptTask = null;
            ActivePort = 0;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (listener != null)
        {
            try { listener.Dispose(); } catch { }
        }

        if (acceptTask != null)
        {
            try { await acceptTask.ConfigureAwait(false); } catch { }
        }

        if (cts != null)
        {
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                Safe.Log(ex, "[MobileBridge] Accept failed");
                continue;
            }

            _ = Task.Run(() => HandleConnectionSafeAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionSafeAsync(TcpClient client, CancellationToken ct)
    {
        bool slotAcquired = false;

        try
        {
            using (client)
            {
                client.NoDelay = true;

                IPAddress? remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (remoteAddress == null || !LanAddressResolver.IsAllowedPeer(remoteAddress))
                {
                    Serilog.Log.Warning("[MobileBridge] Rejected connection from non-LAN peer {Peer}", remoteAddress);
                    return;
                }

                await _concurrency.WaitAsync(ct).ConfigureAwait(false);
                slotAcquired = true;
                InFlightRequests++;

                using NetworkStream network = client.GetStream();
                using SslStream? tls = _serverCertificate == null
                    ? null
                    : new SslStream(network, leaveInnerStreamOpen: false);

                if (tls != null && !await TryAuthenticateAsync(tls, _serverCertificate!, ct).ConfigureAwait(false))
                {
                    // A plain-HTTP client or a port scanner hit the TLS port; drop it quietly.
                    return;
                }

                await ProcessConnectionAsync(
                    tls ?? (Stream)network,
                    remoteAddress,
                    isSecure: tls != null,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Connection handling failed");
        }
        finally
        {
            if (slotAcquired)
            {
                InFlightRequests--;
                try { _concurrency.Release(); } catch { }
            }
        }
    }

    /// <summary>
    /// Runs the TLS handshake. Failures are normal traffic on a LAN port, so they are reported as a
    /// boolean rather than an exception.
    /// </summary>
    private static async Task<bool> TryAuthenticateAsync(
        SslStream tls,
        X509Certificate2 certificate,
        CancellationToken ct)
    {
        try
        {
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or ObjectDisposedException)
        {
            // A plain-HTTP client or a port scanner hit the TLS port; drop it quietly.
            return false;
        }
    }

    private async Task ProcessConnectionAsync(Stream stream, IPAddress remoteAddress, bool isSecure, CancellationToken ct)
    {
        byte[] headerBytes;
        try
        {
            headerBytes = await ReadHeaderBlockAsync(stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TrySendErrorAsync(stream, isHttp11: true, 408, "Request timed out.").ConfigureAwait(false);
            return;
        }

        if (headerBytes.Length == 0)
        {
            return;
        }

        if (!TryParseRequest(
                headerBytes,
                remoteAddress,
                isSecure,
                stream,
                out MobileHttpRequest? request,
                out string? parseError,
                out int errorStatus))
        {
            await TrySendErrorAsync(stream, isHttp11: true, errorStatus, parseError ?? "Malformed request.")
                .ConfigureAwait(false);
            return;
        }

        var response = new MobileHttpResponse(stream, request!.IsHttp11);

        try
        {
            await _handler(request, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client vanished mid-request.
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] Request handler failed");
            await TrySendErrorAsync(stream, request.IsHttp11, 500, "Internal bridge error.", response)
                .ConfigureAwait(false);
        }
        finally
        {
            await response.CloseAsync().ConfigureAwait(false);

            if (!response.Started)
            {
                await TrySendErrorAsync(stream, request.IsHttp11, 204, string.Empty, response).ConfigureAwait(false);
            }

            await DrainUnreadBodyAsync(stream, request).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// When a request is rejected before its declared body has been read, closing the socket makes
    /// the kernel answer with a TCP reset, which can destroy the rejection response the client is
    /// about to read. Draining the remainder (bounded and best effort) keeps it deliverable.
    /// </summary>
    private static async Task DrainUnreadBodyAsync(Stream stream, MobileHttpRequest request)
    {
        long remaining = request.ContentLength - request.ConsumedLength;
        if (remaining <= 0 || remaining > MaxDrainBytes)
        {
            return;
        }

        byte[] scratch = new byte[8192];
        using var cts = new CancellationTokenSource(DrainTimeout);

        try
        {
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(scratch.Length, remaining);
                int read = await stream.ReadAsync(scratch.AsMemory(0, toRead), cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                remaining -= read;
            }
        }
        catch
        {
            // Opportunistic only: the response is already on the wire.
        }
    }

    private static async Task TrySendErrorAsync(
        Stream stream,
        bool isHttp11,
        int statusCode,
        string message,
        MobileHttpResponse? existing = null)
    {
        var response = existing ?? new MobileHttpResponse(stream, isHttp11);
        if (response.Started)
        {
            return;
        }

        try
        {
            response.SetStatus(statusCode);
            byte[] payload = statusCode == 204
                ? Array.Empty<byte>()
                : JsonSerializer.SerializeToUtf8Bytes(
                    new CompanionErrorResponse(message),
                    CompanionJsonContext.Default.CompanionErrorResponse);

            await response
                .WriteAsync(payload, "application/json; charset=utf-8", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort only; the client may already be gone.
        }
    }

    /// <summary>Reads raw header bytes up to and including the terminating CRLFCRLF.</summary>
    private static async Task<byte[]> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxRequestHeaderBytes];
        int total = 0;
        int scanFrom = 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HeaderReadTimeout);

        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), timeoutCts.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                return total > 0 ? buffer[..total] : Array.Empty<byte>();
            }

            total += read;
            if (FindTerminator(buffer, Math.Max(0, scanFrom - 3), total) >= 0)
            {
                return buffer[..total];
            }

            scanFrom = total;
        }

        return buffer[..total];
    }

    private static int FindTerminator(byte[] buffer, int start, int end)
    {
        for (int i = Math.Max(0, start); i <= end - HeaderTerminator.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < HeaderTerminator.Length; j++)
            {
                if (buffer[i + j] != HeaderTerminator[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseRequest(
        byte[] headerBytes,
        IPAddress remoteAddress,
        bool isSecure,
        Stream bodyStream,
        out MobileHttpRequest? request,
        out string? error,
        out int errorStatus)
    {
        request = null;
        error = null;
        errorStatus = 400;

        int terminatorIndex = FindTerminator(headerBytes, 0, headerBytes.Length);
        if (terminatorIndex < 0)
        {
            error = "Request header block too large or missing terminator.";
            errorStatus = 431;
            return false;
        }

        string headerText = Encoding.Latin1.GetString(headerBytes, 0, terminatorIndex);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0)
        {
            error = "Missing request line.";
            return false;
        }

        if (lines[0].Length > MaxRequestLineBytes)
        {
            error = "Request line too long.";
            errorStatus = 414;
            return false;
        }

        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            error = "Malformed request line.";
            return false;
        }

        string method = parts[0].ToUpperInvariant();
        string target = parts[1];
        bool isHttp11 = parts.Length >= 3 &&
                        parts[2].Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);

        if (target.Length > MaxRequestLineBytes)
        {
            error = "Request target too long.";
            errorStatus = 414;
            return false;
        }

        if (!target.StartsWith('/'))
        {
            error = "Only origin-form request targets are supported.";
            return false;
        }

        int queryIndex = target.IndexOf('?');
        string rawPath = queryIndex >= 0 ? target[..queryIndex] : target;
        string rawQuery = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // Header folding and duplicate smuggling are not supported: first value wins.
            if (!headers.ContainsKey(name))
            {
                headers[name] = value;
            }
        }

        if (headers.TryGetValue("Transfer-Encoding", out string? transferEncoding) &&
            !string.IsNullOrEmpty(transferEncoding))
        {
            error = "Chunked request bodies are not supported. Send a Content-Length body.";
            errorStatus = 411;
            return false;
        }

        long contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthHeader) &&
            !string.IsNullOrWhiteSpace(lengthHeader))
        {
            if (!long.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                contentLength < 0)
            {
                error = "Invalid Content-Length.";
                return false;
            }
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawQuery.Length > 0)
        {
            foreach (string pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair[..eq] : pair;
                string value = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
                key = WebUtility.UrlDecode(key);
                if (key.Length > 0 && !query.ContainsKey(key))
                {
                    query[key] = WebUtility.UrlDecode(value);
                }
            }
        }

        request = new MobileHttpRequest
        {
            Method = method,
            RawPath = rawPath,
            RawQuery = rawQuery,
            Query = query,
            Headers = headers,
            RemoteAddress = remoteAddress,
            ContentLength = contentLength,
            Body = bodyStream,
            IsHttp11 = isHttp11,
            IsSecure = isSecure
        };

        // Anything the socket handed us past the header terminator is body data already.
        int bodyStart = terminatorIndex + HeaderTerminator.Length;
        if (bodyStart < headerBytes.Length)
        {
            request.SeedBody(headerBytes.AsSpan(bodyStart));
        }

        return true;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _concurrency.Dispose();
    }
}
