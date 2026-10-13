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
    /// </summary>
    public static async Task<bool> DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken ct = default,
        long minimumBytes = 0)
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
                await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            var fi = new FileInfo(tempFile);
            if (minimumBytes > 0 && (!fi.Exists || fi.Length < minimumBytes))
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
}
