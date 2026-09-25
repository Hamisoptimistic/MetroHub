using System.Diagnostics;

namespace MetroHub.Core.Media;

/// <summary>
/// One position snapshot from a media source (Windows SMTC, or the WebNowPlaying adapter).
/// </summary>
/// <remarks>
/// <see cref="ObservedAtTicks"/> is a <see cref="Stopwatch"/> timestamp captured by the caller at the
/// instant the snapshot was received. The clock never reads a clock of its own, which is what makes it
/// deterministic: identical observations replayed with identical <c>now</c> values always produce
/// identical output, so every real-world glitch can be captured as a trace and pinned by a test.
/// </remarks>
public readonly record struct MediaObservation
{
    /// <summary>Raw position the source reported. Never pre-adjusted by the caller.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>Raw duration the source reported. Zero when the source omits or invalidates it.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Playback rate; values that are not finite and positive are normalised to 1.0.</summary>
    public double Rate { get; init; }

    public bool IsPlaying { get; init; }

    /// <summary>Whether the source advertises seeking. Disabling it is a live-stream signal.</summary>
    public bool CanSeek { get; init; }

    /// <summary>
    /// The source's own <c>LastUpdatedTime</c>, or <see cref="DateTimeOffset.MinValue"/> when the source
    /// omits it — which every Chromium-based player does.
    /// </summary>
    public DateTimeOffset OsTimestamp { get; init; }

    /// <summary>
    /// True for dedicated players that never stream (Spotify, VLC, MPC, Movies &amp; TV). Such sources
    /// suppress every live-stream heuristic so a local file is never mistaken for a broadcast.
    /// </summary>
    public bool IsKnownNonLiveSource { get; init; }

    /// <summary>True when the displayed title matched a live-broadcast keyword (LIVE, 24/7, ...).</summary>
    public bool HasLiveTitleKeyword { get; init; }

    /// <summary>Identifies the current track. A change resets all accumulated state.</summary>
    public string TrackId { get; init; }

    public long ObservedAtTicks { get; init; }
}

/// <summary>
/// Immutable result of sampling the clock at a single instant.
/// </summary>
/// <param name="NeedsRefresh">
/// The owner should issue exactly one authoritative source query (never a loop). Set while a seek
/// candidate is unconfirmed, or once the anchor is older than the freshness budget.
/// </param>
public readonly record struct MediaPlaybackSample(
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    bool IsLive,
    bool IsStalled,
    bool IsSeekPending,
    bool NeedsRefresh);

/// <summary>
/// The single authority for media playback position.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Position from Windows SMTC is a sparse, lossy, occasionally-wrong feed.
/// Chromium only began forwarding <c>MediaPosition</c> to SMTC in late 2022, it is driven by whatever the
/// web page passes to <c>navigator.mediaSession.setPositionState()</c>, it is delivered out of order on
/// arbitrary thread-pool threads, and it emits a transient 0:00 during seek, pause and ad transitions.
/// Enforcing correctness with one guard per bug report produced a rat's nest of interacting heuristics
/// that kept breaking each other — see <c>docs/MEDIA_SEEKBAR_ARCHITECTURE.md</c>.</para>
///
/// <para><b>The replacement.</b> Two states and two rules, mirroring Chromium's own
/// <c>media/base/time_delta_interpolator.h</c> and the <c>nowplaying</c> crate's <c>MediaClock</c>:</para>
/// <list type="number">
/// <item><description><b>Anchor + clock.</b> Keep the raw position the source last reported and the
/// monotonic instant it arrived. Position = anchor + (elapsed + staleness the sample already carried)
/// x rate, capped by the run-ahead budget so the bar can never creep past a stalled player.</description></item>
/// <item><description><b>Monotonic floor.</b> While playing, the displayed position never moves
/// backwards. Small backwards noise is absorbed by the floor instead of being rejected, so the anchor
/// stays fresh and the run-ahead budget stays honest. Only a <i>confirmed</i> large backwards jump —
/// a real rewind — may move the bar back.</description></item>
/// </list>
///
/// <para><b>Thread safety.</b> Not thread-safe by design; the owning ViewModel marshals to the UI thread.
/// No locks, no timers, no allocation. <see cref="Observe"/> mutates; <see cref="Sample"/> only reads
/// (plus the monotonic floor, which is idempotent for a given instant).</para>
/// </remarks>
public sealed class MediaPlaybackClock
{
    /// <summary>Backwards jump larger than this is treated as a seek candidate rather than jitter.</summary>
    public const double SeekThresholdSeconds = 2.0;

