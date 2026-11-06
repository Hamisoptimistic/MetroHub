using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// Minimal Telegram Bot API transport. The bot token appears in every URL, so this class
/// never logs a URL and never puts one into an exception message.
/// The HttpClient must have an infinite Timeout; per-call deadlines come from CancellationTokens.
/// </summary>
public sealed class TelegramHttpTransport : ITelegramTransport
{
    private readonly HttpClient _http;
    private readonly string _apiRoot;
    private readonly string _fileRoot;

    public TelegramHttpTransport(HttpClient http, Uri apiBase, string token, bool allowInsecureLoopbackForTests = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // HTTPS only. Plain HTTP is accepted solely for loopback AND when tests explicitly opt in.
        if (apiBase.Scheme != Uri.UriSchemeHttps && !(allowInsecureLoopbackForTests && apiBase.IsLoopback))
            throw new ArgumentException("Telegram traffic must use HTTPS.", nameof(apiBase));

        _http = http;
        var cleanToken = token.Trim();
        var root = apiBase.GetLeftPart(UriPartial.Authority);
        _apiRoot = $"{root}/bot{cleanToken}/";
        _fileRoot = $"{root}/file/bot{cleanToken}/";
    }

    /// <summary>The only sanctioned place to construct an HttpClient for Telegram traffic.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "SocketsHttpHandler ownership transferred to HttpClient with disposeHandler: true.")]
    public static HttpClient CreateDedicatedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
        };

        try
        {
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // long-poll + uploads; deadlines come from CTS
            };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    public async Task<TelegramBotUser> GetMeAsync(CancellationToken ct)
    {
        var url = $"{_apiRoot}getMe";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        try
        {
            var result = doc.RootElement.GetProperty("result");
            var id = result.GetProperty("id").GetInt64();
            var isBot = result.TryGetProperty("is_bot", out var ib) && ib.GetBoolean();
            var firstName = result.TryGetProperty("first_name", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString() ?? string.Empty
                : string.Empty;
            var username = result.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String
                ? un.GetString()
                : null;

            return new TelegramBotUser(id, isBot, firstName, username);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new JsonException("Unexpected getMe response shape.", e);
        }
    }

    public async Task<IReadOnlyList<RawUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var url = $"{_apiRoot}getUpdates?timeout={timeoutSeconds}&limit=100&allowed_updates=%5B%22message%22%5D";
        if (offset != 0) url += $"&offset={offset}";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        try
        {
            var result = doc.RootElement.GetProperty("result");
            var list = new List<RawUpdate>(result.GetArrayLength());
            foreach (var element in result.EnumerateArray())
                list.Add(new RawUpdate(element.GetProperty("update_id").GetInt64(), element.Clone()));
            return list;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new JsonException("Unexpected getUpdates response shape.", e); // transient for the engine
        }
    }

    public async Task<TelegramFileInfo> GetFileAsync(string fileId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        var url = $"{_apiRoot}getFile?file_id={Uri.EscapeDataString(fileId)}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        try
        {
            var r = doc.RootElement.GetProperty("result");
            return new TelegramFileInfo(
                r.GetProperty("file_id").GetString()!,
                r.GetProperty("file_unique_id").GetString()!,
                r.TryGetProperty("file_size", out var size) && size.TryGetInt64(out var s) ? s : null,
                r.TryGetProperty("file_path", out var path) ? path.GetString() : null);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new JsonException("Unexpected getFile response shape.", e);
        }
    }

    public async Task DownloadAsync(string filePath, Stream destination, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));

        // file_path comes from Telegram, but never trust it blindly.
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains("..", StringComparison.Ordinal) || filePath.Contains('\\'))
            throw new ArgumentException("Invalid file path.", nameof(filePath));

        HttpResponseMessage response;
        try
        {
            response = await _http
                .GetAsync(_fileRoot + filePath.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Redact URI to guarantee token never leaks in logs or messages
            throw new HttpRequestException("HTTP download request failed.", ex.InnerException, ex.StatusCode);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new TelegramApiException((int)response.StatusCode, "Download failed");

            // Declared size is a hint only; the streamed count below is the real guard.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                throw new FileTooLargeException(maxBytes);

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new FileTooLargeException(maxBytes);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public async Task<JsonDocument> SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfZero(chatId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, 4096);

        var url = $"{_apiRoot}sendMessage";
        var payload = new { chat_id = chatId, text };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return await PostJsonAsync(url, content, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(string url, HttpContent content, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsync(url, content, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Redact URI to guarantee token never leaks in logs or messages
            throw new HttpRequestException("HTTP request to Telegram API failed.", ex.InnerException, ex.StatusCode);
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                // e.g. an HTML 502 from a proxy. Keep the status, drop the body.
                throw new TelegramApiException((int)response.StatusCode, "Non-JSON error response");
            }

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return doc;

            var code = (int)response.StatusCode;
            var description = "unknown";
            int? retryAfter = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error_code", out var ec) && ec.TryGetInt32(out var c)) code = c;
                if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    description = d.GetString() ?? description;
                if (root.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("retry_after", out var ra) && ra.TryGetInt32(out var r)) retryAfter = r;
            }

            doc.Dispose();
            throw new TelegramApiException(code, description, retryAfter);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Redact URI to guarantee token never leaks in logs or messages
            throw new HttpRequestException("HTTP request to Telegram API failed.", ex.InnerException, ex.StatusCode);
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                // e.g. an HTML 502 from a proxy. Keep the status, drop the body.
                throw new TelegramApiException((int)response.StatusCode, "Non-JSON error response");
            }

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return doc;

            var code = (int)response.StatusCode;
            var description = "unknown";
            int? retryAfter = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error_code", out var ec) && ec.TryGetInt32(out var c)) code = c;
                if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    description = d.GetString() ?? description;
                if (root.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("retry_after", out var ra) && ra.TryGetInt32(out var r)) retryAfter = r;
            }

            doc.Dispose();
            throw new TelegramApiException(code, description, retryAfter);
        }
    }
}
