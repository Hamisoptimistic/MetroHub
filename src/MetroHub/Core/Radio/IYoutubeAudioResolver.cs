using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Radio;

/// <summary>
/// Lightweight YouTube search hit. Decoupled from YoutubeExplode types for testability.
/// </summary>
public sealed record YoutubeSearchHit(
    string VideoId,
    string Title,
    string Channel,
    string? ThumbnailUrl,
    TimeSpan? Duration)
{
    /// <summary>"3:24" style duration, empty when unknown (bindable without a converter).</summary>
    public string DurationText => Duration.HasValue
        ? $"{(int)Duration.Value.TotalMinutes}:{Duration.Value.Seconds:D2}"
        : string.Empty;
}

/// <summary>
/// Resolved direct audio stream URL. URLs expire within hours: resolve-then-play immediately,
/// never persist.
/// </summary>
public sealed record YoutubeAudioStream(
    string Url,
    string Codec,
    int BitrateKbps);

/// <summary>
/// Contract for YouTube search + audio URL resolution. Implemented by
/// <see cref="YoutubeAudioResolver"/>; faked in unit tests.
/// </summary>
public interface IYoutubeAudioResolver
{
    /// <summary>Top-N search hits for a query. Empty list when offline or on failure.</summary>
    Task<IReadOnlyList<YoutubeSearchHit>> SearchAsync(string query, int topN = 8, CancellationToken ct = default);

    /// <summary>
    /// Best direct audio URL for a video: Opus first (needs bassopus), AAC/MP4 fallback.
    /// Null when unresolvable (deleted, blocked, cipher breakage) — never throws for those.
    /// </summary>
    Task<YoutubeAudioStream?> ResolveAudioUrlAsync(string videoId, CancellationToken ct = default);

    /// <summary>
    /// AAC/MP4-only variant, used when the Opus pick fails to open in BASS
    /// (missing opus plugin). Null when no AAC stream exists.
    /// </summary>
    Task<YoutubeAudioStream?> ResolveAacFallbackAsync(string videoId, CancellationToken ct = default);
}