    /// <summary>
    /// Ceiling on how far the bar may run past the position the player actually reported. Only ever
    /// fully spent when the player stops advancing (buffering, or the connection dropping) while still
    /// claiming to be Playing; the bar then holds and reports <see cref="MediaPlaybackSample.IsStalled"/>.
    /// </summary>
    public const double MaxRunAheadSeconds = 5.0;

    /// <summary>How far an unconfirmed candidate may differ from the sample that confirms it.</summary>
    public const double SeekConfirmationToleranceSeconds = 2.0;

    /// <summary>A candidate seek this stale is dropped without ever moving the bar.</summary>
    public const double SeekConfirmationWindowSeconds = 3.0;

    /// <summary>Chromium 0:00 transient: near-zero position while we were well into a long track.</summary>
    public const double TransientZeroCeilingSeconds = 1.5;
    public const double TransientZeroFloorSeconds = 2.5;
    public const double TransientZeroMinDurationSeconds = 5.0;

    /// <summary>Uploads longer than this are always broadcasts (YouTube caps uploads at 12 hours).</summary>
    public const double BroadcastDurationCeilingHours = 12.0;

    /// <summary>A duration that grew by more than this is a DVR window extending in real time.</summary>
    public const double ExpandingDurationStepSeconds = 1.5;

    /// <summary>A non-seekable source reports zero duration for this long before it counts as live.</summary>
    public const double ZeroDurationLiveQualifySeconds = 2.5;

    /// <summary>
    /// While playing, ask the owner for an authoritative refresh once the anchor is older than this.
    /// Demand-driven, rate-limited and one-shot — never a polling loop. This is what stops a silent
    /// source (many players emit nothing for tens of seconds) from stalling a healthy seekbar.
    /// </summary>
    public const double AnchorFreshnessSeconds = 4.0;

    /// <summary>How long a local play/pause intent outranks a contradicting source snapshot.</summary>
    public const double TransportGraceSeconds = 1.5;

    /// <summary>Timestamps older than a day are not wall-clock; treat them as absent.</summary>
    private const double MaxPlausibleStalenessSeconds = 86400.0;

    private string _trackId = string.Empty;

    // Anchor: the raw position the source last reported, and the instant we received it.
    private bool _hasAnchor;
    private TimeSpan _anchorPosition;
    private long _anchorObservedTicks;
    private double _anchorCarriedSeconds;

    // Source flags; latest snapshot wins.
    private TimeSpan _duration;          // Sticky: a transient 0 never erases a known duration.
    private TimeSpan _previousDuration;
    private double _rate = 1.0;
    private bool _isPlaying;
    private bool _canSeek = true;

    // Monotonic floor: highest position displayed for the current track / seek epoch.
    private TimeSpan _playbackFloor;

    // Live latch, cleared deterministically (not permanently) once the source proves it is seekable.
    private bool _isLive;
    private long _zeroDurationFirstSeenTicks;

    // A large backwards jump is a candidate until a second agreeing sample confirms it.
    private bool _hasCandidateSeek;
    private TimeSpan _candidateSeekPosition;
    private long _candidateSeekTicks;

    // A seek this widget initiated: trusted immediately, no confirmation round.
    private bool _hasPendingSeek;
    private TimeSpan _pendingSeekTarget;

    // Local play/pause intent so the transport button never flickers while the OS catches up.
    private bool _hasTransportRequest;
    private bool _transportRequestedPlaying;
    private long _transportRequestExpiresTicks;

    /// <summary>Track identifier currently driving the clock.</summary>
    public string TrackId => _trackId;

    /// <summary>True while the current item is classified as a live broadcast.</summary>
    public bool IsLive => _isLive;

    /// <summary>Highest position displayed for the current track / seek epoch.</summary>
    public TimeSpan PlaybackFloor => _playbackFloor;

    /// <summary>Monotonic instant the current anchor was received.</summary>
    public long AnchorObservedTicks => _anchorObservedTicks;

    /// <summary>True while a large backwards jump is awaiting its confirming sample.</summary>
    public bool HasCandidateSeek => _hasCandidateSeek;

    /// <summary>True while a locally requested seek has not yet been echoed by the source.</summary>
    public bool HasPendingSeek => _hasPendingSeek;

