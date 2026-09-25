using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MetroHub.Core.Media;

namespace MetroHub.Core.Services;

/// <summary>
/// Writes a JSONL trace of every media snapshot the seekbar consumed, next to the produced position.
/// </summary>
/// <remarks>
/// Disabled by default: this is a diagnostic capture tool, not a running log. Enable it, reproduce the
/// glitch, then drop the file into <c>tests/MetroHub.Tests/Traces</c> and replay it through
/// <see cref="MediaPlaybackClock"/> to pin the behaviour permanently. Follows the same contract as
/// <see cref="HiddenDiagnosticsLogger"/>: buffered, lock-guarded, and it must never throw.
/// </remarks>
public static class MediaSyncTraceLogger
{
    public static bool IsEnabled { get; set; }

    private static string _traceFilePath = @"d:\MetroHub\media_sync_trace.jsonl";
    private static readonly object _lock = new();
    private static readonly List<MediaSyncTraceEntry> _pending = new();
    private static long _traceStartTicks;
    private static long _lastFlushTicks;

    /// <summary>Entries are flushed in batches so a live capture never blocks the render loop.</summary>
    private const double FlushIntervalSeconds = 5.0;

    static MediaSyncTraceLogger()
    {
        try
        {
            string dir = Path.GetDirectoryName(_traceFilePath) ?? string.Empty;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                _traceFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "media_sync_trace.jsonl");
            }
        }
        catch
        {
            _traceFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "media_sync_trace.jsonl");
        }
    }

    /// <summary>Starts a new trace, discarding anything buffered from a previous capture.</summary>
    public static void StartTrace()
    {
        lock (_lock)
        {
            _pending.Clear();
            _traceStartTicks = Stopwatch.GetTimestamp();
            _lastFlushTicks = _traceStartTicks;
        }

        IsEnabled = true;
    }

    /// <summary>Flushes the remaining buffer and stops capturing.</summary>
    public static void StopTrace()
    {
        IsEnabled = false;
        Flush();
    }

    /// <summary>Records one snapshot alongside the position the clock produced from it.</summary>
    public static void Record(in MediaObservation observation, in MediaPlaybackSample sample)
    {
        if (!IsEnabled) return;

        try
        {
            double observedMs = (double)(observation.ObservedAtTicks - _traceStartTicks) / Stopwatch.Frequency * 1000.0;
            string? osTimestamp = observation.OsTimestamp > DateTimeOffset.MinValue
                ? observation.OsTimestamp.ToUniversalTime().ToString("O")
                : null;

            var entry = new MediaSyncTraceEntry
            {
                ObservedMs = Math.Round(observedMs, 1),
                TrackId = observation.TrackId,
                SourcePositionSeconds = Math.Round(observation.Position.TotalSeconds, 3),
                SourceDurationSeconds = Math.Round(observation.Duration.TotalSeconds, 3),
                Rate = observation.Rate,
                IsPlaying = observation.IsPlaying,
                CanSeek = observation.CanSeek,
                IsKnownNonLiveSource = observation.IsKnownNonLiveSource,
                HasLiveTitleKeyword = observation.HasLiveTitleKeyword,
                OsTimestampUtc = osTimestamp,
                DisplayedPositionSeconds = Math.Round(sample.Position.TotalSeconds, 3),
                DisplayedIsLive = sample.IsLive,
                DisplayedIsStalled = sample.IsStalled,
                DisplayedIsSeekPending = sample.IsSeekPending
            };

            bool shouldFlush;
            lock (_lock)
            {
                _pending.Add(entry);
                double sinceFlush = (double)(Stopwatch.GetTimestamp() - _lastFlushTicks) / Stopwatch.Frequency;
                shouldFlush = _pending.Count >= 512 || sinceFlush >= FlushIntervalSeconds;
                if (shouldFlush)
                {
                    _lastFlushTicks = Stopwatch.GetTimestamp();
                }
            }

            if (shouldFlush)
            {
                Flush();
            }
        }
        catch
        {
            // Diagnostics must never fail, and never take the seekbar down with them.
        }
    }

    /// <summary>Appends everything buffered to the trace file.</summary>
    public static void Flush()
    {
        try
        {
            MediaSyncTraceEntry[] batch;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            var builder = new System.Text.StringBuilder();
            foreach (var entry in batch)
            {
                builder.Append(JsonSerializer.Serialize(entry)).Append('\n');
            }

            lock (_lock)
            {
                File.AppendAllText(_traceFilePath, builder.ToString());
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }
}
