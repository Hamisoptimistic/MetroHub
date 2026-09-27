using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Radio;

/// <summary>
/// Streams one resolved YouTube audio URL into a <see cref="ChunkedAudioBuffer"/> using short
/// HTTP <c>Range</c> requests.
/// </summary>
/// <remarks>
/// <para>
/// YouTube throttles a long-lived googlevideo download: the first seconds are fast, then the
/// connection is capped (your logs show ~12 KB/s after a 27-45 KB/s burst on every reconnect).
/// Requesting a fresh short range restarts that fast window, so playback stays several times
/// ahead of realtime.
/// </para>
/// <para>
/// If a chunk's live rate collapses mid-transfer, the downloader abandons the range and opens a
/// fresh one from the last received byte — the throttle window resets, and we lose nothing.
/// </para>
/// </remarks>
public sealed class YoutubeChunkedDownloader : IDisposable
{
    public const int MinChunkBytes = 512 * 1024;
    public const int MaxChunkBytes = 2 * 1024 * 1024;
    private const int InitialChunkBytes = 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;
    private const int ThrottleProbeMilliseconds = 2500;
    private const double ThrottledBytesPerSecond = 48 * 1024;
    private const int MaxConsecutiveFailures = 5;

    /// <summary>Chrome-like UA; googlevideo rejects some default agent strings.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly HttpClient _client;
    private readonly ChunkedAudioBuffer _buffer;
    private readonly string _url;
    private readonly string _label;

    public YoutubeChunkedDownloader(
        ChunkedAudioBuffer buffer,
        string url,
        string label,
        HttpClient? client = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _label = label ?? string.Empty;
        _client = client ?? SharedClient;
    }

    /// <summary>Runs until the whole track is downloaded, the token fires, or a fatal error occurs.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        long position = 0;
        long totalLength = -1;
        int chunkBytes = InitialChunkBytes;
        int consecutiveFailures = 0;

        try
        {
            while (!ct.IsCancellationRequested && (totalLength < 0 || position < totalLength))
            {
                long end = position + chunkBytes - 1;
                if (totalLength >= 0)
                {
                    end = Math.Min(end, totalLength - 1);
                }

                using HttpResponseMessage? response = await SendRangeAsync(position, end, ct).ConfigureAwait(false);
                if (response is null)
                {
                    if (++consecutiveFailures > MaxConsecutiveFailures)
                    {
                        throw new IOException($"range request failed {consecutiveFailures} times in a row");
                    }
                    await Task.Delay(400 * consecutiveFailures, ct).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    totalLength = position; // asked past the end: we are done
                    break;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}");
                }

                if (response.Content.Headers.ContentRange?.Length is long declaredLength && declaredLength > 0)
                {
                    totalLength = declaredLength;
                }

                (long bytesRead, bool reopened, double bytesPerSecond) =
                    await PumpBodyAsync(response, ct).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    if (totalLength >= 0 && position < totalLength)
                    {
                        throw new IOException($"server returned no data at byte {position} of {totalLength}");
                    }
                    break; // unknown length and no data => end of stream
                }

                position += bytesRead;
                consecutiveFailures = 0;
                chunkBytes = AdaptChunkSize(bytesPerSecond);

                JukeboxLog.Info(
                    $"Chunk {position / 1024} KB @ {bytesPerSecond / 1024:F0} KB/s (chunk {chunkBytes / 1024} KB{(reopened ? ", re-opened" : string.Empty)}) for '{_label}'.");
            }

            _buffer.Complete();
            JukeboxLog.Info($"Download complete for '{_label}': {_buffer.BufferedBytes / 1024} KB buffered.");
        }
        catch (OperationCanceledException)
        {
            // Track switched or widget closed: stop silently.
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Download failed for '{_label}': {ex.Message}");
            _buffer.CompleteFaulted(ex);
        }
    }

    private async Task<(long BytesRead, bool Reopened, double BytesPerSecond)> PumpBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] tmp = new byte[ReadBufferBytes];
        long read = 0;
        long windowBytes = 0;
        long windowStartMs = 0;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            int n = await body.ReadAsync(tmp.AsMemory(0, tmp.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;

            _buffer.Append(tmp, 0, n);
            read += n;
            windowBytes += n;

            // Rolling window: a fresh connection is fast for a moment, then YouTube throttles it.
            // When the live rate collapses we abandon the range and restart it from the last byte.
            long elapsedMs = stopwatch.ElapsedMilliseconds;
            long windowMs = elapsedMs - windowStartMs;
            if (windowMs >= ThrottleProbeMilliseconds)
            {
                double windowRate = windowBytes / (windowMs / 1000.0);
                windowStartMs = elapsedMs;
                windowBytes = 0;

                if (windowRate < ThrottledBytesPerSecond)
                {
                    JukeboxLog.Info(
                        $"Throttled at {windowRate / 1024:F1} KB/s for '{_label}' — re-opening a fresh range to reset the window.");
                    return (read, true, windowRate);
                }
            }
        }

        double elapsed = stopwatch.Elapsed.TotalSeconds;
        double rate = elapsed > 0 ? read / elapsed : 0;
        return (read, false, rate);
    }

    private async Task<HttpResponseMessage?> SendRangeAsync(long start, long end, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Range = new RangeHeaderValue(start, end);
            return await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Range {start}-{end} request failed for '{_label}': {ex.Message}");
            return null;
        }
    }

    private static int AdaptChunkSize(double bytesPerSecond)
    {
        if (bytesPerSecond >= 512 * 1024) return MaxChunkBytes;
        if (bytesPerSecond <= 128 * 1024) return MinChunkBytes;
        return InitialChunkBytes;
    }

    public void Dispose()
    {
        // The downloader holds no unmanaged state of its own: cancellation flows through the
        // token, and the read loop releases the HTTP response via its using scope.
    }
}