    /// <summary>
    /// Clears every accumulated value. Called on context change so no ghost state from a previous track,
    /// session or transport source can corrupt the new one.
    /// </summary>
    public void Reset(string trackId)
    {
        _trackId = trackId ?? string.Empty;
        _hasAnchor = false;
        _anchorPosition = TimeSpan.Zero;
        _anchorObservedTicks = 0;
        _anchorCarriedSeconds = 0;
        _duration = TimeSpan.Zero;
        _previousDuration = TimeSpan.Zero;
        _rate = 1.0;
        _isPlaying = false;
        _canSeek = true;
        _playbackFloor = TimeSpan.Zero;
        _isLive = false;
        _zeroDurationFirstSeenTicks = 0;
        _hasCandidateSeek = false;
        _candidateSeekPosition = TimeSpan.Zero;
        _candidateSeekTicks = 0;
        _hasPendingSeek = false;
        _pendingSeekTarget = TimeSpan.Zero;
        _hasTransportRequest = false;
        _transportRequestedPlaying = false;
        _transportRequestExpiresTicks = 0;
    }

    /// <summary>
    /// Records a seek this widget asked for. The bar jumps immediately and the source's echo of the
    /// request is accepted without confirmation — this replaces the old suppress-window plus
    /// widget-echo-rejection pair of guards.
    /// </summary>
    public void NotifySeekRequested(TimeSpan target)
    {
        _hasPendingSeek = true;
        _pendingSeekTarget = target;

        // The bar now sits where the source has not reported yet: rebase the floor so the optimistic
        // jump survives, and set the anchor at target so interpolation continues smoothly from the seek position.
        _playbackFloor = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        _hasCandidateSeek = false;
        _hasAnchor = true;
        _anchorPosition = target;
        _anchorObservedTicks = Stopwatch.GetTimestamp();
        _anchorCarriedSeconds = 0;
    }

    /// <summary>
    /// Records a play/pause this widget asked for, so the transport reflects intent immediately while
    /// the OS processes the command asynchronously.
    /// </summary>
    public void NotifyTransportRequested(bool targetIsPlaying, long nowTicks)
    {
        _hasTransportRequest = true;
        _transportRequestedPlaying = targetIsPlaying;
        _transportRequestExpiresTicks = nowTicks + (long)(Stopwatch.Frequency * TransportGraceSeconds);
    }

    /// <summary>
    /// Feeds a source snapshot in. The only mutation entry point for position state.
    /// </summary>
    public void Observe(in MediaObservation observation)
    {
        long now = observation.ObservedAtTicks;

        // A different track means every accumulated value is from another context: drop all of it.
        if (!string.Equals(_trackId, observation.TrackId, StringComparison.Ordinal))
        {
            Reset(observation.TrackId);
        }

        // Duration is sticky: a transient zero must never erase a known duration, and the previous
        // value is what reveals a DVR window extending in real time.
        if (observation.Duration > TimeSpan.Zero)
        {
            _previousDuration = _duration;
            _duration = observation.Duration;
            _zeroDurationFirstSeenTicks = 0;
        }
        else if (_zeroDurationFirstSeenTicks == 0)
        {
            _zeroDurationFirstSeenTicks = now;
        }

        _rate = NormalizeRate(observation.Rate);
        _isPlaying = ResolveIsPlaying(observation.IsPlaying, now);
        _canSeek = observation.CanSeek;

        UpdateLiveClassification(in observation, now);

        if (_isLive)
        {
            // Nothing on a broadcast is worth anchoring: the bar is never displayed.
            _hasAnchor = false;
            _hasCandidateSeek = false;
            _hasPendingSeek = false;
            _playbackFloor = TimeSpan.Zero;
            return;
        }

        ApplyAnchor(in observation, now);
    }

    /// <summary>
    /// Feeds a transport-state-only update (play/pause, rate, seekability) that carries no position.
    /// Playback-state events arrive without a timeline, and letting them move the anchor would be
    /// precisely the cross-talk that produced the transient 0:00 resets this class exists to remove.
    /// </summary>
    public void ObserveTransport(bool isPlaying, double rate, bool canSeek, long nowTicks)
    {
        _rate = NormalizeRate(rate);
        _isPlaying = ResolveIsPlaying(isPlaying, nowTicks);
        _canSeek = canSeek;
    }

