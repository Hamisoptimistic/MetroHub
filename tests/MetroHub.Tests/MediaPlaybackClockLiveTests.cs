using System.Diagnostics;
using MetroHub.Core.Media;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Live-broadcast classification: the signals that decide whether a seekbar should be shown at all.
/// </summary>
/// <remarks>
/// These signals used to be five independent predicates scattered through the widget, which is how a
/// single bad reading could latch a stream as "live" for the rest of the track and leave the seekbar
/// permanently dead. They now live in one ordered decision with a clear unlatch rule.
/// </remarks>
public class MediaPlaybackClockLiveTests
{
    private const string Track = "Artist|Title|Album";
    private static readonly long T0 = Stopwatch.GetTimestamp();

    private static long At(double seconds) => T0 + (long)(seconds * Stopwatch.Frequency);

    private static MediaObservation Snapshot(
        double positionSeconds = 0,
        bool isPlaying = true,
        double durationSeconds = 300,
        bool canSeek = true,
        bool isKnownNonLiveSource = false,
        bool hasLiveTitleKeyword = false,
        double observedAtSeconds = 0) => new()
        {
            Position = TimeSpan.FromSeconds(positionSeconds),
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Rate = 1.0,
            IsPlaying = isPlaying,
            CanSeek = canSeek,
            OsTimestamp = default,
            IsKnownNonLiveSource = isKnownNonLiveSource,
            HasLiveTitleKeyword = hasLiveTitleKeyword,
            TrackId = Track,
            ObservedAtTicks = At(observedAtSeconds)
        };

    [Fact]
    public void TitleKeyword_MarksBroadcast()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(hasLiveTitleKeyword: true));

        MediaPlaybackSample sample = clock.Sample(At(0));

        Assert.True(sample.IsLive);
        Assert.Equal(TimeSpan.Zero, sample.Position);
        Assert.Equal(TimeSpan.Zero, sample.Duration);
    }

    [Fact]
    public void ThirteenHourDuration_MarksBroadcast()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(durationSeconds: 13 * 3600));

        Assert.True(clock.Sample(At(0)).IsLive);
    }

    [Fact]
    public void NonSeekableWithDuration_MarksBroadcast()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(durationSeconds: 7200, canSeek: false));

        Assert.True(clock.Sample(At(0)).IsLive);
    }

    [Fact]
    public void ExpandingDuration_MarksBroadcast()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(durationSeconds: 100));
        Assert.False(clock.Sample(At(0)).IsLive);

        // A DVR window growing in real time while we watch.
        clock.Observe(Snapshot(positionSeconds: 5, durationSeconds: 102, observedAtSeconds: 1));

        Assert.True(clock.Sample(At(1)).IsLive);
    }

    [Fact]
    public void NonSeekableZeroDuration_BecomesBroadcastOnlyAfterQualifying()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(durationSeconds: 0, canSeek: false, observedAtSeconds: 0));

        // One reading is not enough: a transient zero must not declare a broadcast.
        Assert.False(clock.Sample(At(0)).IsLive);

        clock.Observe(Snapshot(durationSeconds: 0, canSeek: false, observedAtSeconds: 3));

        Assert.True(clock.Sample(At(3)).IsLive);
    }

    [Fact]
    public void Latch_ClearsOnceTheSourceProvesTheItemIsSeekable()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(hasLiveTitleKeyword: true, durationSeconds: 0, canSeek: false));
        Assert.True(clock.Sample(At(0)).IsLive);

        // The same source now reports a real, seekable duration with no live keyword.
        clock.Observe(Snapshot(hasLiveTitleKeyword: false, durationSeconds: 300, canSeek: true, observedAtSeconds: 2));

        MediaPlaybackSample sample = clock.Sample(At(2));

        Assert.False(sample.IsLive);
        Assert.True(sample.Duration > TimeSpan.Zero);
    }

    [Fact]
    public void DedicatedPlayers_AreNeverTreatedAsBroadcast()
    {
        var clock = new MediaPlaybackClock();

        // A local file on a dedicated player can never be a broadcast, whatever its title says.
        clock.Observe(Snapshot(hasLiveTitleKeyword: true, isKnownNonLiveSource: true));

        Assert.False(clock.Sample(At(0)).IsLive);
    }

    [Fact]
    public void Broadcast_ReportsStoppedSeekbarState()
    {
        var clock = new MediaPlaybackClock();
        clock.Observe(Snapshot(hasLiveTitleKeyword: true));

        MediaPlaybackSample sample = clock.Sample(At(5));

        Assert.True(sample.IsLive);
        Assert.False(sample.IsStalled);
        Assert.False(sample.NeedsRefresh);
        Assert.Equal(TimeSpan.Zero, sample.Position);
    }
}
