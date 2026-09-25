using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MetroHub.Core.Media;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Replays captured media-sync traces through <see cref="MediaPlaybackClock"/> and asserts the
/// invariants that made glitches regress-proof.
/// </summary>
/// <remarks>
/// A trace records what the source reported and when; the harness re-samples the clock between
/// observations at 50 ms steps as well as on them, because that is exactly where a seekbar twitches.
/// When a user reports a glitch, capture it with <c>MediaSyncTraceLogger</c>, drop the file into
/// <c>Traces</c>, and assert on it here — the scenario then stays fixed while later changes are made.
/// </remarks>
public sealed class MediaSyncTraceReplayTests
{
    private const double IntervalStepMs = 50.0;
    private const double EpsilonSeconds = 0.01;
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private sealed record ReplayStep(
        double ObservedMs,
        double DisplayedSeconds,
        double? SourceSeconds,
        MediaPlaybackSample Sample);

    // ---- Harness ------------------------------------------------------------------------------

    private static List<MediaSyncTraceEntry> LoadTrace(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Traces", fileName);
        Assert.True(File.Exists(path), $"Missing trace fixture: {path}");

        var entries = new List<MediaSyncTraceEntry>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            MediaSyncTraceEntry? entry = JsonSerializer.Deserialize<MediaSyncTraceEntry>(line);
            Assert.NotNull(entry);
            entries.Add(entry!);
        }