    /// <summary>
    /// Honours a local play/pause intent for a short grace window so the button does not bounce while
    /// the OS processes the command, then defers to the source unconditionally.
    /// </summary>
    private bool ResolveIsPlaying(bool reportedIsPlaying, long now)
    {
        if (!_hasTransportRequest)
        {
            return reportedIsPlaying;
        }

        if (now <= _transportRequestExpiresTicks)
        {
            if (reportedIsPlaying == _transportRequestedPlaying)
            {
                _hasTransportRequest = false;   // Source caught up: intent and truth now agree.
            }
            return _transportRequestedPlaying;
        }

        _hasTransportRequest = false;
        return reportedIsPlaying;
    }

    /// <summary>
    /// Classifies the item as a live broadcast using only signals the source itself reports, in a
    /// fixed priority order. The latch clears as soon as the source proves the item is seekable
    /// rather than staying stuck for the rest of the track.
    /// </summary>
    private void UpdateLiveClassification(in MediaObservation observation, long now)
    {
        // Dedicated players never broadcast, so no heuristic may fire on them at all.
        if (observation.IsKnownNonLiveSource)
        {
            _isLive = false;
            return;
        }

        if (_isLive)
        {
            bool provenRecorded = _duration > TimeSpan.Zero
                                  && observation.CanSeek
                                  && !observation.HasLiveTitleKeyword
                                  && !IsDurationExpanding();
            if (!provenRecorded)
            {
                return;   // Still live: keep the latch.
            }

            _isLive = false;
        }

        _isLive = ClassifyLive(in observation, now);
    }

    private bool ClassifyLive(in MediaObservation observation, long now)
    {
        if (observation.HasLiveTitleKeyword) return true;

        // YouTube caps uploads at 12 hours, so a duration at or beyond that is always a broadcast.
        if (_duration >= TimeSpan.FromHours(BroadcastDurationCeilingHours)) return true;

        // A DVR window that grows while we watch is extending in real time.
        if (observation.IsPlaying && IsDurationExpanding()) return true;

        if (observation.CanSeek) return false;

        // Non-seekable with a declared duration: Twitch's fixed DVR window.
        if (_duration > TimeSpan.Zero) return true;

        // Non-seekable with no duration at all: require the source to hold that state before believing it.
        return _zeroDurationFirstSeenTicks != 0 &&
               (double)(now - _zeroDurationFirstSeenTicks) / Stopwatch.Frequency >= ZeroDurationLiveQualifySeconds;
    }

    private bool IsDurationExpanding() =>
        _previousDuration > TimeSpan.Zero &&
        _duration > _previousDuration + TimeSpan.FromSeconds(ExpandingDurationStepSeconds);

    /// <summary>
    /// Decides whether a snapshot may move the bar, and where it re-anchors.
    /// </summary>
    private void ApplyAnchor(in MediaObservation observation, long now)
    {
        TimeSpan incoming = observation.Position;

        // A sample stamped slightly before it reached us is worth that much extra forward motion.
        // Charged against the same budget Sample() draws on, so it can never be counted twice.
        double carriedSeconds = 0;
        if (observation.IsPlaying && IsUsableTimestamp(observation.OsTimestamp))
        {
            double staleSeconds = (DateTimeOffset.UtcNow - observation.OsTimestamp).TotalSeconds;
            if (staleSeconds > 0 && staleSeconds < MaxPlausibleStalenessSeconds)
            {
                carriedSeconds = Math.Min(staleSeconds, MaxRunAheadSeconds);
                incoming += TimeSpan.FromSeconds(carriedSeconds * _rate);
            }
        }

        // A pending seek is resolved first: the echo may well be the very snapshot that re-establishes
        // the anchor, so this check must precede the no-anchor path or the pending flag would stick.
        if (_hasPendingSeek)
        {
            if (Math.Abs((incoming - _pendingSeekTarget).TotalSeconds) <= SeekConfirmationToleranceSeconds)
            {
                _hasPendingSeek = false;
                _playbackFloor = incoming < TimeSpan.Zero ? TimeSpan.Zero : incoming;
                AcceptAnchor(incoming, now, carriedSeconds);
                return;
            }

            // Drop stale pre-seek echo or 0:00 transient while the seek is pending (grace period up to 3.0s).
            bool seekExpired = (double)(now - _anchorObservedTicks) / Stopwatch.Frequency > 3.0;
            if (!seekExpired)
            {
                return;
            }
            _hasPendingSeek = false;
        }

        if (!_hasAnchor)
        {
            AcceptAnchor(incoming, now, carriedSeconds);
            return;
        }

        double deltaSeconds = (incoming - _anchorPosition).TotalSeconds;

        if (deltaSeconds > -SeekThresholdSeconds)
        {
            // Forward, or no more than a hair backwards. Always trust the newest anchor: the floor,
            // not sample rejection, is what keeps the displayed position monotonic.
            AcceptAnchor(incoming, now, carriedSeconds);
            return;
        }

        // A large backwards jump: either a genuine rewind or one of Chromium's transients (0:00 during
        // seek, pause and ad transitions). A confirmed candidate moves the bar; an unconfirmed one is
        // held back and reported through NeedsRefresh so the owner can settle it with a single query.
        if (_hasCandidateSeek)
        {
            double agreement = Math.Abs((incoming - _candidateSeekPosition).TotalSeconds);
            bool inWindow = (double)(now - _candidateSeekTicks) / Stopwatch.Frequency <= SeekConfirmationWindowSeconds;
            if (agreement <= SeekConfirmationToleranceSeconds && inWindow)
            {
                _hasCandidateSeek = false;
                _playbackFloor = incoming < TimeSpan.Zero ? TimeSpan.Zero : incoming;
                AcceptAnchor(incoming, now, carriedSeconds);
                return;
            }
        }

        _hasCandidateSeek = true;
        _candidateSeekPosition = incoming;
        _candidateSeekTicks = now;
    }

