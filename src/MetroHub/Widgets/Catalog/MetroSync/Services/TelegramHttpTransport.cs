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
        ArgumentOutOfRangeException.ThrowIfZero(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, 4096, nameof(text));

        var url = $"{_apiRoot}sendMessage";
        var payload = new { chat_id = chatId, text };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return await PostJsonAsync(url, content, ct).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of child HttpContent objects is transferred to MultipartFormDataContent which disposes them.")]
    public async Task<JsonDocument> SendPhotoAsync(long chatId, Stream imageStream, string? caption, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(chatId);
        ArgumentNullException.ThrowIfNull(imageStream);

        const long maxPhotoSizeBytes = 10 * 1024 * 1024; // 10 MB
        if (imageStream.CanSeek && imageStream.Length > maxPhotoSizeBytes)
        {
            throw new FileTooLargeException(maxPhotoSizeBytes);
        }

        if (caption is not null && caption.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(caption), "Caption exceeds 4,096 characters.");
        }

        string contentType = "image/jpeg";
        string fileName = "photo.jpg";

        if (imageStream.CanSeek)
        {
            if (imageStream.Position != 0)
            {
                imageStream.Position = 0;
            }

            if (imageStream.Length >= 8)
            {
                var header = new byte[8];
                int read = imageStream.Read(header, 0, 8);
                imageStream.Position = 0;
                if (read == 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                {
                    contentType = "image/png";
                    fileName = "photo.png";
                }
            }
        }

        var url = $"{_apiRoot}sendPhoto";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");

        if (!string.IsNullOrEmpty(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }

        Stream uploadStream = imageStream.CanSeek ? imageStream : new BoundedReadStream(imageStream, maxPhotoSizeBytes);
        var streamContent = new StreamContent(uploadStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(streamContent, "photo", fileName);

        return await PostJsonAsync(url, form, ct).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of child HttpContent objects is transferred to MultipartFormDataContent which disposes them.")]
    public async Task<JsonDocument> SendDocumentAsync(long chatId, Stream documentStream, string fileName, string? caption, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(chatId);
        ArgumentNullException.ThrowIfNull(documentStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        const long maxDocSizeBytes = 50 * 1024 * 1024; // 50 MB
        if (documentStream.CanSeek && documentStream.Length > maxDocSizeBytes)
        {
            throw new FileTooLargeException(maxDocSizeBytes);
        }

        if (caption is not null && caption.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(caption), "Caption exceeds 4,096 characters.");
        }

        if (documentStream.CanSeek && documentStream.Position != 0)
        {
            documentStream.Position = 0;
        }

        var url = $"{_apiRoot}sendDocument";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");

        if (!string.IsNullOrEmpty(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }

        Stream uploadStream = documentStream.CanSeek ? documentStream : new BoundedReadStream(documentStream, maxDocSizeBytes);
        var streamContent = new StreamContent(uploadStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(streamContent, "document", fileName);

        return await PostJsonAsync(url, form, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetUserProfilePhotoBytesAsync(long userId, CancellationToken ct)
    {
        try
        {
            var url = $"{_apiRoot}getUserProfilePhotos?user_id={userId}&limit=1";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("photos", out var photos) ||
                photos.ValueKind != JsonValueKind.Array ||
                photos.GetArrayLength() == 0)
            {
                return null;
            }

            var firstPhotoSizes = photos[0];
            if (firstPhotoSizes.ValueKind != JsonValueKind.Array ||
                firstPhotoSizes.GetArrayLength() == 0)
            {
                return null;
            }

            var firstSize = firstPhotoSizes[0];
            if (!firstSize.TryGetProperty("file_id", out var fileIdProp) ||
                fileIdProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var fileId = fileIdProp.GetString();
            if (string.IsNullOrWhiteSpace(fileId))
            {
                return null;
            }

            var fileInfo = await GetFileAsync(fileId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(fileInfo.FilePath) ||
                fileInfo.FilePath.Contains("..", StringComparison.Ordinal) ||
                fileInfo.FilePath.Contains('\\'))
            {
                return null;
            }

            using var ms = new MemoryStream();
            await DownloadAsync(fileInfo.FilePath, ms, maxBytes: 2 * 1024 * 1024, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> GetBotPhotoBytesAsync(string? username, long botId, CancellationToken ct)
    {
        try
        {
            string? fileId = null;

            // 1. Try getChat with @username if available, or botId
            string target = !string.IsNullOrWhiteSpace(username)
                ? $"@{username.TrimStart('@')}"
                : (botId > 0 ? botId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

            if (!string.IsNullOrEmpty(target))
            {
                var url = $"{_apiRoot}getChat?chat_id={Uri.EscapeDataString(target)}";
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("photo", out var photo))
                {
                    if (photo.TryGetProperty("small_file_id", out var smallProp) &&
                        smallProp.ValueKind == JsonValueKind.String)
                    {
                        fileId = smallProp.GetString();
                    }
                    else if (photo.TryGetProperty("big_file_id", out var bigProp) &&
                             bigProp.ValueKind == JsonValueKind.String)
                    {
                        fileId = bigProp.GetString();
                    }
                }
            }

            // 2. Fallback to getUserProfilePhotos if getChat returned no photo
            if (string.IsNullOrWhiteSpace(fileId) && botId > 0)
            {
                var photosUrl = $"{_apiRoot}getUserProfilePhotos?user_id={botId}&limit=1";
                using var photosDoc = await GetJsonAsync(photosUrl, ct).ConfigureAwait(false);
                if (photosDoc.RootElement.TryGetProperty("result", out var pResult) &&
                    pResult.TryGetProperty("photos", out var photos) &&
                    photos.ValueKind == JsonValueKind.Array &&
                    photos.GetArrayLength() > 0)
                {
                    var firstSizes = photos[0];
                    if (firstSizes.ValueKind == JsonValueKind.Array && firstSizes.GetArrayLength() > 0)
                    {
                        var firstSize = firstSizes[0];
                        if (firstSize.TryGetProperty("file_id", out var fProp) && fProp.ValueKind == JsonValueKind.String)
                        {
                            fileId = fProp.GetString();
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(fileId))
            {
                return null;
            }

            var fileInfo = await GetFileAsync(fileId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(fileInfo.FilePath) ||
                fileInfo.FilePath.Contains("..", StringComparison.Ordinal) ||
                fileInfo.FilePath.Contains('\\'))
            {
                return null;
            }

            using var ms = new MemoryStream();
            await DownloadAsync(fileInfo.FilePath, ms, maxBytes: 2 * 1024 * 1024, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
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

    private sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            _totalRead += read;
            if (_totalRead > maxBytes)
            {
                throw new FileTooLargeException(maxBytes);
            }
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _totalRead += read;
            if (_totalRead > maxBytes)
            {
                throw new FileTooLargeException(maxBytes);
            }
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            _totalRead += read;
            if (_totalRead > maxBytes)
            {
                throw new FileTooLargeException(maxBytes);
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
