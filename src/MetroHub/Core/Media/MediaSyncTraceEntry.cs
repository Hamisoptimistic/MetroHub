namespace MetroHub.Core.Media;

/// <summary>
/// One line of a media-sync trace: a source snapshot paired with the position the clock produced from
/// it at the same instant.
/// </summary>
/// <remarks>
/// Recording input and output together makes a trace a complete, replayable scenario rather than a log.
/// A reported glitch can be captured once, committed under <c>tests/MetroHub.Tests/Traces</c>, and then
/// replayed through <see cref="MediaPlaybackClock"/> as a deterministic regression test — which is what
/// stops a fix for one glitch from silently reintroducing another.
/// </remarks>
public sealed record MediaSyncTraceEntry
{
    /// <summary>Milliseconds since the start of the trace, in the caller's monotonic basis.</summary>
    public double ObservedMs { get; init; }

    public string TrackId { get; init; } = string.Empty;

    /// <summary>Raw position the source reported, in seconds.</summary>
    public double SourcePositionSeconds { get; init; }

    public double SourceDurationSeconds { get; init; }

    public double Rate { get; init; } = 1.0;

    public bool IsPlaying { get; init; }

    public bool CanSeek { get; init; }

    public bool IsKnownNonLiveSource { get; init; }

    public bool HasLiveTitleKeyword { get; init; }

    /// <summary>Round-trip ("O") format, or null when the source omits <c>LastUpdatedTime</c>.</summary>
    public string? OsTimestampUtc { get; init; }

    /// <summary>Position the clock displayed for this snapshot.</summary>
    public double DisplayedPositionSeconds { get; init; }

    public bool DisplayedIsLive { get; init; }

    public bool DisplayedIsStalled { get; init; }

    public bool DisplayedIsSeekPending { get; init; }
}