    private void AcceptAnchor(TimeSpan position, long now, double carriedSeconds)
    {
        _hasAnchor = true;
        _anchorPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _anchorObservedTicks = now;
        _anchorCarriedSeconds = carriedSeconds;
        _hasCandidateSeek = false;
    }

    /// <summary>
    /// Reads the interpolated position for a single instant. No source call, no allocation.
    /// </summary>
    public MediaPlaybackSample Sample(long nowTicks)
    {
        if (_isLive)
        {
            return new MediaPlaybackSample(TimeSpan.Zero, TimeSpan.Zero, _isPlaying, true, false, false, false);
        }

        if (!_hasAnchor)
        {
            return new MediaPlaybackSample(_playbackFloor, _duration, _isPlaying, false, false, _hasPendingSeek, false);
        }

        double elapsedSeconds = (double)(nowTicks - _anchorObservedTicks) / Stopwatch.Frequency;
        if (elapsedSeconds < 0) elapsedSeconds = 0;

        double budgetUsed = _anchorCarriedSeconds + elapsedSeconds;
        double runAheadSeconds = Math.Min(budgetUsed, MaxRunAheadSeconds);

        TimeSpan position = _isPlaying
            ? _anchorPosition + TimeSpan.FromSeconds(runAheadSeconds * _rate)
            : _anchorPosition;

        if (_duration > TimeSpan.Zero && position > _duration)
        {
            position = _duration;
        }

        // Budget spent: the player stopped advancing while still claiming to be Playing. runAheadSeconds
        // is already capped at the budget, so the bar converges on anchor + budget and holds there —
        // never creeping further away from the truth while the source stays silent.
        bool isStalled = _isPlaying && budgetUsed >= MaxRunAheadSeconds;

        // Monotonic floor: the only state Sample advances, and the reason a small backwards source
        // sample is physically incapable of twitching the bar backwards. Idempotent for a given
        // instant; reset by track changes, confirmed rewinds and requested seeks.
        if (_isPlaying && position > _playbackFloor)
        {
            _playbackFloor = position;
        }
        else if (position < _playbackFloor)
        {
            position = _playbackFloor;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        bool needsRefresh = _hasCandidateSeek || (_isPlaying && elapsedSeconds >= AnchorFreshnessSeconds);

        return new MediaPlaybackSample(position, _duration, _isPlaying, false, isStalled, _hasPendingSeek, needsRefresh);
    }

    /// <summary>Rates that are not finite and positive would corrupt every subsequent position.</summary>
    private static double NormalizeRate(double rate) => double.IsFinite(rate) && rate > 0 ? rate : 1.0;

    /// <summary>Players that omit <c>LastUpdatedTime</c> report <see cref="DateTimeOffset.MinValue"/>.</summary>
    private static bool IsUsableTimestamp(DateTimeOffset timestamp) =>
        timestamp > DateTimeOffset.MinValue && timestamp.Year > 2000;
}
