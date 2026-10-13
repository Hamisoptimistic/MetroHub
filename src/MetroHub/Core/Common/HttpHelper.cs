using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized, high-performance, Native-AOT compliant HTTP helper for MetroHub.
/// Backed by a shared SocketsHttpHandler with connection pooling to prevent socket exhaustion.
/// </summary>
public static class HttpHelper
{
    private static readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.All
    };

    private static readonly HttpClient _defaultClient = new(_handler)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static HttpClient? _customClient;

    /// <summary>
    /// Global shared HttpClient instance configured with pooled connections and standard desktop headers.
    /// </summary>
    public static HttpClient Client => _customClient ?? _defaultClient;

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static HttpHelper()
    {
        try
        {
            _defaultClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 MetroHub/2.0");
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "Failed to initialize HttpHelper default User-Agent header");
        }
    }

    internal static IDisposable UseTestClient(HttpClient testClient)
    {
        ArgumentNullException.ThrowIfNull(testClient);
        var scope = new TestClientScope(_customClient);
        _customClient = testClient;
        return scope;
    }

    private sealed class TestClientScope : IDisposable
    {
        private readonly HttpClient? _previous;

        public TestClientScope(HttpClient? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            _customClient = _previous;
        }
    }

    /// <summary>
    /// Asynchronously fetches and deserializes JSON from the specified URL using Native AOT trim-safe source-generated metadata.
    /// </summary>
    public static async Task<T?> GetJsonAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"[HttpHelper] Failed to fetch or deserialize JSON from '{url}'");
            return default;
        }
    }

    /// <summary>
    /// Asynchronously fetches and deserializes JSON from the specified URL using reflection-based serializer options.
    /// Note: Prefer the JsonTypeInfo overload for Native AOT trim safety.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that accepts JsonTypeInfo for Native AOT trim safety.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require runtime code generation. Use the overload that accepts JsonTypeInfo for Native AOT trim safety.")]
    public static async Task<T?> GetJsonAsync<T>(
        string url,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, options ?? DefaultJsonOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"[HttpHelper] Failed to fetch or deserialize JSON from '{url}'");
            return default;
        }
    }

    /// <summary>
    /// Asynchronously retrieves the byte array content from the specified URL.
    /// </summary>
    public static async Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return await Client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously retrieves the response stream from the specified URL.
    /// </summary>
    public static async Task<Stream> GetStreamAsync(string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return await Client.GetStreamAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously downloads a remote file directly to disk atomically with temporary staging.
    /// Ensures that partially downloaded or corrupt files are cleaned up and not committed to destination.
    /// Supports streaming maximumBytes cutoff and magic bitmap header validation.
    /// </summary>
    public static async Task<bool> DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken ct = default,
        long minimumBytes = 0,
        long maximumBytes = 0,
        bool requireValidBitmap = false)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        string? tempFile = null;
        try
        {
            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            tempFile = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using (var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (maximumBytes > 0)
                {
                    byte[] buffer = new byte[8192];
                    long totalRead = 0;
                    int read;
                    while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > maximumBytes)
                        {
                            return false;
                        }
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                }
            }

            var fi = new FileInfo(tempFile);
            if (minimumBytes > 0 && (!fi.Exists || fi.Length < minimumBytes))
            {
                return false;
            }

            if (requireValidBitmap && !IsValidBitmapFile(tempFile))
            {
                return false;
            }

            File.Move(tempFile, destinationPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"[HttpHelper] Failed download from '{url}' to '{destinationPath}'");
            return false;
        }
        finally
        {
            if (tempFile != null && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    /// <summary>
    /// Inspects the header magic bytes of a file to confirm it is a valid bitmap (PNG, ICO, JPG, GIF, WEBP).
    /// Prevents saving corrupted downloads, empty files, or HTML error pages.
    /// </summary>
    public static bool IsValidBitmapFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 4) return false;

            Span<byte> header = stackalloc byte[12];
            int read = fs.Read(header);
            if (read < 4) return false;

            // PNG: 89 50 4E 47
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return true;

            // ICO: 00 00 01 00
            if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
                return true;

            // JPG: FF D8 FF
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return true;

            // GIF: 47 49 46 38 ("GIF8")
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
                return true;

            // WEBP: "RIFF" .... "WEBP"
            if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }
}
