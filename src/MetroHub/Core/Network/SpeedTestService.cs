using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Network;

public class SpeedTestService
{
    private static readonly HttpClient HttpClient;

    static SpeedTestService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 8,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(6)
        };

        HttpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MetroHub/1.0");
    }

    public static bool IsCurrentConnectionMetered()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return false;
            var cost = profile.GetConnectionCost();
            if (cost == null) return false;

            return cost.NetworkCostType != Windows.Networking.Connectivity.NetworkCostType.Unrestricted
                || cost.Roaming
                || cost.OverDataLimit;
        }
        catch
        {
            return false;
        }
    }

    public async Task RunTestAsync(IProgress<SpeedTestProgress> progress, CancellationToken cancellationToken)
    {
        double measuredPing = 0;
        double measuredJitter = 0;
        double finalDownloadMbps = 0;
        double finalUploadMbps = 0;

        try
        {
            // -------------------------------------------------------------
            // PRE-FLIGHT CHECK: Network Interface Available
            // -------------------------------------------------------------
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                progress.Report(new SpeedTestProgress
                {
                    Phase = SpeedTestPhase.Failed,
                    ErrorMessage = "No internet connection",
                    StatusMessage = "No internet connection"
                });
                return;
            }

            // -------------------------------------------------------------
            // PHASE 1: CONNECTING & PING / JITTER (5s duration, aborts at 2s if unreachable)
            // -------------------------------------------------------------
            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Connecting,
                StatusMessage = "Connecting to edge server..."
            });

            var pingTimes = new List<double>();
            double? lastProbeElapsed = null;
            double ongoingJitter = 0;
            bool hasJitter = false;
            const int latencyDurationMs = 5000;
            const int noInternetThresholdMs = 2000;
            long latencyStart = Stopwatch.GetTimestamp();
            int consecutiveFailures = 0;

            using var icmpPing = new Ping();
            const string icmpTarget = "1.1.1.1";

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long now = Stopwatch.GetTimestamp();
                double totalElapsedMs = (now - latencyStart) * 1000.0 / Stopwatch.Frequency;
                if (totalElapsedMs >= latencyDurationMs)
                {
                    break;
                }

                long probeStart = Stopwatch.GetTimestamp();
                double elapsedMs = -1;

                try
                {
                    // 1. Primary: True Layer-3 ICMP Ping directly to Cloudflare Anycast Edge (hardware wire-speed RTT)
                    var reply = await icmpPing.SendPingAsync(icmpTarget, 600).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        elapsedMs = reply.RoundtripTime > 0
                            ? (double)reply.RoundtripTime
                            : Math.Max(0.5, (Stopwatch.GetTimestamp() - probeStart) * 1000.0 / Stopwatch.Frequency);
                    }
                }
                catch { }

                // 2. Fallback: If ICMP is blocked by a strict firewall/proxy, fall back to HTTP endpoint
                if (elapsedMs < 0)
                {
                    try
                    {
                        using var response = await HttpClient.GetAsync(
                            "https://speed.cloudflare.com/__down?bytes=0",
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken).ConfigureAwait(false);

                        response.EnsureSuccessStatusCode();
                        elapsedMs = (Stopwatch.GetTimestamp() - probeStart) * 1000.0 / Stopwatch.Frequency;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        consecutiveFailures++;
                        lastProbeElapsed = null; // Do not calculate jitter across dropped probes
                    }
                }

                if (elapsedMs >= 0)
                {
                    pingTimes.Add(elapsedMs);
                    consecutiveFailures = 0;

                    // Calculate ongoing median ping
                    var sorted = pingTimes.OrderBy(p => p).ToList();
                    measuredPing = sorted[sorted.Count / 2];

                    // Calculate ongoing RFC 3550 jitter across consecutive successful probes
                    if (lastProbeElapsed.HasValue)
                    {
                        double diff = Math.Abs(elapsedMs - lastProbeElapsed.Value);
                        if (!hasJitter)
                        {
                            ongoingJitter = diff;
                            hasJitter = true;
                        }
                        else
                        {
                            ongoingJitter += (diff - ongoingJitter) / 16.0;
                        }
                        measuredJitter = ongoingJitter;
                    }
                    lastProbeElapsed = elapsedMs;
                }

                double currentElapsedMs = (Stopwatch.GetTimestamp() - latencyStart) * 1000.0 / Stopwatch.Frequency;

                // Early exit: if no probe succeeded after 2 seconds and multiple failures, fail fast
                if (pingTimes.Count == 0 && currentElapsedMs >= noInternetThresholdMs && consecutiveFailures >= 2)
                {
                    progress.Report(new SpeedTestProgress
                    {
                        Phase = SpeedTestPhase.Failed,
                        ErrorMessage = "No internet connection",
                        StatusMessage = "No internet connection"
                    });
                    return;
                }

                double phaseProg = Math.Clamp(currentElapsedMs / latencyDurationMs, 0.0, 1.0);

                progress.Report(new SpeedTestProgress
                {
                    Phase = SpeedTestPhase.Ping,
                    PingMs = Math.Round(measuredPing, 1),
                    JitterMs = Math.Round(measuredJitter, 1),
                    PhaseProgress = phaseProg,
                    StatusMessage = $"Measuring latency ({Math.Round(currentElapsedMs / 1000.0, 1)}s)..."
                });

                if (currentElapsedMs >= latencyDurationMs)
                {
                    break;
                }

                int remainingMs = (int)(latencyDurationMs - currentElapsedMs);
                int delayMs = Math.Min(90, Math.Max(10, remainingMs));
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            // If 0 probes succeeded in total, do not proceed to download/upload
            if (pingTimes.Count == 0)
            {
                progress.Report(new SpeedTestProgress
                {
                    Phase = SpeedTestPhase.Failed,
                    ErrorMessage = "No internet connection",
                    StatusMessage = "No internet connection"
                });
                return;
            }

            // -------------------------------------------------------------
            // PHASE 2: DOWNLOAD TEST (Multi-stream, scaled on metered networks)
            // -------------------------------------------------------------
            bool isMetered = IsCurrentConnectionMetered();
            int downloadStreams = isMetered ? 2 : 4;
            int testDurationMs = isMetered ? 5000 : 10000;
            string downloadStatusMsg = isMetered ? "Testing download (metered network)..." : "Testing download speed...";

            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Download,
                PingMs = measuredPing,
                JitterMs = measuredJitter,
                IsMeteredConnection = isMetered,
                StatusMessage = downloadStatusMsg
            });

            long totalBytesDownloaded = 0;
            double peakDownloadMbps = 0;
            double smoothedDownloadMbps = 0;

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(testDurationMs);

            long downloadStart = Stopwatch.GetTimestamp();
            long lastSampleTime = downloadStart;
            long lastSampleBytes = 0;

            int consecutiveDownloadErrors = 0;
            Exception? lastDownloadError = null;

            // Background worker tasks downloading chunks
            var downloadTasks = Enumerable.Range(0, downloadStreams).Select(async _ =>
            {
                var buffer = new byte[65536]; // 64KB buffer
                while (!downloadCts.IsCancellationRequested)
                {
                    try
                    {
                        using var res = await HttpClient.GetAsync(
                            "https://speed.cloudflare.com/__down?bytes=25000000",
                            HttpCompletionOption.ResponseHeadersRead,
                            downloadCts.Token).ConfigureAwait(false);

                        res.EnsureSuccessStatusCode();

                        using var stream = await res.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false);
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, downloadCts.Token).ConfigureAwait(false)) > 0)
                        {
                            Interlocked.Add(ref totalBytesDownloaded, read);
                            consecutiveDownloadErrors = 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastDownloadError = ex;
                        if (Interlocked.Increment(ref consecutiveDownloadErrors) >= downloadStreams * 3)
                        {
                            downloadCts.Cancel();
                            break;
                        }

                        if (downloadCts.IsCancellationRequested) break;
                        await Task.Delay(50, downloadCts.Token).ConfigureAwait(false);
                    }
                }
            }).ToList();

            // Progress sampling loop (every ~60ms with EMA smoothing)
            while (!downloadCts.IsCancellationRequested)
            {
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);

                long now = Stopwatch.GetTimestamp();
                double deltaSec = (now - lastSampleTime) / (double)Stopwatch.Frequency;
                if (deltaSec >= 0.04)
                {
                    long currentTotal = Interlocked.Read(ref totalBytesDownloaded);
                    long deltaBytes = currentTotal - lastSampleBytes;
                    lastSampleBytes = currentTotal;
                    lastSampleTime = now;

                    double currentMbps = (deltaBytes * 8.0) / (deltaSec * 1_000_000.0);
                    smoothedDownloadMbps = smoothedDownloadMbps <= 0.05 
                        ? currentMbps 
                        : (smoothedDownloadMbps * 0.70 + currentMbps * 0.30);

                    if (smoothedDownloadMbps > peakDownloadMbps) peakDownloadMbps = smoothedDownloadMbps;

                    double elapsedMs = (now - downloadStart) * 1000.0 / Stopwatch.Frequency;
                    double phaseProg = Math.Clamp(elapsedMs / testDurationMs, 0.0, 1.0);

                    progress.Report(new SpeedTestProgress
                    {
                        Phase = SpeedTestPhase.Download,
                        InstantaneousMbps = Math.Round(smoothedDownloadMbps, 1),
                        PeakMbps = Math.Round(peakDownloadMbps, 1),
                        PhaseProgress = phaseProg,
                        PingMs = measuredPing,
                        JitterMs = measuredJitter,
                        IsMeteredConnection = isMetered,
                        StatusMessage = downloadStatusMsg
                    });
                }
            }

            try { await Task.WhenAll(downloadTasks).ConfigureAwait(false); } catch { }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Interlocked.Read(ref totalBytesDownloaded) == 0 && lastDownloadError != null)
            {
                throw new HttpRequestException($"Download speed test failed: {lastDownloadError.Message}", lastDownloadError);
            }

            double totalDownloadSec = (Stopwatch.GetTimestamp() - downloadStart) / (double)Stopwatch.Frequency;
            if (totalDownloadSec > 0.5)
            {
                finalDownloadMbps = Math.Round((Interlocked.Read(ref totalBytesDownloaded) * 8.0) / (totalDownloadSec * 1_000_000.0), 1);
            }

            // -------------------------------------------------------------
            // PHASE 3: UPLOAD TEST (Multi-stream, scaled on metered networks)
            // -------------------------------------------------------------
            int uploadStreams = isMetered ? 2 : 3;
            int uploadDurationMs = isMetered ? 5000 : 10000;
            string uploadStatusMsg = isMetered ? "Testing upload (metered network)..." : "Testing upload speed...";

            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Upload,
                FinalDownloadMbps = finalDownloadMbps,
                PingMs = measuredPing,
                JitterMs = measuredJitter,
                IsMeteredConnection = isMetered,
                StatusMessage = uploadStatusMsg
            });

            long totalBytesUploaded = 0;
            double peakUploadMbps = 0;
            double smoothedUploadMbps = 0;

            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadCts.CancelAfter(uploadDurationMs);

            long uploadStart = Stopwatch.GetTimestamp();
            long lastUploadSampleTime = uploadStart;
            long lastUploadSampleBytes = 0;

            // 64KB pre-allocated chunk buffer
            var uploadChunk = new byte[65536];
            new Random(42).NextBytes(uploadChunk);

            int consecutiveUploadErrors = 0;
            long successfulUploadRequests = 0;
            Exception? lastUploadError = null;

            var uploadTasks = Enumerable.Range(0, uploadStreams).Select(async _ =>
            {
                while (!uploadCts.IsCancellationRequested)
                {
                    try
                    {
                        // 10MB streaming payload per request with real-time per-chunk byte reporting
                        using var content = new TrackedStreamUploadContent(
                            uploadChunk, 
                            10_000_000, 
                            bytesSent => 
                            {
                                Interlocked.Add(ref totalBytesUploaded, bytesSent);
                                consecutiveUploadErrors = 0;
                            }, 
                            uploadCts.Token);

                        using var res = await HttpClient.PostAsync(
                            "https://speed.cloudflare.com/__up",
                            content,
                            uploadCts.Token).ConfigureAwait(false);

                        res.EnsureSuccessStatusCode();
                        Interlocked.Increment(ref successfulUploadRequests);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastUploadError = ex;
                        if (Interlocked.Increment(ref consecutiveUploadErrors) >= uploadStreams * 3)
                        {
                            uploadCts.Cancel();
                            break;
                        }

                        if (uploadCts.IsCancellationRequested) break;
                        await Task.Delay(50, uploadCts.Token).ConfigureAwait(false);
                    }
                }
            }).ToList();

            // Real-time progress sampling loop (every ~60ms with EMA smoothing)
            while (!uploadCts.IsCancellationRequested)
            {
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);

                long now = Stopwatch.GetTimestamp();
                double deltaSec = (now - lastUploadSampleTime) / (double)Stopwatch.Frequency;
                if (deltaSec >= 0.04)
                {
                    long currentTotal = Interlocked.Read(ref totalBytesUploaded);
                    long deltaBytes = currentTotal - lastUploadSampleBytes;
                    lastUploadSampleBytes = currentTotal;
                    lastUploadSampleTime = now;

                    double currentMbps = (deltaBytes * 8.0) / (deltaSec * 1_000_000.0);
                    smoothedUploadMbps = smoothedUploadMbps <= 0.05 
                        ? currentMbps 
                        : (smoothedUploadMbps * 0.70 + currentMbps * 0.30);

                    if (smoothedUploadMbps > peakUploadMbps) peakUploadMbps = smoothedUploadMbps;

                    double elapsedMs = (now - uploadStart) * 1000.0 / Stopwatch.Frequency;
                    double phaseProg = Math.Clamp(elapsedMs / uploadDurationMs, 0.0, 1.0);

                    progress.Report(new SpeedTestProgress
                    {
                        Phase = SpeedTestPhase.Upload,
                        InstantaneousMbps = Math.Round(smoothedUploadMbps, 1),
                        PeakMbps = Math.Round(peakUploadMbps, 1),
                        PhaseProgress = phaseProg,
                        FinalDownloadMbps = finalDownloadMbps,
                        PingMs = measuredPing,
                        JitterMs = measuredJitter,
                        IsMeteredConnection = isMetered,
                        StatusMessage = uploadStatusMsg
                    });
                }
            }

            try { await Task.WhenAll(uploadTasks).ConfigureAwait(false); } catch { }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Interlocked.Read(ref successfulUploadRequests) == 0 && lastUploadError != null)
            {
                throw new HttpRequestException($"Upload speed test failed: {lastUploadError.Message}", lastUploadError);
            }

            double totalUploadSec = (Stopwatch.GetTimestamp() - uploadStart) / (double)Stopwatch.Frequency;
            if (totalUploadSec > 0.5)
            {
                finalUploadMbps = Math.Round((Interlocked.Read(ref totalBytesUploaded) * 8.0) / (totalUploadSec * 1_000_000.0), 1);
            }

            // Allow the gauge to settle on the final reading before smoothly transitioning back to GO
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            // -------------------------------------------------------------
            // PHASE 4: COMPLETED
            // -------------------------------------------------------------
            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Completed,
                FinalDownloadMbps = finalDownloadMbps,
                FinalUploadMbps = finalUploadMbps,
                PingMs = measuredPing,
                JitterMs = measuredJitter,
                PhaseProgress = 1.0,
                IsMeteredConnection = isMetered,
                StatusMessage = isMetered ? "Test completed (metered network)" : "Test completed"
            });
        }
        catch (OperationCanceledException)
        {
            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Cancelled,
                StatusMessage = "Speed test cancelled"
            });
        }
        catch (Exception ex)
        {
            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Failed,
                ErrorMessage = ex.Message,
                StatusMessage = "Test failed: " + ex.Message
            });
        }
    }
}

/// <summary>
/// HttpContent that streams data in chunks, invoking a callback as bytes are written to the transport stream.
/// Ensures live, continuous accounting of uploaded data without waiting for the full POST response.
/// </summary>
internal sealed class TrackedStreamUploadContent : HttpContent
{
    private readonly byte[] _chunk;
    private readonly long _totalLength;
    private readonly Action<int> _onBytesSent;
    private readonly CancellationToken _ct;

    public TrackedStreamUploadContent(byte[] chunk, long totalLength, Action<int> onBytesSent, CancellationToken ct)
    {
        _chunk = chunk;
        _totalLength = totalLength;
        _onBytesSent = onBytesSent;
        _ct = ct;
        Headers.ContentLength = totalLength;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        long remaining = _totalLength;
        while (remaining > 0 && !_ct.IsCancellationRequested)
        {
            int toSend = (int)Math.Min(_chunk.Length, remaining);
            await stream.WriteAsync(_chunk.AsMemory(0, toSend), _ct).ConfigureAwait(false);
            _onBytesSent(toSend);
            remaining -= toSend;
        }
        await stream.FlushAsync(_ct).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _totalLength;
        return true;
    }
}