        Assert.True(entries.Count >= 2, "A replayable trace needs at least two snapshots.");
        return entries;
    }

    private static long TicksAt(double observedMs, long t0) => t0 + (long)(observedMs * TicksPerMs);

    private static void ObserveAt(MediaPlaybackClock clock, MediaSyncTraceEntry entry, long t0)
    {
        clock.Observe(new MediaObservation
        {
            Position = TimeSpan.FromSeconds(entry.SourcePositionSeconds),
            Duration = TimeSpan.FromSeconds(entry.SourceDurationSeconds),
            Rate = entry.Rate,
            IsPlaying = entry.IsPlaying,
            CanSeek = entry.CanSeek,
            OsTimestamp = string.IsNullOrEmpty(entry.OsTimestampUtc)
                ? DateTimeOffset.MinValue
                : DateTimeOffset.Parse(entry.OsTimestampUtc),
            IsKnownNonLiveSource = entry.IsKnownNonLiveSource,
            HasLiveTitleKeyword = entry.HasLiveTitleKeyword,
            TrackId = entry.TrackId,
            ObservedAtTicks = TicksAt(entry.ObservedMs, t0)
        });
    }

    private static List<ReplayStep> Replay(string fileName)
    {
        List<MediaSyncTraceEntry> entries = LoadTrace(fileName);
        var clock = new MediaPlaybackClock();
        long t0 = Stopwatch.GetTimestamp();
        var steps = new List<ReplayStep>();

        ObserveAt(clock, entries[0], t0);
        steps.Add(new ReplayStep(
            entries[0].ObservedMs,
            clock.Sample(TicksAt(entries[0].ObservedMs, t0)).Position.TotalSeconds,
            entries[0].SourcePositionSeconds,
            clock.Sample(TicksAt(entries[0].ObservedMs, t0))));

        for (int i = 1; i < entries.Count; i++)
        {
            MediaSyncTraceEntry previous = entries[i - 1];
            MediaSyncTraceEntry current = entries[i];

            // Sample the gap between snapshots: this is the render-loop path the old timer never covered.
            for (double ms = previous.ObservedMs + IntervalStepMs; ms < current.ObservedMs; ms += IntervalStepMs)
            {
                MediaPlaybackSample mid = clock.Sample(TicksAt(ms, t0));
                steps.Add(new ReplayStep(ms, mid.Position.TotalSeconds, null, mid));
            }

            ObserveAt(clock, current, t0);
            MediaPlaybackSample atEntry = clock.Sample(TicksAt(current.ObservedMs, t0));
            steps.Add(new ReplayStep(
                current.ObservedMs,
                atEntry.Position.TotalSeconds,
                current.SourcePositionSeconds,
                atEntry));
        }

        return steps;
    }

    private static List<ReplayStep> EntriesOnly(List<ReplayStep> steps) =>
        steps.Where(s => s.SourceSeconds.HasValue).ToList();

    // ---- Invariants that hold for every captured trace ---------------------------------------

    [Theory]
    [InlineData("chromium-seek-and-rewind.jsonl")]
    [InlineData("pause-resume.jsonl")]
    [InlineData("buffering-stall.jsonl")]
    [InlineData("live-dvr-window.jsonl")]
    public void DisplayedPosition_OnlyMovesBackwards_WhenTheSourceConfirmsTheRewind(string trace)
    {
        List<ReplayStep> steps = Replay(trace);
        List<ReplayStep> entries = EntriesOnly(steps);

        for (int i = 1; i < steps.Count; i++)
        {
            ReplayStep previous = steps[i - 1];
            ReplayStep current = steps[i];

            if (current.DisplayedSeconds >= previous.DisplayedSeconds - EpsilonSeconds) continue;

            string where = $"at {current.ObservedMs} ms in {trace}";

            // A backwards move may only originate from a snapshot: between snapshots the monotonic
            // floor forbids it outright, which is precisely the twitch users reported.
            Assert.True(current.SourceSeconds.HasValue, $"Bar moved backwards between snapshots {where}");

            // And the source must go on to agree with the new position — a transient 0:00 never does.
            int index = entries.FindIndex(e => e.ObservedMs == current.ObservedMs);
            Assert.True(index >= 0 && index + 1 < entries.Count,
                $"Backwards move on the final snapshot has nothing to confirm it {where}");

            double agreement = Math.Abs(entries[index + 1].SourceSeconds!.Value - current.SourceSeconds!.Value);
            Assert.True(agreement <= MediaPlaybackClock.SeekConfirmationToleranceSeconds + EpsilonSeconds,
                $"Source never confirmed the rewind (Δ{agreement:0.00}s) {where}");
        }
    }

    [Theory]
    [InlineData("chromium-seek-and-rewind.jsonl")]
    [InlineData("pause-resume.jsonl")]
    [InlineData("buffering-stall.jsonl")]
    [InlineData("live-dvr-window.jsonl")]
    public void DisplayedPosition_NeverDecreasesBetweenSnapshots(string trace)
    {
        ReplayStep? previous = null;

        foreach (ReplayStep step in Replay(trace))
        {
            bool bothBetweenSnapshots = step.SourceSeconds is null && previous?.SourceSeconds is null;
            if (bothBetweenSnapshots)
            {
                Assert.True(step.DisplayedSeconds >= previous!.DisplayedSeconds - EpsilonSeconds,
                    $"Bar moved backwards mid-interval at {step.ObservedMs} ms in {trace} " +
                    $"({previous!.DisplayedSeconds:0.00} -> {step.DisplayedSeconds:0.00})");
            }

            previous = step;
        }
    }

    [Theory]
    [InlineData("chromium-seek-and-rewind.jsonl")]
    [InlineData("pause-resume.jsonl")]
    [InlineData("buffering-stall.jsonl")]
    [InlineData("live-dvr-window.jsonl")]
    public void DisplayedPosition_NeverExceedsTheDeclaredDuration(string trace)
    {
        foreach (ReplayStep step in Replay(trace))
        {
            if (step.Sample.IsLive || step.Sample.Duration <= TimeSpan.Zero) continue;

            Assert.True(step.DisplayedSeconds <= step.Sample.Duration.TotalSeconds + EpsilonSeconds,
                $"Bar at {step.DisplayedSeconds:0.00}s overruns duration {step.Sample.Duration.TotalSeconds:0.00}s " +
                $"at {step.ObservedMs} ms in {trace}");
        }
    }

    [Theory]
    [InlineData("chromium-seek-and-rewind.jsonl")]
    [InlineData("pause-resume.jsonl")]
    [InlineData("buffering-stall.jsonl")]
    [InlineData("live-dvr-window.jsonl")]
    public void IsStalled_IsNeverTrueWhileTheSourceIsStillAdvancing(string trace)
    {
        List<ReplayStep> entries = EntriesOnly(Replay(trace));

        for (int i = 1; i < entries.Count; i++)
        {
            double advance = entries[i].SourceSeconds!.Value - entries[i - 1].SourceSeconds!.Value;
            if (advance < 1.0) continue;

            Assert.False(entries[i].Sample.IsStalled,
                $"Source advanced {advance:0.00}s yet the bar reported a stall at {entries[i].ObservedMs} ms in {trace}");
        }
    }

    // ---- Scenario outcomes: what the user actually sees ---------------------------------------

    [Fact]
    public void ChromiumTrace_TransientZero_IsNeverDisplayed_AndRewindStillLands()
    {
        List<ReplayStep> steps = Replay("chromium-seek-and-rewind.jsonl");

        // Chromium reports 0:00 mid-track while seeking; the bar must stay deep into the track.
        ReplayStep transientZero = steps.Single(s => s.ObservedMs == 2050);
        Assert.True(transientZero.DisplayedSeconds >= 100,
            $"Transient 0:00 reached the display (bar at {transientZero.DisplayedSeconds:0.00}s).");

        // Nothing before the confirmed rewind may fall back towards the start of the track.
        foreach (ReplayStep step in steps.Where(s => s.ObservedMs <= 3100))
        {
            Assert.True(step.DisplayedSeconds >= 50,
                $"Bar collapsed to {step.DisplayedSeconds:0.00}s at {step.ObservedMs} ms before any confirmed rewind.");
        }

        // The genuine rewind (source moves to 60s and stays there) does land.
        ReplayStep last = steps[^1];
        Assert.Equal(62.1, last.DisplayedSeconds, 1);
        Assert.False(last.Sample.IsLive);
    }

    [Fact]
    public void PauseResumeTrace_BarFreezesWhilePaused_ThenResumesAdvancing()
    {
        List<ReplayStep> steps = Replay("pause-resume.jsonl");

        // The pause is reported at 2100 ms and the resume at 5200 ms.
        List<ReplayStep> paused = steps.Where(s => s.ObservedMs >= 2100 && s.ObservedMs <= 5100).ToList();
        Assert.True(paused.Count > 50, "Pause window was not sampled densely enough to prove a freeze.");

        double frozenAt = paused[0].DisplayedSeconds;
        foreach (ReplayStep step in paused)
        {
            Assert.True(Math.Abs(step.DisplayedSeconds - frozenAt) < 0.5,
                $"Bar drifted {step.DisplayedSeconds:0.00}s -> {frozenAt:0.00}s while paused (at {step.ObservedMs} ms).");
        }

        Assert.True(steps[^1].DisplayedSeconds > 52.5,
            $"Bar did not resume advancing (at {steps[^1].DisplayedSeconds:0.00}s).");
        Assert.False(steps[^1].Sample.IsStalled);
    }

    [Fact]
    public void BufferingTrace_StallsOnlyDuringSilence_ThenRecovers()
    {
        List<ReplayStep> steps = Replay("buffering-stall.jsonl");

        // The source goes silent after 2000 ms: the bar may run ahead for its budget, then must hold
        // and say so rather than pretending to still be playing.
        List<ReplayStep> silent = steps.Where(s => s.ObservedMs >= 7000 && s.ObservedMs < 12000).ToList();
        Assert.True(silent.Count > 0, "Silence window was never sampled.");
        Assert.Contains(silent, s => s.Sample.IsStalled);

        foreach (ReplayStep step in silent)
        {
            Assert.True(step.DisplayedSeconds <= 17.01,
                $"Bar ran {step.DisplayedSeconds:0.00}s — past the 5s run-ahead budget during silence.");
        }

        // Demand-driven recovery: the clock asks for exactly one authoritative refresh, it never loops.
        Assert.Contains(steps, s => s.ObservedMs >= 6050 && s.ObservedMs < 12000 && s.Sample.NeedsRefresh);

        // The source speaks again and the stall clears.
        ReplayStep last = steps[^1];
        Assert.False(last.Sample.IsStalled);
        Assert.False(last.Sample.NeedsRefresh);
    }

    [Fact]
    public void LiveDvrTrace_HoldsAtZeroWhileLive_ThenClearsWhenProvenRecorded()
    {
        List<MediaSyncTraceEntry> entries = LoadTrace("live-dvr-window.jsonl");
        List<ReplayStep> steps = Replay("live-dvr-window.jsonl");

        foreach (MediaSyncTraceEntry entry in entries.Take(3))
        {
            ReplayStep step = steps.Single(s => s.ObservedMs == entry.ObservedMs && s.SourceSeconds.HasValue);
            Assert.True(step.Sample.IsLive, $"Not classified live at {entry.ObservedMs} ms.");
            Assert.Equal(0, step.DisplayedSeconds, 3);
        }

        // The latch must not stick: once duration is recorded and seekable with no LIVE keyword, it clears.
        ReplayStep lastStep = steps.Single(s => s.ObservedMs == entries[^1].ObservedMs && s.SourceSeconds.HasValue);
        Assert.False(lastStep.Sample.IsLive, "Live latch failed to clear once the item proved recorded.");
        Assert.True(lastStep.DisplayedSeconds > 0, "Bar did not resume a position after leaving live mode.");
    }
}
