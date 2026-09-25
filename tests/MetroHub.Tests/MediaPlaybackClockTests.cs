using System.Diagnostics;
using MetroHub.Core.Media;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Behavioural contract for <see cref="MediaPlaybackClock"/>.
/// </summary>
/// <remarks>
/// Every test drives the clock through explicit <c>nowTicks</c> values, so nothing here sleeps and
/// nothing depends on wall-clock timing. That determinism is the point: these are the tests that make
/// a fix stick, because a future change that reintroduces a glitch fails here instead of in the hub.
/// </remarks>
public class MediaPlaybackClockTests
{
    private const string Track = "Artist|Title|Album";
    private static readonly long T0 = Stopwatch.GetTimestamp();

    private static long At(double seconds) => T0 + (long)(seconds * Stopwatch.Frequency);

    /// <summary>An observation with no OS timestamp, i.e. what every Chromium-based player sends.</summary>
    private static MediaObservation Snapshot(
        double positionSeconds,
        bool isPlaying = true,
        double durationSeconds = 300,
        bool canSeek = true,
        string trackId = Track,
        double rate = 1.0,
        bool isKnownNonLiveSource = false,
        bool hasLiveTitleKeyword = false,
        double observedAtSeconds = 0) => new()
        {
            Position = TimeSpan.FromSeconds(positionSeconds),
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Rate = rate,
            IsPlaying = isPlaying,
            CanSeek = canSeek,
            OsTimestamp = default,
            IsKnownNonLiveSource = isKnownNonLiveSource,
            HasLiveTitleKeyword = hasLiveTitleKeyword,
            TrackId = trackId,
            ObservedAtTicks = At(observedAtSeconds)
        };

