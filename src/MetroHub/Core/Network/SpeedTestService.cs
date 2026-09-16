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

public sealed class SpeedTestService
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

        var latencyTracker = new LatencyTracker();
        using var latencyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Background task running continuous ICMP (with HTTP fallback) latency probes
        // simultaneously throughout Phase 1, Download, and Upload
        var pingTask = Task.Run(async () =>
        {
            const string icmpTarget = "1.1.1.1";

            while (!latencyCts.IsCancellationRequested)
            {
                long probeStart = Stopwatch.GetTimestamp();
                double elapsedMs = -1;

                try
                {
                    using var icmpPing = new Ping();
                    var reply = await icmpPing.SendPingAsync(icmpTarget, 600).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        elapsedMs = reply.RoundtripTime > 0
                            ? (double)reply.RoundtripTime
                            : Math.Max(0.5, (Stopwatch.GetTimestamp() - probeStart) * 1000.0 / Stopwatch.Frequency);
                    }
                }
                catch { }

                if (elapsedMs < 0)
                {
                    try
                    {
                        using var response = await HttpClient.GetAsync(
                            "https://speed.cloudflare.com/__down?bytes=0",
                            HttpCompletionOption.ResponseHeadersRead,
                            latencyCts.Token).ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            elapsedMs = (Stopwatch.GetTimestamp() - probeStart) * 1000.0 / Stopwatch.Frequency;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch { }
                }

                if (elapsedMs >= 0)
                {
                    latencyTracker.RecordProbe(elapsedMs);
                }

                try
                {
                    await Task.Delay(180, latencyCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cancellationToken);

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
            // PHASE 1: CONNECTING & INITIAL BASELINE (Runs ~1.5s to establish edge link)
            // -------------------------------------------------------------
            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Connecting,
                StatusMessage = "Connecting to edge server..."
            });

            const int initialPhaseDurationMs = 1500;
            const int noInternetThresholdMs = 2500;
            long latencyStart = Stopwatch.GetTimestamp();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long now = Stopwatch.GetTimestamp();
                double totalElapsedMs = (now - latencyStart) * 1000.0 / Stopwatch.Frequency;

                // Move directly to download once baseline latency is confirmed
                if (totalElapsedMs >= initialPhaseDurationMs && latencyTracker.ProbeCount >= 3)
                {
                    break;
                }

                // If no probe succeeds within threshold, abort early
                if (latencyTracker.ProbeCount == 0 && totalElapsedMs >= noInternetThresholdMs)
                {
                    progress.Report(new SpeedTestProgress
                    {
                        Phase = SpeedTestPhase.Failed,
                        ErrorMessage = "No internet connection",
                        StatusMessage = "No internet connection"
                    });
                    return;
                }

                var (liveP, liveJ) = latencyTracker.GetLiveMetrics();
                if (liveP.HasValue) measuredPing = liveP.Value;
                if (liveJ.HasValue) measuredJitter = liveJ.Value;

                double phaseProg = Math.Clamp(totalElapsedMs / initialPhaseDurationMs, 0.0, 1.0);

                progress.Report(new SpeedTestProgress
                {
                    Phase = SpeedTestPhase.Ping,
                    PingMs = liveP,
                    JitterMs = liveJ,
                    PhaseProgress = phaseProg,
                    StatusMessage = "Measuring latency..."
                });

                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }

            // If 0 probes succeeded in total, do not proceed to download/upload
            if (latencyTracker.ProbeCount == 0)
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
            double downloadWarmUpSec = isMetered ? 1.0 : 1.5;
            long warmUpBytes = 0;
            long warmUpTimestamp = 0;
            bool warmUpRecorded = false;

            int consecutiveDownloadErrors = 0;
            Exception? lastDownloadError = null;

            // Rolling queue for stable, silky-smooth 400ms sliding-window live gauge rate
            var downloadRollingSamples = new Queue<(long Time, long Bytes)>();

            // Background worker tasks downloading chunks
            var downloadTasks = Enumerable.Range(0, downloadStreams).Select(async _ =>
            {
                var buffer = new byte[65536]; // 64KB buffer
                while (!downloadCts.IsCancellationRequested)
                {
                    try
                    {
                        using var res = await HttpClient.GetAsync(
                            "https://speed.cloudflare.com/__down?bytes=50000000",
                            HttpCompletionOption.ResponseHeadersRead,
                            downloadCts.Token).ConfigureAwait(false);

                        if (res.StatusCode == (System.Net.HttpStatusCode)429)
                        {
                            lastDownloadError = new HttpRequestException("429 (Too Many Requests)");
                            await Task.Delay(800, downloadCts.Token).ConfigureAwait(false);
                            continue;
                        }

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
                        await Task.Delay(100, downloadCts.Token).ConfigureAwait(false);
                    }
                }
            }).ToList();

            // Progress sampling loop (every ~50ms with 400ms sliding window)
            while (!downloadCts.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                long now = Stopwatch.GetTimestamp();
                long currentTotal = Interlocked.Read(ref totalBytesDownloaded);
                double elapsedSec = (now - downloadStart) / (double)Stopwatch.Frequency;

                // Mark the end of TCP slow-start warm-up to isolate true sustained throughput
                if (elapsedSec >= downloadWarmUpSec && !warmUpRecorded)
                {
                    warmUpBytes = currentTotal;
                    warmUpTimestamp = now;
                    warmUpRecorded = true;
                }

                // Smooth sliding-window throughput over 400ms
                downloadRollingSamples.Enqueue((now, currentTotal));
                while (downloadRollingSamples.Count > 1 && (now - downloadRollingSamples.Peek().Time) > Stopwatch.Frequency * 0.40)
                {
                    downloadRollingSamples.Dequeue();
                }

                var oldest = downloadRollingSamples.Peek();
                double windowDt = (now - oldest.Time) / (double)Stopwatch.Frequency;
                double liveMbps = windowDt > 0.08 ? ((currentTotal - oldest.Bytes) * 8.0) / (windowDt * 1_000_000.0) : 0.0;

                smoothedDownloadMbps = smoothedDownloadMbps <= 0.1
                    ? liveMbps
                    : (smoothedDownloadMbps * 0.80 + liveMbps * 0.20);

                if (smoothedDownloadMbps > peakDownloadMbps) peakDownloadMbps = smoothedDownloadMbps;

                double elapsedMs = elapsedSec * 1000.0;
                double phaseProg = Math.Clamp(elapsedMs / testDurationMs, 0.0, 1.0);

                var (liveP, liveJ) = latencyTracker.GetLiveMetrics();
                if (liveP.HasValue) measuredPing = liveP.Value;
                if (liveJ.HasValue) measuredJitter = liveJ.Value;

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

            try { await Task.WhenAll(downloadTasks).ConfigureAwait(false); } catch { }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Interlocked.Read(ref totalBytesDownloaded) == 0 && lastDownloadError != null)
            {
                throw lastDownloadError;
            }

            double totalDownloadSec = (Stopwatch.GetTimestamp() - downloadStart) / (double)Stopwatch.Frequency;
            double rawAverageDownload = totalDownloadSec > 0.5 
                ? (Interlocked.Read(ref totalBytesDownloaded) * 8.0) / (totalDownloadSec * 1_000_000.0) 
                : 0.0;

            // Compute true sustained average (discarding the 1.5s TCP slow-start warm-up)
            if (warmUpRecorded && warmUpTimestamp > 0)
            {
                long sustainedBytes = Interlocked.Read(ref totalBytesDownloaded) - warmUpBytes;
                double sustainedSec = (Stopwatch.GetTimestamp() - warmUpTimestamp) / (double)Stopwatch.Frequency;
                if (sustainedSec > 0.5 && sustainedBytes > 0)
                {
                    finalDownloadMbps = Math.Round((sustainedBytes * 8.0) / (sustainedSec * 1_000_000.0), 1);
                }
                else
                {
                    finalDownloadMbps = Math.Round(rawAverageDownload, 1);
                }
            }
            else
            {
                finalDownloadMbps = Math.Round(rawAverageDownload, 1);
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
            double uploadWarmUpSec = isMetered ? 1.0 : 1.5;
            long uploadWarmUpBytes = 0;
            long uploadWarmUpTimestamp = 0;
            bool uploadWarmUpRecorded = false;

            // Rolling queue for stable, silky-smooth 400ms sliding-window live gauge rate
            var uploadRollingSamples = new Queue<(long Time, long Bytes)>();

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

                        if (res.StatusCode == (System.Net.HttpStatusCode)429)
                        {
                            lastUploadError = new HttpRequestException("429 (Too Many Requests)");
                            await Task.Delay(800, uploadCts.Token).ConfigureAwait(false);
                            continue;
                        }

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

            // Real-time progress sampling loop (every ~50ms with 400ms sliding window)
            while (!uploadCts.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                long now = Stopwatch.GetTimestamp();
                long currentTotal = Interlocked.Read(ref totalBytesUploaded);
                double elapsedSec = (now - uploadStart) / (double)Stopwatch.Frequency;

                // Mark the end of TCP slow-start warm-up to isolate true sustained throughput
                if (elapsedSec >= uploadWarmUpSec && !uploadWarmUpRecorded)
                {
                    uploadWarmUpBytes = currentTotal;
                    uploadWarmUpTimestamp = now;
                    uploadWarmUpRecorded = true;
                }

                // Smooth sliding-window throughput over 400ms
                uploadRollingSamples.Enqueue((now, currentTotal));
                while (uploadRollingSamples.Count > 1 && (now - uploadRollingSamples.Peek().Time) > Stopwatch.Frequency * 0.40)
                {
                    uploadRollingSamples.Dequeue();
                }

                var oldest = uploadRollingSamples.Peek();
                double windowDt = (now - oldest.Time) / (double)Stopwatch.Frequency;
                double liveMbps = windowDt > 0.08 ? ((currentTotal - oldest.Bytes) * 8.0) / (windowDt * 1_000_000.0) : 0.0;

                smoothedUploadMbps = smoothedUploadMbps <= 0.1
                    ? liveMbps
                    : (smoothedUploadMbps * 0.80 + liveMbps * 0.20);

                if (smoothedUploadMbps > peakUploadMbps) peakUploadMbps = smoothedUploadMbps;

                double elapsedMs = elapsedSec * 1000.0;
                double phaseProg = Math.Clamp(elapsedMs / uploadDurationMs, 0.0, 1.0);

                var (liveP, liveJ) = latencyTracker.GetLiveMetrics();
                if (liveP.HasValue) measuredPing = liveP.Value;
                if (liveJ.HasValue) measuredJitter = liveJ.Value;

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

            try { await Task.WhenAll(uploadTasks).ConfigureAwait(false); } catch { }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Interlocked.Read(ref successfulUploadRequests) == 0 && lastUploadError != null)
            {
                throw lastUploadError;
            }

            double totalUploadSec = (Stopwatch.GetTimestamp() - uploadStart) / (double)Stopwatch.Frequency;
            double rawAverageUpload = totalUploadSec > 0.5 
                ? (Interlocked.Read(ref totalBytesUploaded) * 8.0) / (totalUploadSec * 1_000_000.0) 
                : 0.0;

            // Compute true sustained average (discarding the 1.5s TCP slow-start warm-up)
            if (uploadWarmUpRecorded && uploadWarmUpTimestamp > 0)
            {
                long sustainedUploadBytes = Interlocked.Read(ref totalBytesUploaded) - uploadWarmUpBytes;
                double sustainedUploadSec = (Stopwatch.GetTimestamp() - uploadWarmUpTimestamp) / (double)Stopwatch.Frequency;
                if (sustainedUploadSec > 0.5 && sustainedUploadBytes > 0)
                {
                    finalUploadMbps = Math.Round((sustainedUploadBytes * 8.0) / (sustainedUploadSec * 1_000_000.0), 1);
                }
                else
                {
                    finalUploadMbps = Math.Round(rawAverageUpload, 1);
                }
            }
            else
            {
                finalUploadMbps = Math.Round(rawAverageUpload, 1);
            }

            // Stop continuous background latency probing and await task completion
            latencyCts.Cancel();
            try { await pingTask.ConfigureAwait(false); } catch { }

            // Compute representative consolidated session metrics across all probes (median to eliminate tail spikes)
            var (finalPing, finalJitter) = latencyTracker.GetConsolidatedMetrics();
            if (finalPing.HasValue) measuredPing = finalPing.Value;
            if (finalJitter.HasValue) measuredJitter = finalJitter.Value;

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
            string friendlyMsg = GetFriendlyErrorMessage(ex);
            progress.Report(new SpeedTestProgress
            {
                Phase = SpeedTestPhase.Failed,
                ErrorMessage = friendlyMsg,
                StatusMessage = friendlyMsg
            });
        }
        finally
        {
            latencyCts.Cancel();
            try { await pingTask.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Translates raw network/HTTP exceptions into short, relevant, user-friendly messages.
    /// </summary>
    public static string GetFriendlyErrorMessage(Exception? ex)
    {
        if (ex == null) return "Speed test failed. Try again.";

        string msg = ex.Message ?? "";
        if (ex.InnerException != null)
        {
            msg += " " + ex.InnerException.Message;
        }

        if (msg.Contains("429") || msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
            return "Server busy. Try again in a moment.";
        }

        if (msg.Contains("No internet", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase))
        {
            return "No internet connection";
        }

        if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out";
        }

        if (msg.Contains("500") || msg.Contains("502") || msg.Contains("503") || msg.Contains("504"))
        {
            return "Server error. Try again shortly.";
        }

        if (msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection refused. Try again.";
        }

        return "Speed test failed. Try again.";
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

/// <summary>
/// Thread-safe tracker for latency probes running concurrently throughout all test phases.
/// Provides real-time responsive metrics for live UI display and consolidated median metrics
/// across the entire session to prevent tail-spike distortion.
/// </summary>
internal sealed class LatencyTracker
{
    private readonly object _lock = new();
    private readonly List<double> _allProbes = new(256);
    private readonly List<double> _jitterDeltas = new(256);
    private double? _lastProbe;

    public int ProbeCount
    {
        get
        {
            lock (_lock) return _allProbes.Count;
        }
    }

    public void RecordProbe(double rttMs)
    {
        if (rttMs < 0 || double.IsNaN(rttMs) || double.IsInfinity(rttMs)) return;

        lock (_lock)
        {
            _allProbes.Add(rttMs);
            if (_lastProbe.HasValue)
            {
                double delta = Math.Abs(rttMs - _lastProbe.Value);
                _jitterDeltas.Add(delta);
            }
            _lastProbe = rttMs;
        }
    }

    /// <summary>
    /// Computes live responsive metrics from the most recent probes (sliding window of up to 6 samples)
    /// for real-time display during test execution.
    /// </summary>
    public (double? LivePing, double? LiveJitter) GetLiveMetrics()
    {
        lock (_lock)
        {
            if (_allProbes.Count == 0) return (null, null);

            int count = _allProbes.Count;
            int windowSize = Math.Min(count, 6);
            var recentSamples = new double[windowSize];
            for (int i = 0; i < windowSize; i++)
            {
                recentSamples[i] = _allProbes[count - windowSize + i];
            }

            double livePing = ComputeMedian(recentSamples);

            double? liveJitter = null;
            if (_jitterDeltas.Count > 0)
            {
                int jCount = _jitterDeltas.Count;
                int jWindow = Math.Min(jCount, 6);
                var recentDeltas = new double[jWindow];
                for (int i = 0; i < jWindow; i++)
                {
                    recentDeltas[i] = _jitterDeltas[jCount - jWindow + i];
                }
                liveJitter = ComputeMedian(recentDeltas);
            }

            return (Math.Round(livePing, 1), liveJitter.HasValue ? Math.Round(liveJitter.Value, 1) : null);
        }
    }

    /// <summary>
    /// Computes overall consolidated session metrics across all probes recorded
    /// during baseline, download, and upload phases.
    /// Uses median RTT and median jitter delta to eliminate single-packet fluke distortion.
    /// </summary>
    public (double? FinalPing, double? FinalJitter) GetConsolidatedMetrics()
    {
        lock (_lock)
        {
            if (_allProbes.Count == 0) return (null, null);

            double medianPing = ComputeMedian(_allProbes.ToArray());

            double? medianJitter = null;
            if (_jitterDeltas.Count > 0)
            {
                medianJitter = ComputeMedian(_jitterDeltas.ToArray());
            }

            return (Math.Round(medianPing, 1), medianJitter.HasValue ? Math.Round(medianJitter.Value, 1) : null);
        }
    }

    private static double ComputeMedian(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        int mid = values.Length / 2;
        if (values.Length % 2 != 0)
        {
            return values[mid];
        }
        return (values[mid - 1] + values[mid]) / 2.0;
    }
}