    [Fact]
    public void Playing_PositionAdvancesSmoothly()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));

        MediaPlaybackSample sample = clock.Sample(At(0.25));

        Assert.Equal(60.25, sample.Position.TotalSeconds, 3);
        Assert.True(sample.IsPlaying);
        Assert.False(sample.IsStalled);
    }

    [Fact]
    public void PlaybackRate_IsApplied()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60, rate: 1.5));

        Assert.Equal(60.3, clock.Sample(At(0.2)).Position.TotalSeconds, 3);
    }

    [Fact]
    public void Paused_PositionIsFrozen()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(90, isPlaying: false));

        MediaPlaybackSample sample = clock.Sample(At(30));

        Assert.Equal(90, sample.Position.TotalSeconds, 3);
        Assert.False(sample.IsPlaying);
        Assert.False(sample.IsStalled);
    }

    [Fact]
    public void ForwardSeek_ReplacesTheAnchor()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));
        clock.Observe(Snapshot(280, observedAtSeconds: 1));

        Assert.Equal(280.25, clock.Sample(At(1.25)).Position.TotalSeconds, 3);
    }

    [Fact]
    public void BackwardSeek_RequiresConfirmation_ThenMovesBack()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(200));

        // A single large backwards jump only becomes a candidate: the bar must not move yet.
        clock.Observe(Snapshot(20, observedAtSeconds: 0.1));
        Assert.True(clock.HasCandidateSeek);
        Assert.True(clock.Sample(At(0.2)).Position.TotalSeconds > 150);

        // A second agreeing sample confirms the rewind.
        clock.Observe(Snapshot(20.2, observedAtSeconds: 0.3));

        Assert.False(clock.HasCandidateSeek);
        Assert.Equal(20.2, clock.Sample(At(0.3)).Position.TotalSeconds, 2);
    }

    [Fact]
    public void TransientZero_DoesNotMoveTheBar()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(260));

        // Chromium's 0:00 blip during a seek or ad transition, never confirmed by a second sample.
        clock.Observe(Snapshot(0, observedAtSeconds: 0.1));
        clock.Observe(Snapshot(260.2, observedAtSeconds: 0.2));

        MediaPlaybackSample sample = clock.Sample(At(0.2));

        Assert.True(sample.Position.TotalSeconds > 259);
        Assert.False(clock.HasCandidateSeek);
    }

    [Fact]
    public void SmallBackwardSample_NeverMovesTheBarBackwards()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(100));

        // The bar has run a second ahead of the source through interpolation...
        Assert.Equal(102, clock.Sample(At(2)).Position.TotalSeconds, 2);

        // ...so a stale 101.5s sample may not drag it backwards.
        clock.Observe(Snapshot(101.5, observedAtSeconds: 2));

        Assert.Equal(102, clock.Sample(At(2)).Position.TotalSeconds, 2);
    }

    [Fact]
    public void RequestedSeek_IsAppliedWithoutConfirmation()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(30));

        clock.NotifySeekRequested(TimeSpan.FromSeconds(250));
        Assert.True(clock.HasPendingSeek);

        // The source echoes our own request: trusted immediately, no candidate round.
        clock.Observe(Snapshot(250, observedAtSeconds: 0.05));

        Assert.False(clock.HasPendingSeek);
        Assert.Equal(250, clock.Sample(At(0.05)).Position.TotalSeconds, 2);
    }

    [Fact]
    public void RequestedSeek_RejectsStalePreSeekEchoAndZeroTransient_BeforeConfirmingTarget()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));

        // Seek forward to 180s
        clock.NotifySeekRequested(TimeSpan.FromSeconds(180));
        Assert.True(clock.HasPendingSeek);
        Assert.True(clock.Sample(At(0.1)).Position.TotalSeconds >= 180.0);

        // Pre-seek echo (old position 60s) arrives: must be dropped
        clock.Observe(Snapshot(60, observedAtSeconds: 0.1));
        Assert.True(clock.HasPendingSeek);
        Assert.True(clock.Sample(At(0.15)).Position.TotalSeconds >= 180.0);

        // 0:00 transient arrives during buffering: must be dropped
        clock.Observe(Snapshot(0, observedAtSeconds: 0.2));
        Assert.True(clock.HasPendingSeek);
        Assert.True(clock.Sample(At(0.25)).Position.TotalSeconds >= 180.0);

        // Player finally reaches target (180.5s): accepted
        clock.Observe(Snapshot(180.5, observedAtSeconds: 0.5));
        Assert.False(clock.HasPendingSeek);
        Assert.Equal(180.5, clock.Sample(At(0.5)).Position.TotalSeconds, 2);
    }

    [Fact]
    public void StalledPlayer_HoldsTheBarAndReportsStalled()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));

        MediaPlaybackSample sample = clock.Sample(At(MediaPlaybackClock.MaxRunAheadSeconds + 1));

        Assert.True(sample.IsStalled);
        Assert.Equal(60 + MediaPlaybackClock.MaxRunAheadSeconds, sample.Position.TotalSeconds, 2);
    }

    [Fact]
    public void RealProgress_ClearsTheStall()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));
        Assert.True(clock.Sample(At(6)).IsStalled);

        clock.Observe(Snapshot(66, observedAtSeconds: 6));

        Assert.False(clock.Sample(At(6)).IsStalled);
    }

    [Fact]
    public void StaleAnchor_RequestsRefresh()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));

        Assert.False(clock.Sample(At(1)).NeedsRefresh);
        Assert.True(clock.Sample(At(MediaPlaybackClock.AnchorFreshnessSeconds + 0.1)).NeedsRefresh);
    }

    [Fact]
    public void UnconfirmedCandidate_RequestsRefresh()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(200));
        clock.Observe(Snapshot(10, observedAtSeconds: 0.1));

        Assert.True(clock.Sample(At(0.2)).NeedsRefresh);
    }

    [Fact]
    public void TransportRequest_OutranksContradictingSourceDuringGraceWindow()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(60));

        clock.NotifyTransportRequested(targetIsPlaying: false, nowTicks: At(1));

        // The OS has not processed the pause yet and still reports Playing.
        clock.Observe(Snapshot(61, isPlaying: true, observedAtSeconds: 1.1));

        Assert.False(clock.Sample(At(1.2)).IsPlaying);
    }

    [Fact]
    public void Position_IsClampedToDuration()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(299, durationSeconds: 300));

        Assert.Equal(300, clock.Sample(At(10)).Position.TotalSeconds, 2);
    }

    [Fact]
    public void InvalidRate_FallsBackToRealTimePlayback()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(500, durationSeconds: 3600, rate: double.NaN));

        // A non-finite rate must not freeze the bar or poison every later position: the session says
        // it is Playing, so real-time advance is the only sane reading.
        MediaPlaybackSample sample = clock.Sample(At(1));

        Assert.Equal(501, sample.Position.TotalSeconds, 2);
        Assert.False(double.IsNaN(sample.Position.TotalSeconds));
    }

    [Fact]
    public void ZeroRate_FallsBackToRealTimePlayback()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(500, durationSeconds: 3600, rate: 0.0));

        // A zero rate would leave a Playing session's bar dead in the water, so it is normalised too.
        Assert.Equal(501, clock.Sample(At(1)).Position.TotalSeconds, 2);
    }

    [Fact]
    public void NonPositiveRate_IsNormalisedForward()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(500, durationSeconds: 3600, rate: -1.0));

        MediaPlaybackSample sample = clock.Sample(At(1));

        Assert.Equal(501, sample.Position.TotalSeconds, 2);
        Assert.True(sample.Position >= TimeSpan.Zero);
    }

    [Fact]
    public void TrackChange_DiscardsAllPreviousState()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(200, trackId: "A|One|X"));
        Assert.True(clock.Sample(At(0)).Position.TotalSeconds > 199);

        // The new track starts at 0:00 and must not inherit the previous track's floor.
        clock.Observe(Snapshot(0, trackId: "B|Two|Y", observedAtSeconds: 5));

        MediaPlaybackSample sample = clock.Sample(At(5));

        Assert.Equal(0, sample.Position.TotalSeconds, 2);
        Assert.Equal("B|Two|Y", clock.TrackId);
    }
}
